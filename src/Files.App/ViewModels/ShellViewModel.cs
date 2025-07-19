// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.SizeProvider;
using Files.App.Services.Thumbnails;
using Files.Shared.Helpers;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Vanara.Windows.Shell;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using static Files.App.Helpers.Win32PInvoke;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using FileAttributes = System.IO.FileAttributes;
using ByteSize = ByteSizeLib.ByteSize;
using Windows.Win32.System.SystemServices;

namespace Files.App.ViewModels
{
	/// <summary>
	/// Represents view model of <see cref="IShellPage"/>.
	/// </summary>
	public sealed partial class ShellViewModel : ObservableObject, IDisposable
	{
		private readonly SemaphoreSlim enumFolderSemaphore;
		private readonly SemaphoreSlim getFileOrFolderSemaphore;
		private readonly SemaphoreSlim bulkOperationSemaphore;
		private readonly SemaphoreSlim loadThumbnailSemaphore;
		private readonly ConcurrentQueue<(uint Action, string FileName)> operationQueue;
		private readonly ConcurrentQueue<uint> gitChangesQueue;
		private readonly ConcurrentDictionary<string, bool> itemLoadQueue;
		private readonly AsyncManualResetEvent operationEvent;
		private readonly AsyncManualResetEvent gitChangedEvent;
		private DispatcherQueue? dispatcherQueue;
		private readonly JsonElement defaultJson;
		private readonly string folderTypeTextLocalized;

		private Task? aProcessQueueAction;
		private Task? gitProcessQueueAction;

		// Files and folders list for manipulating
		private ConcurrentCollection<ListedItem> filesAndFolders;
		private readonly IWindowsIniService WindowsIniService;
		private readonly IWindowsJumpListService jumpListService;
		private readonly IDialogService dialogService;
		private IUserSettingsService UserSettingsService { get; }
		private readonly INetworkService NetworkService;
		private readonly IFileTagsSettingsService fileTagsSettingsService;
		private readonly ISizeProvider folderSizeProvider;
			private readonly IStorageCacheService fileListCache;
	private readonly IWindowsSecurityService WindowsSecurityService;
	private readonly IStorageTrashBinService StorageTrashBinService;
	private readonly Services.Thumbnails.IViewportThumbnailLoaderService? viewportThumbnailLoader;

		// Only used for Binding and ApplyFilesAndFoldersChangesAsync, don't manipulate on this!
		public BulkConcurrentObservableCollection<ListedItem> FilesAndFolders { get; } = new();

		private LayoutPreferencesManager folderSettings;

		private ListedItem? currentFolder;
		public ListedItem? CurrentFolder
		{
			get => currentFolder;
			private set => SetProperty(ref currentFolder, value);
		}

		private string? _SolutionFilePath;
		public string? SolutionFilePath
		{
			get => _SolutionFilePath;
			private set => SetProperty(ref _SolutionFilePath, value);
		}

		private ImageSource? _FolderBackgroundImageSource;
		public ImageSource? FolderBackgroundImageSource
		{
			get => _FolderBackgroundImageSource;
			private set => SetProperty(ref _FolderBackgroundImageSource, value);
		}

		private float _FolderBackgroundImageOpacity = 1f;
		public float FolderBackgroundImageOpacity
		{
			get => _FolderBackgroundImageOpacity;
			private set => SetProperty(ref _FolderBackgroundImageOpacity, value);
		}

		private Stretch _FolderBackgroundImageFit = Stretch.UniformToFill;
		public Stretch FolderBackgroundImageFit
		{
			get => _FolderBackgroundImageFit;
			private set => SetProperty(ref _FolderBackgroundImageFit, value);
		}

		private VerticalAlignment _FolderBackgroundImageVerticalAlignment = VerticalAlignment.Center;
		public VerticalAlignment FolderBackgroundImageVerticalAlignment
		{
			get => _FolderBackgroundImageVerticalAlignment;
			private set => SetProperty(ref _FolderBackgroundImageVerticalAlignment, value);
		}

		private HorizontalAlignment _FolderBackgroundImageHorizontalAlignment = HorizontalAlignment.Center;
		public HorizontalAlignment FolderBackgroundImageHorizontalAlignment
		{
			get => _FolderBackgroundImageHorizontalAlignment;
			private set => SetProperty(ref _FolderBackgroundImageHorizontalAlignment, value);
		}

		private GitProperties _EnabledGitProperties;
		public GitProperties EnabledGitProperties
		{
			get => _EnabledGitProperties;
			set
			{
				if (SetProperty(ref _EnabledGitProperties, value) && value is not GitProperties.None)
				{
					var gitPropertiesTask = Task.Run(async () =>
					{
						try
						{
							foreach (var item in filesAndFolders)
							{
								if (item is IGitItem gitItem &&
									(!gitItem.StatusPropertiesInitialized && value is GitProperties.All or GitProperties.Status
									|| !gitItem.CommitPropertiesInitialized && value is GitProperties.All or GitProperties.Commit))
								{
									await LoadGitPropertiesAsync(gitItem).ConfigureAwait(false);
								}
							}
						}
						catch (Exception ex)
						{
							App.Logger?.LogError(ex, "Error loading git properties");
						}
					});

					// Handle task exceptions properly
					_ = gitPropertiesTask.ContinueWith(task =>
					{
						if (task.IsFaulted)
						{
							App.Logger?.LogError(task.Exception, "Unhandled error in git properties loading task");
						}
					}, TaskScheduler.Default);
				}
			}
		}

		public CollectionViewSource viewSource;

		private FileSystemWatcher watcher;

		private static BitmapImage shieldIcon;

		private CancellationTokenSource addFilesCTS;
		private CancellationTokenSource semaphoreCTS;
		private CancellationTokenSource loadPropsCTS;
		private CancellationTokenSource watcherCTS;
		private CancellationTokenSource searchCTS;
		private CancellationTokenSource updateTagGroupCTS;
		
		// Debouncing for ApplyFilesAndFoldersChangesAsync
		private DispatcherQueueTimer? applyChangesDebounceTimer;
		private readonly SemaphoreSlim applyChangesSemaphore = new(1, 1);
		private CancellationTokenSource applyChangesCTS = new();

		static ShellViewModel()
		{
			try
			{
				App.Logger?.LogInformation("ShellViewModel static constructor started");
				
				// Test if all required services are available
				var testServices = new[]
				{
					(nameof(IWindowsIniService), typeof(IWindowsIniService)),
					(nameof(IWindowsJumpListService), typeof(IWindowsJumpListService)),
					(nameof(IDialogService), typeof(IDialogService)),
					(nameof(IUserSettingsService), typeof(IUserSettingsService)),
					(nameof(INetworkService), typeof(INetworkService)),
					(nameof(IFileTagsSettingsService), typeof(IFileTagsSettingsService)),
					(nameof(ISizeProvider), typeof(ISizeProvider)),
					(nameof(IStorageCacheService), typeof(IStorageCacheService)),
					(nameof(IWindowsSecurityService), typeof(IWindowsSecurityService)),
					(nameof(IStorageTrashBinService), typeof(IStorageTrashBinService))
				};
				
				foreach (var (name, type) in testServices)
				{
					try
					{
						var service = Ioc.Default.GetService(type);
						if (service == null)
						{
							App.Logger?.LogError($"Service {name} is not registered in IoC container");
						}
						else
						{
							App.Logger?.LogInformation($"Service {name} is available");
						}
					}
					catch (Exception ex)
					{
						App.Logger?.LogError(ex, $"Error getting service {name}");
					}
				}
				
				App.Logger?.LogInformation("ShellViewModel static constructor completed");
			}
			catch (Exception ex)
			{
				App.Logger?.LogError(ex, "Exception in ShellViewModel static constructor");
			}
		}

		public event EventHandler DirectoryInfoUpdated;

		public event EventHandler GitDirectoryUpdated;

		public event EventHandler<List<ListedItem>> OnSelectionRequestedEvent;

		public string WorkingDirectory { get; private set; }

		public string? GitDirectory { get; private set; }

		public bool IsValidGitDirectory { get; private set; }

		public List<IniSectionDataItem> DesktopIni { get; private set; }

		private StorageFolderWithPath? currentStorageFolder;
		private StorageFolderWithPath workingRoot;

		public delegate void WorkingDirectoryModifiedEventHandler(object sender, WorkingDirectoryModifiedEventArgs e);

		public event WorkingDirectoryModifiedEventHandler WorkingDirectoryModified;

		public delegate void PageTypeUpdatedEventHandler(object sender, PageTypeUpdatedEventArgs e);

		public event PageTypeUpdatedEventHandler PageTypeUpdated;

		public delegate void ItemLoadStatusChangedEventHandler(object sender, ItemLoadStatusChangedEventArgs e);

		public event ItemLoadStatusChangedEventHandler ItemLoadStatusChanged;

		public async Task SetWorkingDirectoryAsync(string? value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return;

			var isLibrary = false;
			string? name = null;
			if (App.LibraryManager.TryGetLibrary(value, out LibraryLocationItem library))
			{
				isLibrary = true;
				name = library.Text;
			}

			WorkingDirectoryModified?.Invoke(this, new WorkingDirectoryModifiedEventArgs { Path = value, IsLibrary = isLibrary, Name = name });

			if (isLibrary || !Path.IsPathRooted(value))
				workingRoot = currentStorageFolder = null;
			else if (!Path.IsPathRooted(WorkingDirectory) || Path.GetPathRoot(WorkingDirectory) != Path.GetPathRoot(value))
				workingRoot = await FilesystemTasks.Wrap(() => DriveHelpers.GetRootFromPathAsync(value)).ConfigureAwait(false);

			if (value == "Home" || value == "ReleaseNotes" || value == "Settings")
				currentStorageFolder = null;
			else
				_ = Task.Run(() => jumpListService.AddFolderAsync(value));

			WorkingDirectory = value;

			string? pathRoot = null;
			if (!FtpHelpers.IsFtpPath(WorkingDirectory))
			{
				pathRoot = Path.GetPathRoot(WorkingDirectory);
			}

			GitDirectory = GitHelpers.GetGitRepositoryPath(WorkingDirectory, pathRoot);
			IsValidGitDirectory = !string.IsNullOrEmpty((await GitHelpers.GetRepositoryHead(GitDirectory).ConfigureAwait(false))?.Name);

			OnPropertyChanged(nameof(WorkingDirectory));
		}

		public async Task<FilesystemResult<BaseStorageFolder>> GetFolderFromPathAsync(string value, CancellationToken cancellationToken = default)
		{
			await getFileOrFolderSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				return await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(value, workingRoot, currentStorageFolder)).ConfigureAwait(false);
			}
			finally
			{
				getFileOrFolderSemaphore.Release();
			}
		}

		public async Task<FilesystemResult<BaseStorageFile>> GetFileFromPathAsync(string value, CancellationToken cancellationToken = default)
		{
			await getFileOrFolderSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				return await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileFromPathAsync(value, workingRoot, currentStorageFolder)).ConfigureAwait(false);
			}
			finally
			{
				getFileOrFolderSemaphore.Release();
			}
		}

		public async Task<FilesystemResult<StorageFolderWithPath>> GetFolderWithPathFromPathAsync(string value, CancellationToken cancellationToken = default)
		{
			await getFileOrFolderSemaphore.WaitAsync(cancellationToken);
			try
			{
				return await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(value, workingRoot, currentStorageFolder));
			}
			finally
			{
				getFileOrFolderSemaphore.Release();
			}
		}

		public async Task<FilesystemResult<StorageFileWithPath>> GetFileWithPathFromPathAsync(string value, CancellationToken cancellationToken = default)
		{
			await getFileOrFolderSemaphore.WaitAsync(cancellationToken);
			try
			{
				return await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileWithPathFromPathAsync(value, workingRoot, currentStorageFolder));
			}
			finally
			{
				getFileOrFolderSemaphore.Release();
			}
		}

		private EmptyTextType emptyTextType;
		public EmptyTextType EmptyTextType
		{
			get => emptyTextType;
			set => SetProperty(ref emptyTextType, value);
		}

		public async Task UpdateSortOptionStatusAsync()
		{
			OnPropertyChanged(nameof(IsSortedByName));
			OnPropertyChanged(nameof(IsSortedByDate));
			OnPropertyChanged(nameof(IsSortedByType));
			OnPropertyChanged(nameof(IsSortedBySize));
			OnPropertyChanged(nameof(IsSortedByPath));
			OnPropertyChanged(nameof(IsSortedByOriginalPath));
			OnPropertyChanged(nameof(IsSortedByDateDeleted));
			OnPropertyChanged(nameof(IsSortedByDateCreated));
			OnPropertyChanged(nameof(IsSortedBySyncStatus));
			OnPropertyChanged(nameof(IsSortedByFileTag));

			await OrderFilesAndFoldersAsync().ConfigureAwait(false);
			await ApplyFilesAndFoldersChangesAsync().ConfigureAwait(false);
		}

		public async Task UpdateSortDirectionStatusAsync()
		{
			OnPropertyChanged(nameof(IsSortedAscending));
			OnPropertyChanged(nameof(IsSortedDescending));

			await OrderFilesAndFoldersAsync().ConfigureAwait(false);
			await ApplyFilesAndFoldersChangesAsync().ConfigureAwait(false);
		}

		public async Task UpdateSortDirectoriesAlongsideFilesAsync()
		{
			OnPropertyChanged(nameof(AreDirectoriesSortedAlongsideFiles));

			await OrderFilesAndFoldersAsync().ConfigureAwait(false);
			await ApplyFilesAndFoldersChangesAsync().ConfigureAwait(false);
		}

		public async Task UpdateSortFilesFirstAsync()
		{
			OnPropertyChanged(nameof(AreFilesSortedFirst));

			await OrderFilesAndFoldersAsync().ConfigureAwait(false);
			await ApplyFilesAndFoldersChangesAsync().ConfigureAwait(false);
		}

		private void UpdateSortAndGroupOptions()
		{
			OnPropertyChanged(nameof(IsSortedByName));
			OnPropertyChanged(nameof(IsSortedByDate));
			OnPropertyChanged(nameof(IsSortedByType));
			OnPropertyChanged(nameof(IsSortedBySize));
			OnPropertyChanged(nameof(IsSortedByPath));
			OnPropertyChanged(nameof(IsSortedByOriginalPath));
			OnPropertyChanged(nameof(IsSortedByDateDeleted));
			OnPropertyChanged(nameof(IsSortedByDateCreated));
			OnPropertyChanged(nameof(IsSortedBySyncStatus));
			OnPropertyChanged(nameof(IsSortedByFileTag));
			OnPropertyChanged(nameof(IsSortedAscending));
			OnPropertyChanged(nameof(IsSortedDescending));
			OnPropertyChanged(nameof(AreDirectoriesSortedAlongsideFiles));
			OnPropertyChanged(nameof(AreFilesSortedFirst));
		}

		public bool IsSortedByName
		{
			get => folderSettings.DirectorySortOption == SortOption.Name;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.Name;
					OnPropertyChanged(nameof(IsSortedByName));
				}
			}
		}

		public bool IsSortedByPath
		{
			get => folderSettings.DirectorySortOption == SortOption.Path;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.Path;

					OnPropertyChanged(nameof(IsSortedByPath));
				}
			}
		}

		public bool IsSortedByOriginalPath
		{
			get => folderSettings.DirectorySortOption == SortOption.OriginalFolder;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.OriginalFolder;

					OnPropertyChanged(nameof(IsSortedByOriginalPath));
				}
			}
		}

		public bool IsSortedByDateDeleted
		{
			get => folderSettings.DirectorySortOption == SortOption.DateDeleted;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.DateDeleted;

					OnPropertyChanged(nameof(IsSortedByDateDeleted));
				}
			}
		}

		public bool IsSortedByDate
		{
			get => folderSettings.DirectorySortOption == SortOption.DateModified;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.DateModified;

					OnPropertyChanged(nameof(IsSortedByDate));
				}
			}
		}

		public bool IsSortedByDateCreated
		{
			get => folderSettings.DirectorySortOption == SortOption.DateCreated;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.DateCreated;

					OnPropertyChanged(nameof(IsSortedByDateCreated));
				}
			}
		}

		public bool IsSortedByType
		{
			get => folderSettings.DirectorySortOption == SortOption.FileType;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.FileType;

					OnPropertyChanged(nameof(IsSortedByType));
				}
			}
		}

		public bool IsSortedBySize
		{
			get => folderSettings.DirectorySortOption == SortOption.Size;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.Size;
					OnPropertyChanged(nameof(IsSortedBySize));
				}
			}
		}

		public bool IsSortedBySyncStatus
		{
			get => folderSettings.DirectorySortOption == SortOption.SyncStatus;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.SyncStatus;
					OnPropertyChanged(nameof(IsSortedBySyncStatus));
				}
			}
		}

		public bool IsSortedByFileTag
		{
			get => folderSettings.DirectorySortOption == SortOption.FileTag;
			set
			{
				if (value)
				{
					folderSettings.DirectorySortOption = SortOption.FileTag;
					OnPropertyChanged(nameof(IsSortedByFileTag));
				}
			}
		}

		public bool IsSortedAscending
		{
			get => folderSettings.DirectorySortDirection == SortDirection.Ascending;
			set
			{
				folderSettings.DirectorySortDirection = value ? SortDirection.Ascending : SortDirection.Descending;
				OnPropertyChanged(nameof(IsSortedAscending));
				OnPropertyChanged(nameof(IsSortedDescending));
			}
		}

		public bool IsSortedDescending
		{
			get => !IsSortedAscending;
			set
			{
				folderSettings.DirectorySortDirection = value ? SortDirection.Descending : SortDirection.Ascending;
				OnPropertyChanged(nameof(IsSortedAscending));
				OnPropertyChanged(nameof(IsSortedDescending));
			}
		}

		public bool AreDirectoriesSortedAlongsideFiles
		{
			get => folderSettings.SortDirectoriesAlongsideFiles;
			set
			{
				folderSettings.SortDirectoriesAlongsideFiles = value;
				OnPropertyChanged(nameof(AreDirectoriesSortedAlongsideFiles));
			}
		}

		public bool AreFilesSortedFirst
		{
			get => folderSettings.SortFilesFirst;
			set
			{
				folderSettings.SortFilesFirst = value;
				OnPropertyChanged(nameof(AreFilesSortedFirst));
			}
		}

		public bool HasNoWatcher { get; private set; }

		public ShellViewModel(LayoutPreferencesManager folderSettingsViewModel)
		{
			System.Diagnostics.Debug.WriteLine("ShellViewModel constructor started");
			
			try
			{
				System.Diagnostics.Debug.WriteLine("Checking folderSettingsViewModel parameter");
				if (folderSettingsViewModel == null)
				{
					System.Diagnostics.Debug.WriteLine("folderSettingsViewModel is null!");
					throw new ArgumentNullException(nameof(folderSettingsViewModel));
				}
				
				System.Diagnostics.Debug.WriteLine("Initializing field values");
				defaultJson = JsonSerializer.SerializeToElement("{}");
				folderTypeTextLocalized = "Folder"; // Strings.Folder.GetLocalizedResource();
				
				System.Diagnostics.Debug.WriteLine("Initializing services from IoC container");
				try
				{
					WindowsIniService = Ioc.Default.GetRequiredService<IWindowsIniService>();
					App.Logger?.LogInformation("WindowsIniService initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize WindowsIniService");
				}
				
				try
				{
					jumpListService = Ioc.Default.GetRequiredService<IWindowsJumpListService>();
					App.Logger?.LogInformation("jumpListService initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize jumpListService");
				}
				
				try
				{
					dialogService = Ioc.Default.GetRequiredService<IDialogService>();
					App.Logger?.LogInformation("dialogService initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize dialogService");
				}
				
				try
				{
					UserSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
					App.Logger?.LogInformation("UserSettingsService initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize UserSettingsService");
				}
				
				try
				{
					NetworkService = Ioc.Default.GetRequiredService<INetworkService>();
					App.Logger?.LogInformation("NetworkService initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize NetworkService");
				}
				
				try
				{
					fileTagsSettingsService = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();
					App.Logger?.LogInformation("fileTagsSettingsService initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize fileTagsSettingsService");
				}
				
				try
				{
					folderSizeProvider = Ioc.Default.GetRequiredService<ISizeProvider>();
					App.Logger?.LogInformation("folderSizeProvider initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize folderSizeProvider");
				}
				
				try
				{
					fileListCache = Ioc.Default.GetRequiredService<IStorageCacheService>();
					App.Logger?.LogInformation("fileListCache initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize fileListCache");
				}
				
				try
				{
					WindowsSecurityService = Ioc.Default.GetRequiredService<IWindowsSecurityService>();
					App.Logger?.LogInformation("WindowsSecurityService initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize WindowsSecurityService");
				}
				
				try
				{
					StorageTrashBinService = Ioc.Default.GetRequiredService<IStorageTrashBinService>();
					App.Logger?.LogInformation("StorageTrashBinService initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize StorageTrashBinService");
				}
				
				try
				{
					viewportThumbnailLoader = Ioc.Default.GetService<Services.Thumbnails.IViewportThumbnailLoaderService>();
					App.Logger?.LogInformation("ViewportThumbnailLoaderService initialized");
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Failed to initialize ViewportThumbnailLoaderService");
				}
				
				App.Logger?.LogInformation("Setting folderSettings from parameter");
				folderSettings = folderSettingsViewModel;
				
				App.Logger?.LogInformation("Initializing collections");
				filesAndFolders = [];
				
				App.Logger?.LogInformation("Initializing queues");
				operationQueue = new ConcurrentQueue<(uint Action, string FileName)>();
				gitChangesQueue = new ConcurrentQueue<uint>();
				itemLoadQueue = new ConcurrentDictionary<string, bool>();
				
				App.Logger?.LogInformation("Getting DispatcherQueue");
				// Initialize DispatcherQueue first
				dispatcherQueue = DispatcherQueue.GetForCurrentThread();
				if (dispatcherQueue == null)
				{
					App.Logger?.LogWarning("DispatcherQueue.GetForCurrentThread() returned null, will defer timer initialization");
					// We'll initialize the timer when DispatcherQueue becomes available
					applyChangesDebounceTimer = null;
				}
				else
				{
					App.Logger?.LogInformation("Creating debounce timer");
					// Initialize debounce timer for ApplyFilesAndFoldersChangesAsync
					applyChangesDebounceTimer = dispatcherQueue.CreateTimer();
					applyChangesDebounceTimer.Interval = TimeSpan.FromMilliseconds(100);
					applyChangesDebounceTimer.Tick += async (s, e) =>
					{
						applyChangesDebounceTimer.Stop();
						await ApplyFilesAndFoldersChangesAsyncInternal();
					};
				}
				
				
				App.Logger?.LogInformation("Creating CancellationTokenSources");
				addFilesCTS = new CancellationTokenSource();
				semaphoreCTS = new CancellationTokenSource();
				loadPropsCTS = new CancellationTokenSource();
				watcherCTS = new CancellationTokenSource();
				
				App.Logger?.LogInformation("Creating async events");
				operationEvent = new AsyncManualResetEvent();
				gitChangedEvent = new AsyncManualResetEvent();
				
				App.Logger?.LogInformation("Creating semaphores");
				enumFolderSemaphore = new SemaphoreSlim(1, 1);
				getFileOrFolderSemaphore = new SemaphoreSlim(32, 32); // Reduce from 50 to 32
				bulkOperationSemaphore = new SemaphoreSlim(1, 1);
				loadThumbnailSemaphore = new SemaphoreSlim(8, 8); // Reduce from 64 to 8 - viewport service handles concurrency
				
				App.Logger?.LogInformation("Subscribing to UserSettingsService events");
				if (UserSettingsService == null)
				{
					App.Logger?.LogError("UserSettingsService is null");
				}
				else
				{
					UserSettingsService.OnSettingChangedEvent += UserSettingsService_OnSettingChangedEvent;
				}
				
				App.Logger?.LogInformation("Subscribing to fileTagsSettingsService events");
				if (fileTagsSettingsService == null)
				{
					App.Logger?.LogError("fileTagsSettingsService is null");
				}
				else
				{
					fileTagsSettingsService.OnSettingImportedEvent += FileTagsSettingsService_OnSettingUpdated;
					fileTagsSettingsService.OnTagsUpdated += FileTagsSettingsService_OnSettingUpdated;
				}
				
				App.Logger?.LogInformation("Subscribing to folderSizeProvider events");
				if (folderSizeProvider == null)
				{
					App.Logger?.LogError("folderSizeProvider is null");
				}
				else
				{
					folderSizeProvider.SizeChanged += FolderSizeProvider_SizeChanged;
				}
				
				App.Logger?.LogInformation("Subscribing to folderSettings events");
				if (folderSettings == null)
				{
					App.Logger?.LogError("folderSettings is null");
				}
				else
				{
					folderSettings.LayoutModeChangeRequested += LayoutModeChangeRequested;
				}
				
				App.Logger?.LogInformation("Subscribing to StorageTrashBinService events");
				if (StorageTrashBinService == null)
				{
					App.Logger?.LogError("StorageTrashBinService is null");
				}
				else if (StorageTrashBinService.Watcher == null)
				{
					App.Logger?.LogError("StorageTrashBinService.Watcher is null");
				}
				else
				{
					StorageTrashBinService.Watcher.ItemAdded += RecycleBinItemCreatedAsync;
					StorageTrashBinService.Watcher.ItemDeleted += RecycleBinItemDeletedAsync;
					StorageTrashBinService.Watcher.RefreshRequested += RecycleBinRefreshRequestedAsync;
				}
				
				App.Logger?.LogInformation("ShellViewModel constructor completed successfully");
			}
			catch (Exception ex)
			{
				App.Logger?.LogError(ex, "Exception in ShellViewModel constructor");
				throw;
			}
		}

		private async void LayoutModeChangeRequested(object? sender, LayoutModeEventArgs e)
		{
			await SafeEnqueueOrInvokeAsync(CheckForBackgroundImage, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
		}

		private async void RecycleBinRefreshRequestedAsync(object sender, FileSystemEventArgs e)
		{
			if (!Constants.UserEnvironmentPaths.RecycleBinPath.Equals(CurrentFolder?.ItemPath, StringComparison.OrdinalIgnoreCase))
				return;

			await SafeEnqueueOrInvokeAsync(() =>
			{
				RefreshItems(null);
			});
		}

		private async void RecycleBinItemDeletedAsync(object sender, FileSystemEventArgs e)
		{
			if (!Constants.UserEnvironmentPaths.RecycleBinPath.Equals(CurrentFolder?.ItemPath, StringComparison.OrdinalIgnoreCase))
				return;

			var removedItem = await RemoveFileOrFolderAsync(e.FullPath);

			if (removedItem is not null)
				await ApplyFilesAndFoldersChangesAsync();
		}

		private async void RecycleBinItemCreatedAsync(object sender, FileSystemEventArgs e)
		{
			if (!Constants.UserEnvironmentPaths.RecycleBinPath.Equals(CurrentFolder?.ItemPath, StringComparison.OrdinalIgnoreCase))
				return;

			using var folderItem = SafetyExtensions.IgnoreExceptions(() => new ShellItem(e.FullPath));
			if (folderItem is null)
				return;

			var shellFileItem = ShellFolderExtensions.GetShellFileItem(folderItem);

			var newListedItem = await AddFileOrFolderFromShellFile(shellFileItem);
			if (newListedItem is null)
				return;

			await AddFileOrFolderAsync(newListedItem);
			await OrderFilesAndFoldersAsync().ConfigureAwait(false);
			await ApplyFilesAndFoldersChangesAsync().ConfigureAwait(false);
		}

		private async void FolderSizeProvider_SizeChanged(object? sender, Services.SizeProvider.SizeChangedEventArgs e)
		{
			try
			{
				// Add timeout to prevent deadlocks
				if (!await enumFolderSemaphore.WaitAsync(TimeSpan.FromSeconds(5), semaphoreCTS.Token))
				{
					App.Logger?.LogWarning("Timeout waiting for enumFolderSemaphore in FolderSizeProvider_SizeChanged");
					return;
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				var matchingItem = filesAndFolders.FirstOrDefault(x => x.ItemPath == e.Path);
				if (matchingItem is not null && (e.ValueState is not SizeChangedValueState.Intermediate || (long)e.NewSize > matchingItem.FileSizeBytes))
				{
					await SafeEnqueueOrInvokeAsync(() =>
					{
						if (e.ValueState is SizeChangedValueState.None)
						{
							matchingItem.FileSizeBytes = 0;
							matchingItem.FileSize = "Size not calculated";
						}
						else if (e.ValueState is SizeChangedValueState.Final || (long)e.NewSize > matchingItem.FileSizeBytes)
						{
							matchingItem.FileSizeBytes = (long)e.NewSize;
							matchingItem.FileSize = e.NewSize.ToSizeString();
						}

						DirectoryInfoUpdated?.Invoke(this, EventArgs.Empty);
					},
					Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
				}
			}
			finally
			{
				enumFolderSemaphore.Release();
			}
		}

		private async void FileTagsSettingsService_OnSettingUpdated(object? sender, EventArgs e)
		{
			await SafeEnqueueOrInvokeAsync(() =>
			{
				if (WorkingDirectory != "Home" && WorkingDirectory != "ReleaseNotes" && WorkingDirectory != "Settings")
					RefreshItems(null);
			});
		}

		private async void UserSettingsService_OnSettingChangedEvent(object? sender, SettingChangedEventArgs e)
		{
			switch (e.SettingName)
			{
				case nameof(UserSettingsService.FoldersSettingsService.ShowFileExtensions):
				case nameof(UserSettingsService.FoldersSettingsService.ShowThumbnails):
				case nameof(UserSettingsService.FoldersSettingsService.ShowHiddenItems):
				case nameof(UserSettingsService.FoldersSettingsService.ShowProtectedSystemFiles):
				case nameof(UserSettingsService.FoldersSettingsService.AreAlternateStreamsVisible):
				case nameof(UserSettingsService.FoldersSettingsService.ShowDotFiles):
				case nameof(UserSettingsService.FoldersSettingsService.CalculateFolderSizes):
				case nameof(UserSettingsService.FoldersSettingsService.SelectFilesOnHover):
				case nameof(UserSettingsService.FoldersSettingsService.ShowCheckboxesWhenSelectingItems):
				case nameof(UserSettingsService.FoldersSettingsService.SizeUnitFormat):
					await SafeEnqueueOrInvokeAsync(() =>
					{
						if (WorkingDirectory != "Home" && WorkingDirectory != "ReleaseNotes" && WorkingDirectory != "Settings")
							RefreshItems(null);
					});
					break;
				case nameof(UserSettingsService.LayoutSettingsService.DefaultSortOption):
				case nameof(UserSettingsService.LayoutSettingsService.DefaultGroupOption):
				case nameof(UserSettingsService.LayoutSettingsService.DefaultSortDirectoriesAlongsideFiles):
				case nameof(UserSettingsService.LayoutSettingsService.DefaultSortFilesFirst):
				case nameof(UserSettingsService.LayoutSettingsService.SyncFolderPreferencesAcrossDirectories):
				case nameof(UserSettingsService.LayoutSettingsService.DefaultGroupByDateUnit):
				case nameof(UserSettingsService.LayoutSettingsService.DefaultLayoutMode):
					await SafeEnqueueOrInvokeAsync(() =>
					{
						folderSettings.OnDefaultPreferencesChanged(WorkingDirectory, e.SettingName);
						UpdateSortAndGroupOptions();
					});
					await OrderFilesAndFoldersAsync();
					await ApplyFilesAndFoldersChangesAsync();
					break;
			}
		}

		private bool IsLoadingCancelled { get; set; }

		public void CancelLoadAndClearFiles()
		{
			Debug.WriteLine("CancelLoadAndClearFiles");
			CloseWatcher();
			if (IsLoadingItems)
			{
				IsLoadingCancelled = true;
				// Properly dispose the old cancellation token source
				var oldCTS = addFilesCTS;
				addFilesCTS = new CancellationTokenSource();
				
				// Cancel and dispose the old token source safely
				try
				{
					oldCTS.Cancel();
					// Give a small delay before disposing to let active operations complete
					_ = Task.Delay(100).ContinueWith(_ => oldCTS.Dispose(), TaskScheduler.Default);
				}
				catch (ObjectDisposedException)
				{
					// Already disposed, ignore
				}
			}
			CancelExtendedPropertiesLoading();
			filesAndFolders.Clear();
			FilesAndFolders.Clear();
			ClearViewport(); // Clear viewport thumbnails
			
			// Clear thumbnail loading queue to reset singleton service state
			try
			{
				var thumbnailQueue = Ioc.Default.GetService<IThumbnailLoadingQueue>();
				if (thumbnailQueue != null)
				{
					// Cancel all pending thumbnail requests for this directory
					thumbnailQueue.CancelRequests(_ => true);
				}
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "Error clearing thumbnail loading queue");
			}
			
			CancelSearch();
		}

		public void CancelExtendedPropertiesLoading()
		{
			// Create new CTS first
			var newCTS = new CancellationTokenSource();
			
			// Atomically swap the CTS
			var oldCTS = Interlocked.Exchange(ref loadPropsCTS, newCTS);
			
			// Cancel and dispose the old token source safely
			if (oldCTS != null)
			{
				try
				{
					oldCTS.Cancel();
					// Dispose after a delay to let active operations handle cancellation
					_ = Task.Delay(100).ContinueWith(_ => 
					{
						try { oldCTS.Dispose(); } catch { }
					}, TaskScheduler.Default);
				}
				catch (ObjectDisposedException)
				{
					// Already disposed, ignore
				}
			}
		}

		public void CancelExtendedPropertiesLoadingForItem(ListedItem item)
		{
			itemLoadQueue.TryUpdate(item.ItemPath, true, false);
		}

		private bool IsSearchResults { get; set; }

		public void UpdateEmptyTextType()
		{
			EmptyTextType = FilesAndFolders.Count == 0 ? (IsSearchResults ? EmptyTextType.NoSearchResultsFound : EmptyTextType.FolderEmpty) : EmptyTextType.None;
		}

		public string? FilesAndFoldersFilter { get; set; }

		// Apply changes with debouncing to prevent rapid UI updates
		public Task ApplyFilesAndFoldersChangesAsync()
		{
			// Ensure timer is initialized
			if (applyChangesDebounceTimer == null)
			{
				InitializeDebounceTimer();
			}
			
			if (applyChangesDebounceTimer != null)
			{
				// Cancel any pending timer
				applyChangesDebounceTimer.Stop();
				
				// Start or restart the timer
				applyChangesDebounceTimer.Start();
			}
			else
			{
				// Fallback: execute immediately if timer cannot be created
				App.Logger?.LogWarning("Debounce timer not available, executing changes immediately");
				return ApplyFilesAndFoldersChangesAsyncInternal();
			}
			
			return Task.CompletedTask;
		}
		
		private void InitializeDebounceTimer()
		{
			if (dispatcherQueue == null)
			{
				dispatcherQueue = DispatcherQueue.GetForCurrentThread();
				if (dispatcherQueue == null)
				{
					App.Logger?.LogError("Cannot initialize debounce timer - DispatcherQueue still null");
					return;
				}
			}
			
			if (applyChangesDebounceTimer == null)
			{
				applyChangesDebounceTimer = dispatcherQueue.CreateTimer();
				applyChangesDebounceTimer.Interval = TimeSpan.FromMilliseconds(100);
				applyChangesDebounceTimer.Tick += async (s, e) =>
				{
					applyChangesDebounceTimer.Stop();
					await ApplyFilesAndFoldersChangesAsyncInternal();
				};
				App.Logger?.LogInformation("Debounce timer initialized successfully");
			}
		}
		
		private async Task<T> SafeEnqueueOrInvokeAsync<T>(Func<Task<T>> action, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal)
		{
			if (dispatcherQueue == null)
			{
				dispatcherQueue = DispatcherQueue.GetForCurrentThread();
				if (dispatcherQueue == null)
				{
					App.Logger?.LogWarning("DispatcherQueue is null, executing action synchronously");
					return await action();
				}
			}
			
			return await dispatcherQueue.EnqueueOrInvokeAsync(action, priority);
		}
		
		private async Task SafeEnqueueOrInvokeAsync(Func<Task> action, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal)
		{
			if (dispatcherQueue == null)
			{
				dispatcherQueue = DispatcherQueue.GetForCurrentThread();
				if (dispatcherQueue == null)
				{
					App.Logger?.LogWarning("DispatcherQueue is null, executing action synchronously");
					await action();
					return;
				}
			}
			
			await dispatcherQueue.EnqueueOrInvokeAsync(action, priority);
		}
		
		private async Task SafeEnqueueOrInvokeAsync(Action action, Microsoft.UI.Dispatching.DispatcherQueuePriority priority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal)
		{
			if (dispatcherQueue == null)
			{
				dispatcherQueue = DispatcherQueue.GetForCurrentThread();
				if (dispatcherQueue == null)
				{
					App.Logger?.LogWarning("DispatcherQueue is null, executing action synchronously");
					action();
					return;
				}
			}
			
			await dispatcherQueue.EnqueueOrInvokeAsync(action, priority);
		}
		
		// Internal method that actually applies the changes
		private async Task ApplyFilesAndFoldersChangesAsyncInternal()
		{
			// Prevent concurrent executions
			if (!await applyChangesSemaphore.WaitAsync(0))
			{
				App.Logger?.LogInformation("ApplyFilesAndFoldersChangesAsyncInternal skipped - already running");
				return;
			}
			
			try
			{
				// Cancel any previous operation and dispose properly
				var oldCTS = applyChangesCTS;
				applyChangesCTS = new CancellationTokenSource();
				var cancellationToken = applyChangesCTS.Token;
				
				// Cancel and dispose the old token source
				oldCTS.Cancel();
				oldCTS.Dispose();
				
				if (filesAndFolders is null || filesAndFolders.Count == 0)
				{
					void ClearDisplay()
					{
						FilesAndFolders.Clear();
						UpdateEmptyTextType();
						DirectoryInfoUpdated?.Invoke(this, EventArgs.Empty);
					}

					if (Win32Helper.IsHasThreadAccessPropertyPresent && dispatcherQueue.HasThreadAccess)
						ClearDisplay();
					else
						await SafeEnqueueOrInvokeAsync(ClearDisplay);

					return;
				}
				var filesAndFoldersLocal = filesAndFolders.ToList();

				// Do filtering BEFORE UI operations to avoid async operations inside dispatcher queue
				List<ListedItem> itemsToDisplay;
				if (string.IsNullOrEmpty(FilesAndFoldersFilter))
				{
					App.Logger?.LogInformation("No filter applied, using all items");
					itemsToDisplay = filesAndFoldersLocal;
				}
				else
				{
					App.Logger?.LogInformation($"ApplyFilesAndFoldersChangesAsync: Filter='{FilesAndFoldersFilter}', LocalItems={filesAndFoldersLocal.Count}");
					
					// Use the preferred search engine
					var searchEngine = UserSettingsService.GeneralSettingsService.PreferredSearchEngine;
					itemsToDisplay = new List<ListedItem>();
					
					switch (searchEngine)
					{
						case Data.Enums.SearchEngine.Everything:
							{
								var everythingService = Ioc.Default.GetService<Services.Search.IEverythingSearchService>();
								if (everythingService != null && everythingService.IsEverythingAvailable())
								{
									try
									{
										App.Logger?.LogInformation($"Starting Everything filter: '{FilesAndFoldersFilter}' on {filesAndFoldersLocal.Count} items");
										
										// Call FilterItemsAsync directly with timeout
										var filterTask = everythingService.FilterItemsAsync(filesAndFoldersLocal, FilesAndFoldersFilter, addFilesCTS.Token);
										var timeoutTask = Task.Delay(5000, addFilesCTS.Token);
										
										if (await Task.WhenAny(filterTask, timeoutTask) == filterTask)
										{
											var filteredItems = await filterTask;
											App.Logger?.LogInformation($"Everything filter returned {filteredItems.Count} items");
											if (!addFilesCTS.IsCancellationRequested)
											{
												itemsToDisplay = filteredItems;
											}
										}
										else
										{
											// Timeout - fall back to fuzzy search
											App.Logger?.LogWarning("Everything search timed out, falling back to fuzzy search");
											goto case Data.Enums.SearchEngine.BuiltInFuzzy;
										}
									}
									catch (Exception ex)
									{
										// On error, fall back to fuzzy search
										App.Logger?.LogWarning(ex, "Everything search failed, falling back to fuzzy search");
										goto case Data.Enums.SearchEngine.BuiltInFuzzy;
									}
								}
								else
								{
									// Everything not available, fall back to fuzzy search
									goto case Data.Enums.SearchEngine.BuiltInFuzzy;
								}
								break;
							}
						
						case Data.Enums.SearchEngine.BuiltInFuzzy:
							{
								var fuzzySearchService = Ioc.Default.GetService<Services.FuzzyMatcher.IFuzzySearchService>();
								if (fuzzySearchService != null)
								{
									try
									{
										// Use async version with cancellation support  
										var filterTask = fuzzySearchService.FilterItemsAsync(filesAndFoldersLocal, FilesAndFoldersFilter, addFilesCTS.Token);
										var timeoutTask = Task.Delay(5000, addFilesCTS.Token);
										
										// Wait for filtering with timeout to prevent hanging
										if (await Task.WhenAny(filterTask, timeoutTask) == filterTask)
										{
											var filteredItems = await filterTask;
											if (!addFilesCTS.IsCancellationRequested)
											{
												itemsToDisplay = filteredItems;
											}
										}
										else
										{
											// Timeout - fall back to simple contains
											App.Logger?.LogWarning("Fuzzy search timed out, falling back to simple contains");
											goto case Data.Enums.SearchEngine.SimpleContains;
										}
									}
									catch (Exception ex)
									{
										// On any error, fall back to simple contains
										App.Logger?.LogWarning(ex, "Fuzzy search failed, falling back to simple contains");
										goto case Data.Enums.SearchEngine.SimpleContains;
									}
								}
								else
								{
									// Fuzzy search not available, fall back to simple contains
									goto case Data.Enums.SearchEngine.SimpleContains;
								}
								break;
							}
						
						case Data.Enums.SearchEngine.SimpleContains:
						default:
							{
								// Simple contains search
								App.Logger?.LogInformation($"Using simple contains filter: '{FilesAndFoldersFilter}' on {filesAndFoldersLocal.Count} items");
								itemsToDisplay = filesAndFoldersLocal
									.Where(x => x.Name.Contains(FilesAndFoldersFilter, StringComparison.OrdinalIgnoreCase))
									.Take(500)
									.ToList();
								App.Logger?.LogInformation($"Simple contains filter returned {itemsToDisplay.Count} items");
								break;
							}
					}
				}

				// Now do UI updates synchronously
				await bulkOperationSemaphore.WaitAsync(addFilesCTS.Token);
				var isSemaphoreReleased = false;
				try
				{
					await SafeEnqueueOrInvokeAsync(() =>
					{
						try
						{
							FilesAndFolders.BeginBulkOperation();

							if (addFilesCTS.IsCancellationRequested)
								return;

							FilesAndFolders.Clear();
							FilesAndFolders.AddRange(itemsToDisplay);
							App.Logger?.LogInformation($"Added {itemsToDisplay.Count} items to FilesAndFolders display");

							if (folderSettings.DirectoryGroupOption != GroupOption.None)
								OrderGroups();

							// Trigger CollectionChanged with NotifyCollectionChangedAction.Reset
							// once loading is completed so that UI can be updated
							FilesAndFolders.EndBulkOperation();
							UpdateEmptyTextType();
							DirectoryInfoUpdated?.Invoke(this, EventArgs.Empty);
						}
						finally
						{
							isSemaphoreReleased = true;
							bulkOperationSemaphore.Release();
						}
					});

					// The semaphore will be released in UI thread
					isSemaphoreReleased = true;
				}
				finally
				{
					if (!isSemaphoreReleased)
						bulkOperationSemaphore.Release();
				}
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, ex.Message);
			}
			finally
			{
				applyChangesSemaphore.Release();
			}
		}

		private Task RequestSelectionAsync(List<ListedItem> itemsToSelect)
		{
			// Don't notify if there weren't listed items
			if (itemsToSelect is null || itemsToSelect.IsEmpty())
				return Task.CompletedTask;

			return SafeEnqueueOrInvokeAsync(() =>
			{
				OnSelectionRequestedEvent?.Invoke(this, itemsToSelect);
			});
		}

		private Task OrderFilesAndFoldersAsync()
		{
			void OrderEntries()
			{
				if (filesAndFolders.Count == 0)
					return;

				// Performance: Only sort if we have items and sorting is needed
				if (filesAndFolders.Count > 1)
				{
					filesAndFolders = new ConcurrentCollection<ListedItem>(SortingHelper.OrderFileList(filesAndFolders, folderSettings.DirectorySortOption, folderSettings.DirectorySortDirection,
						folderSettings.SortDirectoriesAlongsideFiles, folderSettings.SortFilesFirst));
				}
			}

			if (Win32Helper.IsHasThreadAccessPropertyPresent && dispatcherQueue.HasThreadAccess)
				return Task.Run(OrderEntries);

			OrderEntries();

			return Task.CompletedTask;
		}

		private void OrderGroups(CancellationToken token = default)
		{
			var gps = FilesAndFolders.GroupedCollection?.Where(x => !x.IsSorted);
			if (gps is null)
				return;

			foreach (var gp in gps)
			{
				if (token.IsCancellationRequested)
					return;

				gp.Order(list => SortingHelper.OrderFileList(list, folderSettings.DirectorySortOption, folderSettings.DirectorySortDirection,
					folderSettings.SortDirectoriesAlongsideFiles, folderSettings.SortFilesFirst));
			}

			if (FilesAndFolders.GroupedCollection is null || FilesAndFolders.GroupedCollection.IsSorted)
				return;

			if (folderSettings.DirectoryGroupDirection == SortDirection.Ascending)
			{
				if (folderSettings.DirectoryGroupOption == GroupOption.Size)
					// Always show file sections below folders
					FilesAndFolders.GroupedCollection.Order(x => x.OrderBy(y => y.First().PrimaryItemAttribute != StorageItemTypes.Folder || y.First().IsArchive)
						.ThenBy(y => y.Model.SortIndexOverride).ThenBy(y => y.Model.Text));
				else
					FilesAndFolders.GroupedCollection.Order(x => x.OrderBy(y => y.Model.SortIndexOverride).ThenBy(y => y.Model.Text));
			}
			else
			{
				if (folderSettings.DirectoryGroupOption == GroupOption.Size)
					// Always show file sections below folders
					FilesAndFolders.GroupedCollection.Order(x => x.OrderBy(y => y.First().PrimaryItemAttribute != StorageItemTypes.Folder || y.First().IsArchive)
						.ThenByDescending(y => y.Model.SortIndexOverride).ThenByDescending(y => y.Model.Text));
				else
					FilesAndFolders.GroupedCollection.Order(x => x.OrderByDescending(y => y.Model.SortIndexOverride).ThenByDescending(y => y.Model.Text));
			}

			FilesAndFolders.GroupedCollection.IsSorted = true;
		}

		public async Task GroupOptionsUpdatedAsync(CancellationToken token)
		{
			try
			{
				// Conflicts will occur if re-grouping is run while items are still being enumerated,
				// so wait for enumeration to complete first
				await enumFolderSemaphore.WaitAsync(token);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				await bulkOperationSemaphore.WaitAsync(token);
				var isSemaphoreReleased = false;
				try
				{
					await SafeEnqueueOrInvokeAsync(() =>
					{
						try
						{
							FilesAndFolders.BeginBulkOperation();
							UpdateGroupOptions();

							if (FilesAndFolders.IsGrouped)
							{
								FilesAndFolders.ResetGroups(token);
								if (token.IsCancellationRequested)
									return;

								OrderGroups();
							}

							if (token.IsCancellationRequested)
								return;

							FilesAndFolders.EndBulkOperation();
						}
						finally
						{
							isSemaphoreReleased = true;
							bulkOperationSemaphore.Release();
						}
					});

					// The semaphore will be released in UI thread
					isSemaphoreReleased = true;
				}
				finally
				{
					if (!isSemaphoreReleased)
						bulkOperationSemaphore.Release();
				}
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, ex.Message);
			}
			finally
			{
				enumFolderSemaphore.Release();
			}
		}

		public Task ReloadItemGroupHeaderImagesAsync()
		{
			// This is needed to update the group icons for file type groups
			if (folderSettings.DirectoryGroupOption != GroupOption.FileType || FilesAndFolders.GroupedCollection is null)
				return Task.CompletedTask;

			return Task.Run(async () =>
			{
				foreach (var gp in FilesAndFolders.GroupedCollection)
				{
					var img = await GetItemTypeGroupIcon(gp.FirstOrDefault()).ConfigureAwait(false);
					await SafeEnqueueOrInvokeAsync(() =>
					{
						gp.Model.ImageSource = img;
					}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low).ConfigureAwait(false);
				}
			});
		}

		public void UpdateGroupOptions()
		{
			FilesAndFolders.ItemGroupKeySelector = GroupingHelper.GetItemGroupKeySelector(folderSettings.DirectoryGroupOption, folderSettings.DirectoryGroupByDateUnit);
			var groupInfoSelector = GroupingHelper.GetGroupInfoSelector(folderSettings.DirectoryGroupOption, folderSettings.DirectoryGroupByDateUnit);
			FilesAndFolders.GetGroupHeaderInfo = groupInfoSelector.Item1;
			FilesAndFolders.GetExtendedGroupHeaderInfo = groupInfoSelector.Item2;
		}

		private bool isLoadingItems = false;
		public bool IsLoadingItems
		{
			get => isLoadingItems;
			set => SetProperty(ref isLoadingItems, value);
		}

		private async Task<BitmapImage> GetShieldIcon()
		{
			shieldIcon ??= await UIHelpers.GetShieldIconResource();

			return shieldIcon;
		}

			private async Task LoadThumbnailAsync(ListedItem item, CancellationToken cancellationToken)
	{
		// Skip if item is null or path is empty
		if (item == null || string.IsNullOrEmpty(item.ItemPath))
			return;

		// Skip if thumbnail is already loaded
		if (item.FileImage != null)
			return;

		// Try new viewport thumbnail loader service first if available
		if (viewportThumbnailLoader != null)
		{
			try
			{
				// The viewport thumbnail loader will handle the actual loading
				// We'll just make sure the item is registered for loading
				await viewportThumbnailLoader.UpdateViewportAsync(
					new[] { item }, 
					(uint)LayoutSizeKindHelper.GetIconSize(folderSettings.LayoutMode), 
					cancellationToken);
				return;
			}
			catch (Exception ex)
			{
				App.Logger?.LogDebug(ex, "Viewport thumbnail loader failed for {Path}, falling back to direct loading", item.ItemPath);
			}
		}

		// Fallback to direct loading if viewport service is not available
		await LoadThumbnailDirectlyAsync(item, cancellationToken);
	}

	private async Task LoadThumbnailDirectlyAsync(ListedItem item, CancellationToken cancellationToken)
	{
		// Skip if item is null or path is empty
		if (item == null || string.IsNullOrEmpty(item.ItemPath))
			return;

		// Skip if thumbnail is already loaded
		if (item.FileImage != null)
			return;

		// Force proper thumbnail size, not just icons
		var thumbnailSize = LayoutSizeKindHelper.GetIconSize(folderSettings.LayoutMode);
		
		// ALWAYS use at least 48px for thumbnails to avoid the 16px icon issue
		if (thumbnailSize < 48)
			thumbnailSize = 48;
		
		var returnIconOnly = UserSettingsService.FoldersSettingsService.ShowThumbnails == false;
		var useCurrentScale = folderSettings.LayoutMode == FolderLayoutModes.DetailsView || folderSettings.LayoutMode == FolderLayoutModes.ListView || folderSettings.LayoutMode == FolderLayoutModes.ColumnView || folderSettings.LayoutMode == FolderLayoutModes.CardsView;

		byte[]? result = null;

		// Force thumbnail loading (not just icons) for better quality
		var iconOptions = returnIconOnly ? IconOptions.ReturnIconOnly : IconOptions.None;
		if (useCurrentScale)
			iconOptions |= IconOptions.UseCurrentScale;

		// Single call to get icon/thumbnail with proper size
		result = await FileThumbnailHelper.GetIconAsync(
			item.ItemPath,
			thumbnailSize,
			item.IsFolder,
			iconOptions);

		cancellationToken.ThrowIfCancellationRequested();

		if (result is not null)
		{
			try
			{
				await SafeEnqueueOrInvokeAsync(async () =>
				{
					// Assign FileImage property
					var image = await result.ToBitmapAsync();
					if (image is not null)
						item.FileImage = image;
				}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
			}
			catch (Exception ex)
			{
				App.Logger?.LogDebug(ex, "Error setting FileImage for {Path}", item.ItemPath);
			}

			cancellationToken.ThrowIfCancellationRequested();
		}

		// Get icon overlay
		var iconOverlay = await FileThumbnailHelper.GetIconOverlayAsync(item.ItemPath, true);

		cancellationToken.ThrowIfCancellationRequested();

		if (iconOverlay is not null)
		{
			try
			{
				await SafeEnqueueOrInvokeAsync(async () =>
				{
					item.IconOverlay = await iconOverlay.ToBitmapAsync();
					item.ShieldIcon = await GetShieldIcon();
				}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
			}
			catch (Exception ex)
			{
				App.Logger?.LogDebug(ex, "Error setting IconOverlay for {Path}", item.ItemPath);
			}

			cancellationToken.ThrowIfCancellationRequested();
		}

		// Simplified approach - no background loading needed since we're using the viewport service primarily
	}

		private static void SetFileTag(ListedItem item)
		{
			var dbInstance = FileTagsHelper.GetDbInstance();
			dbInstance.SetTags(item.ItemPath, item.FileFRN, item.FileTags ?? []);
		}

		// This works for recycle bin as well as GetFileFromPathAsync/GetFolderFromPathAsync work
		// for file inside the recycle bin (but not on the recycle bin folder itself)
		public async Task LoadExtendedItemPropertiesAsync(ListedItem item)
		{
			if (item is null)
				return;

			itemLoadQueue[item.ItemPath] = false;

			var cts = loadPropsCTS;
			
			// Check if CTS is already disposed
			if (cts == null || cts.IsCancellationRequested)
				return;

			try
			{
				// Use a try-catch to handle disposed CTS
				try
				{
					cts.Token.ThrowIfCancellationRequested();
				}
				catch (ObjectDisposedException)
				{
					// CTS was disposed, exit gracefully
					return;
				}
				if (itemLoadQueue.TryGetValue(item.ItemPath, out var canceled) && canceled)
					return;

				item.ItemPropertiesInitialized = true;
				var wasSyncStatusLoaded = false;
				var loadGroupHeaderInfo = false;
				ImageSource? groupImage = null;
				GroupedCollection<ListedItem>? gp = null;

				try
				{
					var isFileTypeGroupMode = folderSettings.DirectoryGroupOption == GroupOption.FileType;
					BaseStorageFile? matchingStorageFile = null;
					if (item.Key is not null && FilesAndFolders.IsGrouped && FilesAndFolders.GetExtendedGroupHeaderInfo is not null)
					{
						gp = FilesAndFolders.GroupedCollection?.FirstOrDefault(x => x.Model.Key == item.Key);
						loadGroupHeaderInfo = gp is not null && !gp.Model.Initialized && gp.GetExtendedGroupHeaderInfo is not null;
					}

					cts.Token.ThrowIfCancellationRequested();
					// Use viewport thumbnail loader if available, otherwise fallback to direct loading
					try
					{
						if (viewportThumbnailLoader != null)
						{
							await viewportThumbnailLoader.UpdateViewportAsync(
								new[] { item }, 
								(uint)LayoutSizeKindHelper.GetIconSize(folderSettings.LayoutMode), 
								cts.Token);
						}
						else
						{
							await LoadThumbnailDirectlyAsync(item, cts.Token);
						}
					}
					catch (OperationCanceledException)
					{
						// Expected during navigation
						throw;
					}
					catch (Exception ex)
					{
						App.Logger?.LogError(ex, "Failed to load thumbnail for {Path}", item.ItemPath);
						// Continue with other property loading even if thumbnail fails
					}

					cts.Token.ThrowIfCancellationRequested();
					if (item.IsLibrary || item.PrimaryItemAttribute == StorageItemTypes.File || item.IsArchive)
					{
						if (!item.IsShortcut && !item.IsHiddenItem && !FtpHelpers.IsFtpPath(item.ItemPath))
						{
							matchingStorageFile = await GetFileFromPathAsync(item.ItemPath, cts.Token);
							if (matchingStorageFile is not null)
							{
								cts.Token.ThrowIfCancellationRequested();

								var syncStatus = await CheckCloudDriveSyncStatusAsync(matchingStorageFile);
								var fileFRN = await FileTagsHelper.GetFileFRN(matchingStorageFile);
								var fileTag = FileTagsHelper.ReadFileTag(item.ItemPath);
								var itemType = (item.ItemType == "Folder") ? item.ItemType : matchingStorageFile.DisplayType;
								var extraProperties = await GetExtraProperties(matchingStorageFile);

								cts.Token.ThrowIfCancellationRequested();

								await SafeEnqueueOrInvokeAsync(() =>
								{
									item.FolderRelativeId = matchingStorageFile.FolderRelativeId;
									item.ItemType = itemType;
									item.SyncStatusUI = CloudDriveSyncStatusUI.FromCloudDriveSyncStatus(syncStatus);
									item.FileFRN = fileFRN;
									item.FileTags = fileTag;
									item.IsElevationRequired = CheckElevationRights(item);
									item.ImageDimensions = extraProperties?.Result["System.Image.Dimensions"]?.ToString() ?? string.Empty;
									item.FileVersion = extraProperties?.Result["System.FileVersion"]?.ToString() ?? string.Empty;
									item.MediaDuration = ulong.TryParse(extraProperties?.Result["System.Media.Duration"]?.ToString(), out ulong duration)
											? TimeSpan.FromTicks((long)duration).ToString(@"hh\:mm\:ss")
											: string.Empty;

									switch (true)
									{
																		case var _ when !string.IsNullOrEmpty(item.ImageDimensions):
									item.ContextualProperty = $"Dimensions: {item.ImageDimensions}";
									break;
								case var _ when !string.IsNullOrEmpty(item.MediaDuration):
									item.ContextualProperty = $"Duration: {item.MediaDuration}";
									break;
								case var _ when !string.IsNullOrEmpty(item.FileVersion):
									item.ContextualProperty = $"Version: {item.FileVersion}";
									break;
								default:
									item.ContextualProperty = $"Modified: {item.ItemDateModified}";
									break;
									}
								},
								Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

								SetFileTag(item);
								wasSyncStatusLoaded = true;
							}
						}
					}
					else
					{
						if (!item.IsShortcut && !item.IsHiddenItem && !FtpHelpers.IsFtpPath(item.ItemPath))
						{
							BaseStorageFolder matchingStorageFolder = await GetFolderFromPathAsync(item.ItemPath, cts.Token);
							if (matchingStorageFolder is not null)
							{
								if (matchingStorageFolder.DisplayName != item.Name && !matchingStorageFolder.DisplayName.StartsWith("$R", StringComparison.Ordinal))
								{
									cts.Token.ThrowIfCancellationRequested();
									await SafeEnqueueOrInvokeAsync(() =>
									{
										item.ItemNameRaw = matchingStorageFolder.DisplayName;
									});
									await fileListCache.AddDisplayName(item.ItemPath, matchingStorageFolder.DisplayName);
									if (folderSettings.DirectorySortOption == SortOption.Name && !isLoadingItems)
									{
										await OrderFilesAndFoldersAsync();
										await ApplyFilesAndFoldersChangesAsync();
									}
								}

								cts.Token.ThrowIfCancellationRequested();
								var syncStatus = await CheckCloudDriveSyncStatusAsync(matchingStorageFolder);
								var fileFRN = await FileTagsHelper.GetFileFRN(matchingStorageFolder);
								var fileTag = FileTagsHelper.ReadFileTag(item.ItemPath);
								var itemType = (item.ItemType == "Folder") ? item.ItemType : matchingStorageFolder.DisplayType;
								var extraProperties = await GetExtraProperties(matchingStorageFolder);

								cts.Token.ThrowIfCancellationRequested();

								await SafeEnqueueOrInvokeAsync(() =>
								{
									item.FolderRelativeId = matchingStorageFolder.FolderRelativeId;
									item.ItemType = itemType;
									item.SyncStatusUI = CloudDriveSyncStatusUI.FromCloudDriveSyncStatus(syncStatus);
									item.FileFRN = fileFRN;
									item.FileTags = fileTag;

									if (extraProperties is not null)
									{
										// Drive Storage Details
										if (extraProperties.Result["System.SFGAOFlags"] is uint attributesRaw &&
											extraProperties.Result["System.Capacity"] is ulong capacityRaw &&
											extraProperties.Result["System.FreeSpace"] is ulong freeSpaceRaw &&
											((SFGAO_FLAGS)attributesRaw).HasFlag(SFGAO_FLAGS.SFGAO_REMOVABLE) &&
											!((SFGAO_FLAGS)attributesRaw).HasFlag(SFGAO_FLAGS.SFGAO_FILESYSTEM))
										{
											var maxSpace = ByteSize.FromBytes(capacityRaw);
											var freeSpace = ByteSize.FromBytes(freeSpaceRaw);

											item.MaxSpace = maxSpace;
											item.SpaceUsed = maxSpace - freeSpace;
											item.FileSize = $"{freeSpace.ToSizeString()} free of {maxSpace.ToSizeString()}";
											item.ShowDriveStorageDetails = true;
										}

									}
									else
									{
										item.ContextualProperty = $"Modified: {item.ItemDateModified}";
									}
								},
								Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

								SetFileTag(item);
								wasSyncStatusLoaded = true;
							}
						}
					}

					if (loadGroupHeaderInfo && isFileTypeGroupMode)
						groupImage = await GetItemTypeGroupIcon(item, matchingStorageFile);
				}
				catch (Exception)
				{
				}
				finally
				{
					if (!wasSyncStatusLoaded)
					{
						// Safely check cancellation to avoid ObjectDisposedException
						bool shouldContinue = true;
						try
						{
							cts.Token.ThrowIfCancellationRequested();
						}
						catch (ObjectDisposedException)
						{
							// CTS was disposed, exit gracefully
							shouldContinue = false;
						}
						
						if (shouldContinue)
						{
							await FilesystemTasks.Wrap(async () =>
							{
								var fileTag = FileTagsHelper.ReadFileTag(item.ItemPath);

								await SafeEnqueueOrInvokeAsync(() =>
								{
									// Reset cloud sync status icon
									item.SyncStatusUI = new CloudDriveSyncStatusUI();

									item.FileTags = fileTag;
								},
								Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

								SetFileTag(item);
							});
						}
					}
					else
					{
						// Try loading thumbnail for cloud files in case they weren't cached the first time
						if (item.SyncStatusUI.SyncStatus != CloudDriveSyncStatus.NotSynced && item.SyncStatusUI.SyncStatus != CloudDriveSyncStatus.Unknown)
						{
							var cloudThumbnailTask = Task.Run(async () =>
							{
								try
								{
									await Task.Delay(500);
									try
									{
										cts.Token.ThrowIfCancellationRequested();
										await LoadThumbnailAsync(item, cts.Token);
									}
									catch (ObjectDisposedException)
									{
										// CTS was disposed, exit gracefully
										return;
									}
								}
								catch (OperationCanceledException)
								{
									// Expected during navigation
								}
								catch (Exception ex)
								{
									App.Logger?.LogError(ex, "Error loading cloud thumbnail for {Path}", item.ItemPath);
								}
							});

							// Handle task exceptions properly
							_ = cloudThumbnailTask.ContinueWith(task =>
							{
								if (task.IsFaulted)
								{
									App.Logger?.LogError(task.Exception, "Unhandled error in cloud thumbnail loading task for {Path}", item.ItemPath);
								}
							}, TaskScheduler.Default);
						}
					}

					if (loadGroupHeaderInfo)
					{
						bool shouldContinue = true;
						try
						{
							cts.Token.ThrowIfCancellationRequested();
						}
						catch (ObjectDisposedException)
						{
							// CTS was disposed, exit gracefully
							shouldContinue = false;
						}
						
						if (shouldContinue)
						{
							await SafetyExtensions.IgnoreExceptions(() =>
								SafeEnqueueOrInvokeAsync(() =>
								{
									gp.Model.ImageSource = groupImage;
									gp.InitializeExtendedGroupHeaderInfoAsync();
								}));
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Ignored
			}
			finally
			{
				itemLoadQueue.TryRemove(item.ItemPath, out _);
				await RefreshTagGroups();
			}
		}

		public async Task RefreshTagGroups()
		{
			if (FilesAndFolders.IsGrouped &&
				folderSettings.DirectoryGroupOption is GroupOption.FileTag &&
				itemLoadQueue.IsEmpty())
			{
				updateTagGroupCTS?.Cancel();
				updateTagGroupCTS = new();

				await GroupOptionsUpdatedAsync(updateTagGroupCTS.Token);
			}
		}

		public Task UpdateItemsTags(Dictionary<string, string[]> newTags)
		{
			return SafeEnqueueOrInvokeAsync(() =>
			{
				int count = newTags.Count;
				foreach(var item in FilesAndFolders)
				{
					if (newTags.TryGetValue(item.ItemPath, out var tags))
					{
						item.FileTags = tags;
						if (--count == 0)
							break;
					}
				}
			},
			Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
		}

		private bool CheckElevationRights(ListedItem item)
		{
			if (item.SyncStatusUI.LoadSyncStatus)
				return false;

			return WindowsSecurityService.IsElevationRequired(item.IsShortcut ? ((IShortcutItem)item).TargetPath : item.ItemPath);
		}

		public async Task LoadGitPropertiesAsync(IGitItem gitItem)
		{
			var getStatus = EnabledGitProperties is GitProperties.All or GitProperties.Status && !gitItem.StatusPropertiesInitialized;
			var getCommit = EnabledGitProperties is GitProperties.All or GitProperties.Commit && !gitItem.CommitPropertiesInitialized;

			if (!getStatus && !getCommit)
				return;

			var cts = loadPropsCTS;

			try
			{
				await Task.Run(async () =>
				{

					if (GitHelpers.IsRepositoryEx(gitItem.ItemPath, out var repoPath) &&
						!string.IsNullOrEmpty(repoPath))
					{
						cts.Token.ThrowIfCancellationRequested();

						if (getStatus)
							gitItem.StatusPropertiesInitialized = true;

						if (getCommit)
							gitItem.CommitPropertiesInitialized = true;

						await SafetyExtensions.IgnoreExceptions(async () =>
						{
							await SafeEnqueueOrInvokeAsync(() =>
							{
								try
								{
									using var repo = new Repository(repoPath);
									GitItemModel gitItemModel = GitHelpers.GetGitInformationForItem(repo, gitItem.ItemPath, getStatus, getCommit);

									if (getStatus)
									{
										gitItem.UnmergedGitStatusIcon = gitItemModel.Status switch
										{
											ChangeKind.Added => (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["App.ThemedIcons.Status.Added"],
											ChangeKind.Deleted => (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["App.ThemedIcons.Status.Removed"],
											ChangeKind.Modified => (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["App.ThemedIcons.Status.Modified"],
											ChangeKind.Untracked => (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["App.ThemedIcons.Status.Removed"],
											_ => null,
										};
										gitItem.UnmergedGitStatusName = gitItemModel.StatusHumanized;
									}
									if (getCommit)
									{
										gitItem.GitLastCommitDate = gitItemModel.LastCommit?.Author.When;
										gitItem.GitLastCommitMessage = gitItemModel.LastCommit?.MessageShort;
										gitItem.GitLastCommitAuthor = gitItemModel.LastCommit?.Author.Name;
										gitItem.GitLastCommitSha = gitItemModel.LastCommit?.Sha.Substring(0, 7);
										gitItem.GitLastCommitFullSha = gitItemModel.LastCommit?.Sha;
									}
								}
								catch (LibGit2Sharp.RepositoryNotFoundException)
								{
									// Repository is no longer valid or accessible
									// This can happen due to permission issues or concurrent modifications
								}
								catch (Exception ex) when (ex is LibGit2SharpException or UnauthorizedAccessException)
								{
									// Handle other LibGit2Sharp exceptions and access issues
								}
							},
							Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
						});
					}
				}, cts.Token);
			}
			catch (OperationCanceledException)
			{
				// Ignored
			}
		}

		private async Task<ImageSource?> GetItemTypeGroupIcon(ListedItem item, BaseStorageFile? matchingStorageItem = null)
		{
			ImageSource? groupImage = null;
			if (item.PrimaryItemAttribute != StorageItemTypes.Folder || item.IsArchive)
			{
				var result = await FileThumbnailHelper.GetIconAsync(
					item.ItemPath,
					Constants.ShellIconSizes.Large,
					false,
					IconOptions.ReturnIconOnly | IconOptions.UseCurrentScale);

							if (result is not null && !item.IsShortcut)
				groupImage = await SafeEnqueueOrInvokeAsync(async () =>
				{
					if (result.Length > 0)
					{
						using var ms = new MemoryStream(result);
						var image = new BitmapImage();
						await image.SetSourceAsync(ms.AsRandomAccessStream());
						return image;
					}
					return null;
				}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

				// The groupImage is null if loading icon from fulltrust process failed
				if (!item.IsShortcut && !item.IsHiddenItem && !FtpHelpers.IsFtpPath(item.ItemPath) && groupImage is null)
				{
					matchingStorageItem ??= await GetFileFromPathAsync(item.ItemPath);

					if (matchingStorageItem is not null)
					{
						using StorageItemThumbnail headerThumbnail = await FilesystemTasks.Wrap(() => matchingStorageItem.GetThumbnailAsync(ThumbnailMode.DocumentsView, 36, ThumbnailOptions.UseCurrentScale).AsTask());
						if (headerThumbnail is not null)
						{
							await SafeEnqueueOrInvokeAsync(async () =>
							{
								var bmp = new BitmapImage();
								await bmp.SetSourceAsync(headerThumbnail);
								groupImage = bmp;
							});
						}

					}
				}
			}
			// This prevents both the shortcut glyph and folder icon being shown
			else if (!item.IsShortcut)
			{
				await SafeEnqueueOrInvokeAsync(() => groupImage = new SvgImageSource(new Uri("ms-appx:///Assets/FolderIcon2.svg"))
				{
					RasterizePixelHeight = 128,
					RasterizePixelWidth = 128,
				}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
			}

			return groupImage;
		}

		public void RefreshItems(string? previousDir, Action postLoadCallback = null)
		{
			RapidAddItemsToCollectionAsync(WorkingDirectory, previousDir, postLoadCallback);
		}

		private async Task RapidAddItemsToCollectionAsync(string path, string? previousDir, Action postLoadCallback)
		{
			IsSearchResults = false;
			HasNoWatcher = false;
			ItemLoadStatusChanged?.Invoke(this, new ItemLoadStatusChangedEventArgs() { Status = ItemLoadStatusChangedEventArgs.ItemLoadStatus.Starting });

			CancelLoadAndClearFiles();

			if (string.IsNullOrEmpty(path))
				return;

			try
			{
				// Only one instance at a time should access this function
				// Wait here until the previous one has ended
				// If we're waiting and a new update request comes through simply drop this instance
				if (!await enumFolderSemaphore.WaitAsync(TimeSpan.FromSeconds(10), semaphoreCTS.Token))
				{
					App.Logger?.LogWarning("Timeout waiting for enumFolderSemaphore in RapidAddItemsToCollectionAsync");
					return;
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				// Drop all the other waiting instances
				semaphoreCTS.Cancel();
				semaphoreCTS = new CancellationTokenSource();

				IsLoadingItems = true;

				filesAndFolders.Clear();
				FilesAndFolders.Clear();

				ItemLoadStatusChanged?.Invoke(this, new ItemLoadStatusChangedEventArgs() { Status = ItemLoadStatusChangedEventArgs.ItemLoadStatus.InProgress });

				if (path.ToLowerInvariant().EndsWith(ShellLibraryItem.EXTENSION, StringComparison.Ordinal))
				{
					if (App.LibraryManager.TryGetLibrary(path, out LibraryLocationItem library) && !library.IsEmpty)
					{
						var libItem = new LibraryItem(library);
						foreach (var folder in library.Folders)
							await RapidAddItemsToCollectionAsync(folder, libItem);
					}
				}
				else
				{
					await RapidAddItemsToCollectionAsync(path);
				}

				ItemLoadStatusChanged?.Invoke(this, new ItemLoadStatusChangedEventArgs() { Status = ItemLoadStatusChangedEventArgs.ItemLoadStatus.Complete, PreviousDirectory = previousDir, Path = path });
				IsLoadingItems = false;

				AdaptiveLayoutHelpers.ApplyAdaptativeLayout(folderSettings, filesAndFolders);
			}
			finally
			{
				// Make sure item count is updated
				DirectoryInfoUpdated?.Invoke(this, EventArgs.Empty);
				enumFolderSemaphore.Release();
			}

			postLoadCallback?.Invoke();
		}

		private async Task RapidAddItemsToCollectionAsync(string? path, LibraryItem? library = null)
		{
			if (string.IsNullOrEmpty(path))
				return;

			var stopwatch = new Stopwatch();
			stopwatch.Start();

			var isRecycleBin = path.StartsWith(Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.Ordinal);
			var enumerated = await EnumerateItemsFromStandardFolderAsync(path, addFilesCTS.Token, library);

			// Hide progressbar after enumeration
			IsLoadingItems = false;

			switch (enumerated)
			{
				// Enumerated with FindFirstFileExFromApp
				// Is folder synced to cloud storage?
				case 0:
					currentStorageFolder ??= await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(path));
					var syncStatus = await CheckCloudDriveSyncStatusAsync(currentStorageFolder?.Item);

					PageTypeUpdated?.Invoke(this, new PageTypeUpdatedEventArgs()
					{
						IsTypeCloudDrive = syncStatus != CloudDriveSyncStatus.NotSynced && syncStatus != CloudDriveSyncStatus.Unknown,
						IsTypeGitRepository = IsValidGitDirectory
					});

					if (!HasNoWatcher)
						WatchForDirectoryChanges(path, syncStatus);
					if (IsValidGitDirectory)
						WatchForGitChanges();
					break;

				// Enumerated with StorageFolder
				case 1:
					PageTypeUpdated?.Invoke(this, new PageTypeUpdatedEventArgs() { IsTypeCloudDrive = false, IsTypeRecycleBin = isRecycleBin });
					currentStorageFolder ??= await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(path));
					if (!HasNoWatcher)
						WatchForStorageFolderChangesAsync(currentStorageFolder?.Item);
					break;

				// Watch for changes using Win32 in Box Drive folder (#7428) and network drives (#5869)
				case 2:
					PageTypeUpdated?.Invoke(this, new PageTypeUpdatedEventArgs() { IsTypeCloudDrive = false });
					if (!HasNoWatcher)
						WatchForWin32FolderChanges(path);
					break;

				// Enumeration failed
				case -1:
				default:
					break;
			}

			if (IsLoadingCancelled)
			{
				IsLoadingCancelled = false;
				IsLoadingItems = false;
				return;
			}

			stopwatch.Stop();
			Debug.WriteLine($"Loading of items in {path} completed in {stopwatch.ElapsedMilliseconds} milliseconds.\n");
		}

		public void CloseWatcher()
		{
			watcher?.Dispose();
			watcher = null;

			aProcessQueueAction = null;
			gitProcessQueueAction = null;
			watcherCTS?.Cancel();
			watcherCTS = new CancellationTokenSource();
		}

		private async Task<int> EnumerateItemsFromStandardFolderAsync(string path, CancellationToken cancellationToken, LibraryItem? library = null)
		{
			// Flag to use FindFirstFileExFromApp or StorageFolder enumeration - Use storage folder for Box Drive (#4629)
			var isBoxFolder = CloudDrivesManager.Drives.FirstOrDefault(x => x.Text == "Box")?.Path?.TrimEnd('\\') is string boxFolder && path.StartsWith(boxFolder);
			bool isWslDistro = path.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase)
				|| path.Equals(@"\\wsl$", StringComparison.OrdinalIgnoreCase) || path.Equals(@"\\wsl.localhost", StringComparison.OrdinalIgnoreCase);
			bool isMtp = path.StartsWith(@"\\?\", StringComparison.Ordinal);
			bool isShellFolder = path.StartsWith(@"\\SHELL\", StringComparison.Ordinal);
			bool isNetwork = path.StartsWith(@"\\", StringComparison.Ordinal) &&
				!isMtp &&
				!isShellFolder &&
				!isWslDistro;
			bool isNetdisk = false;
	
			try
			{
				// Special handling for network drives
				if (!isNetwork)
					isNetdisk = (new DriveInfo(path).DriveType == System.IO.DriveType.Network);
			}
			catch { }
 			
			bool isFtp = FtpHelpers.IsFtpPath(path);
			bool enumFromStorageFolder = isBoxFolder || isFtp;

			BaseStorageFolder? rootFolder = null;

			if (isNetwork || isNetdisk)
			{
				var auth = await NetworkService.AuthenticateNetworkShare(path);
				if (!auth)
					return -1;
			}

			if (!enumFromStorageFolder && FolderHelpers.CheckFolderAccessWithWin32(path))
			{
				// Will enumerate with FindFirstFileExFromApp, rootFolder only used for Bitlocker
				currentStorageFolder = null;
			}
			else if (workingRoot is not null)
			{
				var res = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(path, workingRoot, currentStorageFolder));
				if (!res)
					return -1;

				currentStorageFolder = res.Result;
				rootFolder = currentStorageFolder.Item;
				enumFromStorageFolder = true;
			}
			else
			{
				var res = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(path, workingRoot, currentStorageFolder));
				if (res)
				{
					currentStorageFolder = res.Result;
					rootFolder = currentStorageFolder.Item;
				}
				else if (res == FileSystemStatusCode.Unauthorized)
				{
					await DialogDisplayHelper.ShowDialogAsync(
						"Access Denied",
						"Access denied to folder");

					return -1;
				}
				else if (res == FileSystemStatusCode.NotFound)
				{
					await DialogDisplayHelper.ShowDialogAsync(
						"Folder Not Found",
						"The folder you are looking for does not exist.");

					return -1;
				}
				else
				{
					await DialogDisplayHelper.ShowDialogAsync(
						"Drive Unplugged",
						res.ErrorCode.ToString());

					return -1;
				}
			}

			var pathRoot = Path.GetPathRoot(path);
			if (Path.IsPathRooted(path) && pathRoot == path)
			{
				rootFolder ??= await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(path));
				if (await FolderHelpers.CheckBitlockerStatusAsync(rootFolder, WorkingDirectory))
					await ContextMenu.InvokeVerb("unlock-bde", pathRoot);
			}

			HasNoWatcher = isFtp || isWslDistro || isMtp || currentStorageFolder?.Item is ZipStorageFolder;

			if (enumFromStorageFolder)
			{
				var basicProps = await rootFolder?.GetBasicPropertiesAsync();
				var currentFolder = library ?? new ListedItem(rootFolder?.FolderRelativeId ?? string.Empty)
				{
					PrimaryItemAttribute = StorageItemTypes.Folder,
					ItemPropertiesInitialized = true,
					ItemNameRaw = rootFolder?.DisplayName ?? string.Empty,
					ItemDateModifiedReal = basicProps.DateModified,
					ItemType = rootFolder?.DisplayType ?? string.Empty,
					FileImage = null,
					LoadFileIcon = false,
					ItemPath = string.IsNullOrEmpty(rootFolder?.Path) ? currentStorageFolder?.Path ?? string.Empty : rootFolder.Path,
					FileSize = null,
					FileSizeBytes = 0,
				};

				if (library is null)
					currentFolder.ItemDateCreatedReal = rootFolder?.DateCreated ?? DateTimeOffset.Now;

				CurrentFolder = currentFolder;
				await EnumFromStorageFolderAsync(path, rootFolder, currentStorageFolder, cancellationToken);

				// Workaround for #7428
				return isBoxFolder ? 2 : 1;
			}
			else
			{
				(IntPtr hFile, WIN32_FIND_DATA findData, int errorCode) = await Task.Run(() =>
				{
					var findInfoLevel = FINDEX_INFO_LEVELS.FindExInfoBasic;
					var additionalFlags = FIND_FIRST_EX_LARGE_FETCH;

					IntPtr hFileTsk = FindFirstFileExFromApp(
						path + "\\*.*",
						findInfoLevel,
						out WIN32_FIND_DATA findDataTsk,
						FINDEX_SEARCH_OPS.FindExSearchNameMatch,
						IntPtr.Zero,
						additionalFlags);

					return (hFileTsk, findDataTsk, hFileTsk.ToInt64() == -1 ? Marshal.GetLastWin32Error() : 0);
				})
				.WithTimeoutAsync(TimeSpan.FromSeconds(5));

				var itemModifiedDate = DateTime.Now;
				var itemCreatedDate = DateTime.Now;

				try
				{
					FileTimeToSystemTime(ref findData.ftLastWriteTime, out var systemModifiedTimeOutput);
					itemModifiedDate = systemModifiedTimeOutput.ToDateTime();

					FileTimeToSystemTime(ref findData.ftCreationTime, out SYSTEMTIME systemCreatedTimeOutput);
					itemCreatedDate = systemCreatedTimeOutput.ToDateTime();
				}
				catch (ArgumentException)
				{
				}

				var isHidden = (((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden);
				var opacity = isHidden ? Constants.UI.DimItemOpacity : 1d;

				var currentFolder = library ?? new ListedItem(null)
				{
					PrimaryItemAttribute = StorageItemTypes.Folder,
					ItemPropertiesInitialized = true,
					ItemNameRaw = rootFolder?.DisplayName ?? Path.GetFileName(path.TrimEnd('\\')),
					ItemDateModifiedReal = itemModifiedDate,
					ItemDateCreatedReal = itemCreatedDate,
					ItemType = folderTypeTextLocalized,
					FileImage = null,
					IsHiddenItem = isHidden,
					Opacity = opacity,
					LoadFileIcon = false,
					ItemPath = path,
					FileSize = null,
					FileSizeBytes = 0,
				};

				CurrentFolder = currentFolder;

				if (hFile == IntPtr.Zero)
				{
					await DialogDisplayHelper.ShowDialogAsync("Drive Unplugged", "");

					return -1;
				}
				else if (hFile.ToInt64() == -1)
				{
					await EnumFromStorageFolderAsync(path, rootFolder, currentStorageFolder, cancellationToken);

					// errorCode == ERROR_ACCESS_DENIED
					if (filesAndFolders.Count == 0 && errorCode == 0x5)
					{
						await DialogDisplayHelper.ShowDialogAsync(
							"Access Denied",
							"Access denied to folder");

						return -1;
					}

					return 1;
				}
				else
				{
					await Task.Run(async () =>
					{
						List<ListedItem> fileList = await Win32StorageEnumerator.ListEntries(path, hFile, findData, cancellationToken, -1, intermediateAction: async (intermediateList) =>
						{
							filesAndFolders.AddRange(intermediateList);
							// Adaptive UI updates based on batch size
							// For large directories, update less frequently to improve performance
							bool shouldUpdate = filesAndFolders.Count < 5000 ? 
								intermediateList.Count >= 50 : // Normal directories - update every 50 items
								intermediateList.Count >= 500; // Large directories - update every 500 items
							
							if (shouldUpdate)
							{
								await ApplyFilesAndFoldersChangesAsync();
							}
						});

						filesAndFolders.AddRange(fileList);
						FilesAndFoldersFilter = null;

						await OrderFilesAndFoldersAsync();
						await ApplyFilesAndFoldersChangesAsync();
						
						await SafeEnqueueOrInvokeAsync(CheckForSolutionFile, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
						await SafeEnqueueOrInvokeAsync(() =>
						{
							GetDesktopIniFileData();
							CheckForBackgroundImage();
						},
						Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
					});

					rootFolder ??= await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(path));
					if (rootFolder is not null)
					{
						if (rootFolder.DisplayName is not null)
							currentFolder.ItemNameRaw = rootFolder.DisplayName;

						if (!string.Equals(path, Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.OrdinalIgnoreCase))
						{
							var syncStatus = await CheckCloudDriveSyncStatusAsync(rootFolder);
							currentFolder.SyncStatusUI = CloudDriveSyncStatusUI.FromCloudDriveSyncStatus(syncStatus);
						}
					}

					return 0;
				}
			}
		}

		private async Task EnumFromStorageFolderAsync(string path, BaseStorageFolder? rootFolder, StorageFolderWithPath currentStorageFolder, CancellationToken cancellationToken)
		{
			if (rootFolder is null)
				return;

			if (rootFolder is IPasswordProtectedItem ppis)
				ppis.PasswordRequestedCallback = UIFilesystemHelpers.RequestPassword;

			await Task.Run(async () =>
			{
				List<ListedItem> finalList = await UniversalStorageEnumerator.ListEntries(
					rootFolder,
					currentStorageFolder,
					cancellationToken,
					-1,
					async (intermediateList) =>
					{
						filesAndFolders.AddRange(intermediateList);

						await OrderFilesAndFoldersAsync();
						await ApplyFilesAndFoldersChangesAsync();
					});

				filesAndFolders.AddRange(finalList);

				await OrderFilesAndFoldersAsync();
				await ApplyFilesAndFoldersChangesAsync();
			}, cancellationToken);

			if (rootFolder is IPasswordProtectedItem ppiu)
				ppiu.PasswordRequestedCallback = null;
		}

		private void CheckForSolutionFile()
		{
			SolutionFilePath = filesAndFolders.AsParallel()
				.Where(item => FileExtensionHelpers.HasExtension(item.FileExtension, ".sln", ".slnx"))
				.FirstOrDefault()?.ItemPath;
		}

		private void GetDesktopIniFileData()
		{
			var path = Path.Combine(WorkingDirectory, "desktop.ini");
			DesktopIni = WindowsIniService.GetData(path);
		}

		public void CheckForBackgroundImage()
		{
			if (WorkingDirectory == "Home" || WorkingDirectory == "ReleaseNotes" || WorkingDirectory != "Settings")
			{
				FolderBackgroundImageSource = null;
				return;
			}

			var filesAppSection = DesktopIni?.FirstOrDefault(x => x.SectionName == "FilesApp");
			if (filesAppSection is null || folderSettings.LayoutMode is FolderLayoutModes.ColumnView)
			{
				FolderBackgroundImageSource = null;
				return;
			}

			// Image source
			var backgroundImage = filesAppSection.Parameters.FirstOrDefault(x => x.Key == "Files_BackgroundImage").Value;
			if (string.IsNullOrEmpty(backgroundImage))
			{
				FolderBackgroundImageSource = null;
				return;
			}
			else
			{
				try
				{
					FolderBackgroundImageSource = new BitmapImage
					{
						UriSource = new Uri(backgroundImage, UriKind.RelativeOrAbsolute),
						CreateOptions = BitmapCreateOptions.IgnoreImageCache
					};
				}
				catch (Exception ex)
				{
					// Handle errors with setting the URI
					App.Logger.LogWarning(ex, ex.Message);
				}
			}

			// Opacity
			var backgroundOpacity = filesAppSection.Parameters.FirstOrDefault(x => x.Key == "Files_BackgroundOpacity").Value;
			if (float.TryParse(backgroundOpacity, out var opacity))
				FolderBackgroundImageOpacity = opacity;
			else
				FolderBackgroundImageOpacity = 1f;

			// Stretch
			var backgroundFit = filesAppSection.Parameters.FirstOrDefault(x => x.Key == "Files_BackgroundFit").Value;
			if (Enum.TryParse<Stretch>(backgroundFit, out var fit))
				FolderBackgroundImageFit = fit;
			else
				FolderBackgroundImageFit = Stretch.UniformToFill;

			// Vertical alignment
			var verticalAlignment = filesAppSection.Parameters.FirstOrDefault(x => x.Key == "Files_BackgroundVerticalAlignment").Value;
			if (Enum.TryParse<VerticalAlignment>(verticalAlignment, out var vAlignment))
				FolderBackgroundImageVerticalAlignment = vAlignment;
			else
				FolderBackgroundImageVerticalAlignment = VerticalAlignment.Center;

			// Horizontal alignment
			var horizontalAlignment = filesAppSection.Parameters.FirstOrDefault(x => x.Key == "Files_BackgroundHorizontalAlignment").Value;
			if (Enum.TryParse<HorizontalAlignment>(horizontalAlignment, out var hAlignment))
				FolderBackgroundImageHorizontalAlignment = hAlignment;
			else
				FolderBackgroundImageHorizontalAlignment = HorizontalAlignment.Center;
		}

		public async Task<CloudDriveSyncStatus> CheckCloudDriveSyncStatusAsync(IStorageItem item)
		{
			int? syncStatus = null;
			if (item is BaseStorageFile file && file.Properties is not null)
			{
				var extraProperties = await FilesystemTasks.Wrap(() => file.Properties.RetrievePropertiesAsync(["System.FilePlaceholderStatus"]).AsTask());
				if (extraProperties)
					syncStatus = (int?)(uint?)extraProperties.Result["System.FilePlaceholderStatus"];
			}
			else if (item is BaseStorageFolder folder && folder.Properties is not null)
			{
				var extraProperties = await FilesystemTasks.Wrap(() => folder.Properties.RetrievePropertiesAsync(["System.FilePlaceholderStatus", "System.FileOfflineAvailabilityStatus"]).AsTask());
				if (extraProperties)
				{
					syncStatus = (int?)(uint?)extraProperties.Result["System.FileOfflineAvailabilityStatus"];

					// If no FileOfflineAvailabilityStatus, check FilePlaceholderStatus
					syncStatus ??= (int?)(uint?)extraProperties.Result["System.FilePlaceholderStatus"];
				}
			}

			if (syncStatus is null || !Enum.IsDefined(typeof(CloudDriveSyncStatus), syncStatus))
				return CloudDriveSyncStatus.Unknown;

			return (CloudDriveSyncStatus)syncStatus;
		}

		private async Task<FilesystemResult<IDictionary<string, object>>?> GetExtraProperties(IStorageItem matchingStorageItem)
		{
			if (matchingStorageItem is BaseStorageFile file && file.Properties != null)
				return await FilesystemTasks.Wrap(() => file.Properties.RetrievePropertiesAsync(["System.Image.Dimensions", "System.Media.Duration", "System.FileVersion"]).AsTask());

			else if (matchingStorageItem is BaseStorageFolder folder && folder.Properties != null)
				return await FilesystemTasks.Wrap(() => folder.Properties.RetrievePropertiesAsync(["System.FreeSpace", "System.Capacity", "System.SFGAOFlags"]).AsTask());

			return null;
		}

		private async Task WatchForStorageFolderChangesAsync(BaseStorageFolder? rootFolder)
		{
			if (rootFolder is null)
				return;

			await Task.Factory.StartNew(() =>
			{
				var options = new QueryOptions()
				{
					FolderDepth = FolderDepth.Shallow,
					IndexerOption = IndexerOption.OnlyUseIndexerAndOptimizeForIndexedProperties
				};

				options.SetPropertyPrefetch(PropertyPrefetchOptions.None, null);
				options.SetThumbnailPrefetch(ThumbnailMode.ListView, 0, ThumbnailOptions.ReturnOnlyIfCached);

				if (rootFolder.AreQueryOptionsSupported(options))
				{
					var itemQueryResult = rootFolder.CreateItemQueryWithOptions(options).ToStorageItemQueryResult();
					itemQueryResult.ContentsChanged += ItemQueryResult_ContentsChanged;

					// Just get one item to start getting notifications
					var watchedItemsOperation = itemQueryResult.GetItemsAsync(0, 1);

					watcherCTS.Token.Register(() =>
					{
						itemQueryResult.ContentsChanged -= ItemQueryResult_ContentsChanged;
						watchedItemsOperation?.Cancel();
					});
				}
			},
			default,
			TaskCreationOptions.LongRunning,
			TaskScheduler.Default);
		}

		private void WatchForWin32FolderChanges(string? folderPath)
		{
			if (!Directory.Exists(folderPath))
				return;

			// NOTE: Suppressed NullReferenceException caused by EnableRaisingEvents
			SafetyExtensions.IgnoreExceptions(() =>
			{
				watcher = new FileSystemWatcher
				{
					Path = folderPath,
					Filter = "*.*",
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
				};

				watcher.Created += DirectoryWatcher_Changed;
				watcher.Deleted += DirectoryWatcher_Changed;
				watcher.Renamed += DirectoryWatcher_Changed;
				watcher.EnableRaisingEvents = true;
			}, App.Logger);
		}

		private async void DirectoryWatcher_Changed(object sender, FileSystemEventArgs e)
		{
			Debug.WriteLine($"Directory watcher event: {e.ChangeType}, {e.FullPath}");

			await SafeEnqueueOrInvokeAsync(() =>
			{
				RefreshItems(null);
			});
		}

		private async void ItemQueryResult_ContentsChanged(IStorageQueryResultBase sender, object args)
		{
			// Query options have to be reapplied otherwise old results are returned
			var options = new QueryOptions()
			{
				FolderDepth = FolderDepth.Shallow,
				IndexerOption = IndexerOption.OnlyUseIndexerAndOptimizeForIndexedProperties
			};

			options.SetPropertyPrefetch(PropertyPrefetchOptions.None, null);
			options.SetThumbnailPrefetch(ThumbnailMode.ListView, 0, ThumbnailOptions.ReturnOnlyIfCached);

			sender.ApplyNewQueryOptions(options);

			await SafeEnqueueOrInvokeAsync(() =>
			{
				RefreshItems(null);
			});
		}

		private void WatchForDirectoryChanges(string path, CloudDriveSyncStatus syncStatus)
		{
			Debug.WriteLine($"WatchForDirectoryChanges: {path}");
			var hWatchDir = Win32PInvoke.CreateFileFromApp(path, 1, 1 | 2 | 4,
				IntPtr.Zero, 3, (uint)Win32PInvoke.File_Attributes.BackupSemantics | (uint)Win32PInvoke.File_Attributes.Overlapped, IntPtr.Zero);
			if (hWatchDir.ToInt64() == -1)
				return;

			var hasSyncStatus = syncStatus != CloudDriveSyncStatus.NotSynced && syncStatus != CloudDriveSyncStatus.Unknown;

			aProcessQueueAction ??= Task.Factory.StartNew(() => ProcessOperationQueueAsync(watcherCTS.Token, hasSyncStatus), default,
				TaskCreationOptions.LongRunning, TaskScheduler.Default);

			var aWatcherAction = Windows.System.Threading.ThreadPool.RunAsync((x) =>
			{
				var buff = new byte[4096];
				var rand = Guid.NewGuid();
				var notifyFilters = FILE_NOTIFY_CHANGE_DIR_NAME | FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE;

				if (hasSyncStatus)
					notifyFilters |= FILE_NOTIFY_CHANGE_ATTRIBUTES;

				var overlapped = new OVERLAPPED();
				overlapped.hEvent = CreateEvent(IntPtr.Zero, false, false, null);
				const uint INFINITE = 0xFFFFFFFF;

				while (x.Status != AsyncStatus.Canceled)
				{
					unsafe
					{
						fixed (byte* pBuff = buff)
						{
							ref var notifyInformation = ref Unsafe.As<byte, FILE_NOTIFY_INFORMATION>(ref buff[0]);
							if (x.Status != AsyncStatus.Canceled)
							{
								ReadDirectoryChangesW(hWatchDir, pBuff,
								4096, false,
								notifyFilters, null,
								ref overlapped, null);
							}
							else
							{
								break;
							}

							Debug.WriteLine("waiting: {0}", rand);
							if (x.Status == AsyncStatus.Canceled)
								break;

							var rc = WaitForSingleObjectEx(overlapped.hEvent, INFINITE, true);
							Debug.WriteLine("wait done: {0}", rand);

							uint offset = 0;
							ref var notifyInfo = ref Unsafe.As<byte, FILE_NOTIFY_INFORMATION>(ref buff[offset]);
							if (x.Status == AsyncStatus.Canceled)
								break;

							do
							{
								notifyInfo = ref Unsafe.As<byte, FILE_NOTIFY_INFORMATION>(ref buff[offset]);
								string? FileName = null;
								unsafe
								{
									fixed (char* name = notifyInfo.FileName)
									{
										FileName = Path.Combine(path, new string(name, 0, (int)notifyInfo.FileNameLength / 2));
									}
								}

								uint action = notifyInfo.Action;

								Debug.WriteLine("action: {0}", action);

								operationQueue.Enqueue((action, FileName));

								offset += notifyInfo.NextEntryOffset;
							}
							while (notifyInfo.NextEntryOffset != 0 && x.Status != AsyncStatus.Canceled);

							operationEvent.Set();

							//ResetEvent(overlapped.hEvent);
							Debug.WriteLine("Task running...");
						}
					}
				}

				CloseHandle(overlapped.hEvent);
				operationQueue.Clear();

				Debug.WriteLine("aWatcherAction done: {0}", rand);
			});

			watcherCTS.Token.Register(() =>
			{
				if (aWatcherAction is not null)
				{
					aWatcherAction?.Cancel();

					// Prevent duplicate execution of this block
					aWatcherAction = null;

					Debug.WriteLine("watcher canceled");
				}

				CancelIoEx(hWatchDir, IntPtr.Zero);
				CloseHandle(hWatchDir);
			});
		}

		private void WatchForGitChanges()
		{
			var hWatchDir = Win32PInvoke.CreateFileFromApp(
				GitDirectory!,
				1,
				1 | 2 | 4,
				IntPtr.Zero,
				3,
				(uint)Win32PInvoke.File_Attributes.BackupSemantics | (uint)Win32PInvoke.File_Attributes.Overlapped,
				IntPtr.Zero);

			if (hWatchDir.ToInt64() == -1)
				return;

			gitProcessQueueAction ??= Task.Factory.StartNew(() => ProcessGitChangesQueueAsync(watcherCTS.Token), default,
				TaskCreationOptions.LongRunning, TaskScheduler.Default);

			var gitWatcherAction = Windows.System.Threading.ThreadPool.RunAsync((x) =>
			{
				var buff = new byte[4096];
				var rand = Guid.NewGuid();
				var notifyFilters = FILE_NOTIFY_CHANGE_DIR_NAME | FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE | FILE_NOTIFY_CHANGE_CREATION;

				var overlapped = new OVERLAPPED();
				overlapped.hEvent = CreateEvent(IntPtr.Zero, false, false, null);
				const uint INFINITE = 0xFFFFFFFF;

				while (x.Status != AsyncStatus.Canceled)
				{
					unsafe
					{
						fixed (byte* pBuff = buff)
						{
							ref var notifyInformation = ref Unsafe.As<byte, FILE_NOTIFY_INFORMATION>(ref buff[0]);
							if (x.Status == AsyncStatus.Canceled)
								break;

							ReadDirectoryChangesW(hWatchDir, pBuff,
								4096, true,
								notifyFilters, null,
								ref overlapped, null);

							if (x.Status == AsyncStatus.Canceled)
								break;

							var rc = WaitForSingleObjectEx(overlapped.hEvent, INFINITE, true);

							uint offset = 0;
							ref var notifyInfo = ref Unsafe.As<byte, FILE_NOTIFY_INFORMATION>(ref buff[offset]);
							if (x.Status == AsyncStatus.Canceled)
								break;

							do
							{
								notifyInfo = ref Unsafe.As<byte, FILE_NOTIFY_INFORMATION>(ref buff[offset]);

								uint action = notifyInfo.Action;

								gitChangesQueue.Enqueue(action);

								offset += notifyInfo.NextEntryOffset;
							}
							while (notifyInfo.NextEntryOffset != 0 && x.Status != AsyncStatus.Canceled);

							gitChangedEvent.Set();
						}
					}
				}

				CloseHandle(overlapped.hEvent);
				gitChangesQueue.Clear();
			});

			watcherCTS.Token.Register(() =>
			{
				if (gitWatcherAction is not null)
				{
					gitWatcherAction?.Cancel();

					// Prevent duplicate execution of this block
					gitWatcherAction = null;
				}

				CancelIoEx(hWatchDir, IntPtr.Zero);
				CloseHandle(hWatchDir);
			});
		}

		private async Task ProcessGitChangesQueueAsync(CancellationToken cancellationToken)
		{
			const int DELAY = 200;
			var sampler = new IntervalSampler(100);
			int changes = 0;

			try
			{
				while (!cancellationToken.IsCancellationRequested)
				{
					if (await gitChangedEvent.WaitAsync(DELAY, cancellationToken))
					{
						gitChangedEvent.Reset();
						while (gitChangesQueue.TryDequeue(out var _))
							++changes;

						if (changes != 0 && sampler.CheckNow())
						{
							await SafeEnqueueOrInvokeAsync(() => GitDirectoryUpdated?.Invoke(null, null!));
							changes = 0;
						}
					}
				}
			}
			catch { }
		}

		private async Task ProcessOperationQueueAsync(CancellationToken cancellationToken, bool hasSyncStatus)
		{
			const uint FILE_ACTION_ADDED = 0x00000001;
			const uint FILE_ACTION_REMOVED = 0x00000002;
			const uint FILE_ACTION_MODIFIED = 0x00000003;
			const uint FILE_ACTION_RENAMED_OLD_NAME = 0x00000004;
			const uint FILE_ACTION_RENAMED_NEW_NAME = 0x00000005;

			const int UPDATE_BATCH_SIZE = 32;
			var sampler = new IntervalSampler(200);
			var updateQueue = new Queue<string>();

			var anyEdits = false;
			ListedItem? lastItemAdded = null;
			var rand = Guid.NewGuid();

			// Call when any edits have occurred
			async Task HandleChangesOccurredAsync()
			{
				await OrderFilesAndFoldersAsync();
				await ApplyFilesAndFoldersChangesAsync();

				if (lastItemAdded is not null && !lastItemAdded.IsArchive)
				{
					await RequestSelectionAsync([lastItemAdded]);
					lastItemAdded = null;
				}

				anyEdits = false;
			}

			try
			{
				while (!cancellationToken.IsCancellationRequested)
				{
					if (await operationEvent.WaitAsync(200, cancellationToken))
					{
						operationEvent.Reset();

						while (operationQueue.TryDequeue(out var operation))
						{
							if (cancellationToken.IsCancellationRequested)
								break;

							try
							{
								switch (operation.Action)
								{
									case FILE_ACTION_ADDED:
									case FILE_ACTION_RENAMED_NEW_NAME:
										lastItemAdded = await AddFileOrFolderAsync(operation.FileName);
										if (lastItemAdded is not null)
											anyEdits = true;
										break;

									case FILE_ACTION_MODIFIED:
										if (!updateQueue.Contains(operation.FileName))
											updateQueue.Enqueue(operation.FileName);
										break;

									case FILE_ACTION_REMOVED:
										var itemRemoved = await RemoveFileOrFolderAsync(operation.FileName);
										if (itemRemoved is not null)
											anyEdits = true;
										break;

									case FILE_ACTION_RENAMED_OLD_NAME:
										var itemRenamedOld = await RemoveFileOrFolderAsync(operation.FileName);
										if (itemRenamedOld is not null)
											anyEdits = true;
										break;
								}
							}
							catch (Exception ex)
							{
								App.Logger.LogWarning(ex, ex.Message);
							}

							if (anyEdits && sampler.CheckNow())
								await HandleChangesOccurredAsync();
						}

						var itemsToUpdate = new List<string>();
						for (var i = 0; i < UPDATE_BATCH_SIZE && updateQueue.Count > 0; i++)
							itemsToUpdate.Add(updateQueue.Dequeue());

						await UpdateFilesOrFoldersAsync(itemsToUpdate, hasSyncStatus);
					}

					if (updateQueue.Count > 0)
					{
						var itemsToUpdate = new List<string>();
						for (var i = 0; i < UPDATE_BATCH_SIZE && updateQueue.Count > 0; i++)
							itemsToUpdate.Add(updateQueue.Dequeue());

						await UpdateFilesOrFoldersAsync(itemsToUpdate, hasSyncStatus);
					}

					if (anyEdits && sampler.CheckNow())
						await HandleChangesOccurredAsync();
				}
			}
			catch
			{
				// Prevent disposed cancellation token
			}

			Debug.WriteLine("aProcessQueueAction done: {0}", rand);
		}

		public Task<ListedItem> AddFileOrFolderFromShellFile(ShellFileItem item)
		{
			return
				item.IsFolder ?
				UniversalStorageEnumerator.AddFolderAsync(ShellStorageFolder.FromShellItem(item), currentStorageFolder, addFilesCTS.Token) :
				UniversalStorageEnumerator.AddFileAsync(ShellStorageFile.FromShellItem(item), currentStorageFolder, addFilesCTS.Token);
		}

		private async Task AddFileOrFolderAsync(ListedItem? item)
		{
			if (item is null)
				return;

			try
			{
				await enumFolderSemaphore.WaitAsync(semaphoreCTS.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			if (!filesAndFolders.Any(x => x.ItemPath.Equals(item.ItemPath, StringComparison.OrdinalIgnoreCase))) // Avoid adding duplicate items
			{
				filesAndFolders.Add(item);

				if (UserSettingsService.FoldersSettingsService.AreAlternateStreamsVisible)
				{
					// New file added, enumerate ADS
					foreach (var ads in Win32Helper.GetAlternateStreams(item.ItemPath))
					{
						var adsItem = Win32StorageEnumerator.GetAlternateStream(ads, item);
						filesAndFolders.Add(adsItem);
					}
				}
			}

			enumFolderSemaphore.Release();
		}

		private async Task<ListedItem?> AddFileOrFolderAsync(string fileOrFolderPath)
		{
			FINDEX_INFO_LEVELS findInfoLevel = FINDEX_INFO_LEVELS.FindExInfoBasic;
			var additionalFlags = FIND_FIRST_EX_CASE_SENSITIVE;

			IntPtr hFile = FindFirstFileExFromApp(fileOrFolderPath, findInfoLevel, out WIN32_FIND_DATA findData, FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero,
												  additionalFlags);
			if (hFile.ToInt64() == -1)
			{
				// If we cannot find the file (probably since it doesn't exist anymore) simply exit without adding it
				return null;
			}

			FindClose(hFile);

			var isSystem = ((FileAttributes)findData.dwFileAttributes & FileAttributes.System) == FileAttributes.System;
			var isHidden = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden;
			var startWithDot = findData.cFileName.StartsWith('.');
			if ((isHidden &&
			   (!UserSettingsService.FoldersSettingsService.ShowHiddenItems ||
			   (isSystem && !UserSettingsService.FoldersSettingsService.ShowProtectedSystemFiles))) ||
			   (startWithDot && !UserSettingsService.FoldersSettingsService.ShowDotFiles))
			{
				// Do not add to file list if hidden/system attribute is set and system/hidden file are not to be shown
				return null;
			}

			ListedItem listedItem;

			// FILE_ATTRIBUTE_DIRECTORY
			if ((findData.dwFileAttributes & 0x10) > 0)
				listedItem = await Win32StorageEnumerator.GetFolder(findData, Directory.GetParent(fileOrFolderPath).FullName, IsValidGitDirectory, addFilesCTS.Token);
			else
				listedItem = await Win32StorageEnumerator.GetFile(findData, Directory.GetParent(fileOrFolderPath).FullName, IsValidGitDirectory, addFilesCTS.Token);

			await AddFileOrFolderAsync(listedItem);

			return listedItem;
		}

		private async Task<(ListedItem Item, CloudDriveSyncStatus? SyncStatus, long? Size, DateTimeOffset Created, DateTimeOffset Modified)?> GetFileOrFolderUpdateInfoAsync(ListedItem item, bool hasSyncStatus)
		{
			IStorageItem? storageItem = null;
			if (item.PrimaryItemAttribute == StorageItemTypes.File)
				storageItem = (await GetFileFromPathAsync(item.ItemPath).ConfigureAwait(false)).Result;
			else if (item.PrimaryItemAttribute == StorageItemTypes.Folder)
				storageItem = (await GetFolderFromPathAsync(item.ItemPath).ConfigureAwait(false)).Result;

			if (storageItem is not null)
			{
				CloudDriveSyncStatus? syncStatus = hasSyncStatus ? await CheckCloudDriveSyncStatusAsync(storageItem) : null;
				long? size = null;
				DateTimeOffset created = default, modified = default;

				if (storageItem.IsOfType(StorageItemTypes.File))
				{
					var properties = await storageItem.AsBaseStorageFile().GetBasicPropertiesAsync();
					size = (long)properties.Size;
					modified = properties.DateModified;
					created = properties.DateCreated;
				}
				else if (storageItem.IsOfType(StorageItemTypes.Folder))
				{
					var properties = await storageItem.AsBaseStorageFolder().GetBasicPropertiesAsync();
					size = item.IsArchive ? (long)properties.Size : null;
					modified = properties.DateModified;
					created = properties.DateCreated;
				}

				return (item, syncStatus, size, created, modified);
			}

			return null;
		}

		private async Task UpdateFilesOrFoldersAsync(IEnumerable<string> paths, bool hasSyncStatus)
		{
			try
			{
				await enumFolderSemaphore.WaitAsync(semaphoreCTS.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				var matchingItems = filesAndFolders.Where(x => paths.Any(p => p.Equals(x.ItemPath, StringComparison.OrdinalIgnoreCase)));
				var results = await Task.WhenAll(matchingItems.Select(x => GetFileOrFolderUpdateInfoAsync(x, hasSyncStatus)));

				await SafeEnqueueOrInvokeAsync(() =>
				{
					foreach (var result in results)
					{
						if (result is not null)
						{
							var item = result.Value.Item;
							item.ItemDateModifiedReal = result.Value.Modified;
							item.ItemDateCreatedReal = result.Value.Created;

							if (result.Value.SyncStatus is not null)
								item.SyncStatusUI = CloudDriveSyncStatusUI.FromCloudDriveSyncStatus(result.Value.SyncStatus.Value);

							if (result.Value.Size is not null)
							{
								item.FileSizeBytes = result.Value.Size.Value;
								item.FileSize = item.FileSizeBytes.ToSizeString();
							}
						}
					}
				},
				Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
			}
			finally
			{
				enumFolderSemaphore.Release();
			}
		}

		public async Task<ListedItem?> RemoveFileOrFolderAsync(string path)
		{
			try
			{
				await enumFolderSemaphore.WaitAsync(semaphoreCTS.Token);
			}
			catch (OperationCanceledException)
			{
				return null;
			}

			try
			{
				var matchingItem = filesAndFolders.FirstOrDefault(x => x.ItemPath.Equals(path, StringComparison.OrdinalIgnoreCase));

				if (matchingItem is not null)
				{
					filesAndFolders.Remove(matchingItem);

					if (UserSettingsService.FoldersSettingsService.AreAlternateStreamsVisible)
					{
						// Main file is removed, remove connected ADS
						foreach (var adsItem in filesAndFolders.Where(x => x is AlternateStreamItem ads && ads.MainStreamPath == matchingItem.ItemPath).ToList())
							filesAndFolders.Remove(adsItem);
					}

					return matchingItem;
				}
			}
			finally
			{
				enumFolderSemaphore.Release();
			}

			return null;
		}

		public async Task AddSearchResultsToCollectionAsync(ObservableCollection<ListedItem> searchItems, string currentSearchPath)
		{
			filesAndFolders.Clear();
			filesAndFolders.AddRange(searchItems);

			await OrderFilesAndFoldersAsync().ConfigureAwait(false);
			await ApplyFilesAndFoldersChangesAsync().ConfigureAwait(false);
		}

		public async Task SearchAsync(FolderSearch search)
		{
			App.Logger?.LogInformation($"=== ShellViewModel.SearchAsync START === Query: {search.Query}");
			
			// Ensure UI updates happen on UI thread
			await SafeEnqueueOrInvokeAsync(() =>
			{
				ItemLoadStatusChanged?.Invoke(this, new ItemLoadStatusChangedEventArgs() { Status = ItemLoadStatusChangedEventArgs.ItemLoadStatus.Starting });
				IsLoadingItems = true;
				App.Logger?.LogInformation($"IsLoadingItems set to true for search");
			});

			CancelSearch();
			searchCTS = new CancellationTokenSource();
			filesAndFolders.Clear();
			IsSearchResults = true;
			HasNoWatcher = true;
			await ApplyFilesAndFoldersChangesAsync();
			EmptyTextType = EmptyTextType.None;

			ItemLoadStatusChanged?.Invoke(this, new ItemLoadStatusChangedEventArgs() { Status = ItemLoadStatusChangedEventArgs.ItemLoadStatus.InProgress });

			var results = new List<ListedItem>();
			// Batch search updates - only update UI every 200ms or 100 items
			// FIX: Incremental updates instead of replacing entire collection to preserve thumbnails
			var lastUpdateTime = DateTime.UtcNow;
			var lastUpdateCount = 0;
			
			search.SearchTick += async (s, e) =>
			{
				App.Logger?.LogInformation($"SearchTick fired with {results.Count} results so far");
				
				// Only update if 200ms have passed or we have 100+ new items
				var timeSinceLastUpdate = DateTime.UtcNow - lastUpdateTime;
				var newItemCount = results.Count - lastUpdateCount;
				
				if (timeSinceLastUpdate.TotalMilliseconds >= 200 || newItemCount >= 100)
				{
					lastUpdateTime = DateTime.UtcNow;
					
					// Add only the new items instead of replacing the entire collection
					var newItems = results.Skip(lastUpdateCount).ToList();
					foreach (var item in newItems)
					{
						filesAndFolders.Add(item);
					}
					
					lastUpdateCount = results.Count;
					
					await OrderFilesAndFoldersAsync();
					await ApplyFilesAndFoldersChangesAsync();
				}
			};

			try
			{
				App.Logger?.LogInformation("Starting search...");
				await search.SearchAsync(results, searchCTS.Token);
				App.Logger?.LogInformation($"Search completed with {results.Count} total results");
			}
			catch (OperationCanceledException)
			{
				App.Logger?.LogWarning("Search was cancelled");
			}
			catch (Exception ex)
			{
				App.Logger?.LogError(ex, "Search failed with error");
			}

			// Add any remaining items that weren't added during the search ticks
			var remainingItems = results.Skip(lastUpdateCount).ToList();
			foreach (var item in remainingItems)
			{
				filesAndFolders.Add(item);
			}

			await OrderFilesAndFoldersAsync().ConfigureAwait(false);
			await ApplyFilesAndFoldersChangesAsync().ConfigureAwait(false);

			// Load thumbnails for any remaining search results that weren't processed during search ticks
			if (remainingItems.Count > 0)
			{
				App.Logger?.LogInformation($"Preparing {remainingItems.Count} remaining search results for viewport loading");
				await PrepareSearchResultsForViewportLoadingAsync(remainingItems, searchCTS.Token);
			}

			App.Logger?.LogInformation($"Search complete, setting IsLoadingItems to false. Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
			
			// Ensure we're on the UI thread when updating loading status
			await SafeEnqueueOrInvokeAsync(async () =>
			{
				ItemLoadStatusChanged?.Invoke(this, new ItemLoadStatusChangedEventArgs() { Status = ItemLoadStatusChangedEventArgs.ItemLoadStatus.Complete });
				IsLoadingItems = false;
				App.Logger?.LogInformation($"IsLoadingItems set to false on UI thread");
				
				// Force a viewport update after search completes to ensure thumbnails load
				// This is needed because search results might be visible but not trigger viewport updates
				if (FilesAndFolders.Count > 0)
				{
					App.Logger?.LogInformation($"Forcing viewport update after search complete with {FilesAndFolders.Count} items");
					
					// Get the first batch of items that would be visible
					var initialBatch = FilesAndFolders.Take(50).ToList(); // Reasonable number for initial viewport
					
					// Trigger viewport update
					try
					{
						await UpdateViewportAsync(initialBatch, CancellationToken.None);
						App.Logger?.LogInformation($"Viewport update triggered successfully for {initialBatch.Count} items");
					}
					catch (Exception ex)
					{
						App.Logger?.LogError(ex, "Failed to trigger viewport update after search");
					}
				}
			});
		}

		public void CancelSearch()
		{
			if (searchCTS != null)
			{
				App.Logger?.LogInformation("Cancelling previous search");
				try
				{
					searchCTS.Cancel();
				}
				catch (ObjectDisposedException)
				{
					// Already disposed, ignore
				}
			}
		}



		/// <summary>
		/// Updates the viewport with currently visible items to prioritize their thumbnail loading
		/// </summary>
		/// <param name="visibleItems">Items currently visible in the viewport</param>
		/// <param name="cancellationToken">Cancellation token</param>
		public async Task UpdateViewportAsync(IEnumerable<ListedItem> visibleItems, CancellationToken cancellationToken = default)
		{
			var itemCount = visibleItems?.Count() ?? 0;
			App.Logger?.LogInformation($"[VIEWPORT] UpdateViewportAsync called with {itemCount} items. IsSearchResults: {IsSearchResults}, Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
			
			if (viewportThumbnailLoader == null)
			{
				App.Logger?.LogWarning("[VIEWPORT] ViewportThumbnailLoader is null, cannot update viewport");
				return;
			}

			try
			{
				// Ensure we're on a background thread to avoid blocking UI
				await Task.Run(async () =>
				{
					// Convert to list and deduplicate by path
					var uniqueItems = visibleItems
						.Where(item => item != null && !string.IsNullOrEmpty(item.ItemPath))
						.GroupBy(item => item.ItemPath)
						.Select(group => group.First())
						.ToList();

					if (uniqueItems.Count == 0)
						return;

					App.Logger?.LogInformation($"[VIEWPORT] Updating viewport with {uniqueItems.Count} unique items. First 3 paths: {string.Join(", ", uniqueItems.Take(3).Select(i => Path.GetFileName(i.ItemPath)))}");

					// Prioritize items that don't have thumbnails yet
					var priorityItems = uniqueItems
						.Where(item => item.FileImage == null && !item.ItemPropertiesInitialized)
						.Take(10) // Reduce to 10 to prevent overwhelming
						.ToList();
					
					App.Logger?.LogDebug($"[VIEWPORT] Found {priorityItems.Count} priority items without thumbnails");

					if (priorityItems.Count > 0)
					{
						var thumbnailSize = (uint)LayoutSizeKindHelper.GetIconSize(folderSettings.LayoutMode);
						
						// Ensure thumbnails are loaded with proper size, not just icons
						if (thumbnailSize < 48)
							thumbnailSize = 48; // Force minimum thumbnail size
						
						await viewportThumbnailLoader.UpdateViewportAsync(priorityItems, thumbnailSize, cancellationToken);
						App.Logger?.LogDebug($"[VIEWPORT] Viewport update completed for priority items");
					}
				}, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				// Expected during rapid scrolling
			}
			catch (Exception ex)
			{
				App.Logger?.LogDebug(ex, "Error updating viewport for thumbnail loading");
			}
		}

		/// <summary>
		/// Preloads thumbnails for items near the viewport
		/// </summary>
		/// <param name="itemsNearViewport">Items that are near but not in the viewport</param>
		/// <param name="cancellationToken">Cancellation token</param>
		public async Task PreloadNearViewportAsync(IEnumerable<ListedItem> itemsNearViewport, CancellationToken cancellationToken = default)
		{
			if (viewportThumbnailLoader == null)
				return;

			try
			{
				var thumbnailSize = (uint)LayoutSizeKindHelper.GetIconSize(folderSettings.LayoutMode);
				await viewportThumbnailLoader.PreloadNearViewportAsync(itemsNearViewport, thumbnailSize, cancellationToken);
			}
			catch (Exception ex)
			{
				App.Logger?.LogDebug(ex, "Failed to preload thumbnails near viewport");
			}
		}

		/// <summary>
		/// Clears the viewport and cancels pending thumbnail loads
		/// </summary>
		public void ClearViewport()
		{
			viewportThumbnailLoader?.ClearViewport();
		}

		public void UpdateDateDisplay(bool isFormatChange)
		{
			var dateUpdateTask = Task.Run(async () =>
			{
				try
				{
					await Task.WhenAll(filesAndFolders.Select(async item =>
					{
						// Reassign values to update date display
						if (isFormatChange || IsDateDiff(item.ItemDateAccessedReal))
							await SafeEnqueueOrInvokeAsync(() => item.ItemDateAccessedReal = item.ItemDateAccessedReal).ConfigureAwait(false);
						if (isFormatChange || IsDateDiff(item.ItemDateCreatedReal))
							await SafeEnqueueOrInvokeAsync(() => item.ItemDateCreatedReal = item.ItemDateCreatedReal).ConfigureAwait(false);
						if (isFormatChange || IsDateDiff(item.ItemDateModifiedReal))
							await SafeEnqueueOrInvokeAsync(() => item.ItemDateModifiedReal = item.ItemDateModifiedReal).ConfigureAwait(false);
						if (item is RecycleBinItem recycleBinItem && (isFormatChange || IsDateDiff(recycleBinItem.ItemDateDeletedReal)))
							await SafeEnqueueOrInvokeAsync(() => recycleBinItem.ItemDateDeletedReal = recycleBinItem.ItemDateDeletedReal).ConfigureAwait(false);
						if (item is IGitItem gitItem && gitItem.GitLastCommitDate is DateTimeOffset offset && (isFormatChange || IsDateDiff(offset)))
							await SafeEnqueueOrInvokeAsync(() => gitItem.GitLastCommitDate = gitItem.GitLastCommitDate).ConfigureAwait(false);
					})).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Error updating date display");
				}
			});

			// Handle task exceptions properly
			_ = dateUpdateTask.ContinueWith(task =>
			{
				if (task.IsFaulted)
				{
					App.Logger?.LogError(task.Exception, "Unhandled error in date update task");
				}
			}, TaskScheduler.Default);
		}

		private static bool IsDateDiff(DateTimeOffset offset) => (DateTimeOffset.Now - offset).TotalDays < 7;

		public void Dispose()
		{
			CancelLoadAndClearFiles();
			StorageTrashBinService.Watcher.ItemAdded -= RecycleBinItemCreatedAsync;
			StorageTrashBinService.Watcher.ItemDeleted -= RecycleBinItemDeletedAsync;
			StorageTrashBinService.Watcher.RefreshRequested -= RecycleBinRefreshRequestedAsync;
			UserSettingsService.OnSettingChangedEvent -= UserSettingsService_OnSettingChangedEvent;
			fileTagsSettingsService.OnSettingImportedEvent -= FileTagsSettingsService_OnSettingUpdated;
			fileTagsSettingsService.OnTagsUpdated -= FileTagsSettingsService_OnSettingUpdated;
			folderSizeProvider.SizeChanged -= FolderSizeProvider_SizeChanged;
			folderSettings.LayoutModeChangeRequested -= LayoutModeChangeRequested;
			
			// Dispose all semaphores to prevent resource leaks
			enumFolderSemaphore?.Dispose();
			getFileOrFolderSemaphore?.Dispose();
			bulkOperationSemaphore?.Dispose();
			loadThumbnailSemaphore?.Dispose();
			applyChangesSemaphore?.Dispose();
			
			// Dispose cancellation token sources
			addFilesCTS?.Dispose();
			semaphoreCTS?.Dispose();
			loadPropsCTS?.Dispose();
			watcherCTS?.Dispose();
			searchCTS?.Dispose();
			updateTagGroupCTS?.Dispose();
			applyChangesCTS?.Dispose();
			
			// Note: AsyncManualResetEvent doesn't implement IDisposable, so no disposal needed
			
			// Stop and dispose debounce timer
			applyChangesDebounceTimer?.Stop();
			
			// Clean up viewport thumbnail loader
			if (viewportThumbnailLoader is IDisposable disposableLoader)
			{
				disposableLoader.Dispose();
			}
		}

		/// <summary>
		/// Prepares search results for viewport-based thumbnail loading (no bulk loading)
		/// </summary>
		private async Task PrepareSearchResultsForViewportLoadingAsync(List<ListedItem> searchResults, CancellationToken cancellationToken)
		{
			if (searchResults == null || searchResults.Count == 0)
				return;

			try
			{
				App.Logger?.LogInformation($"[SEARCH-PREP] Preparing {searchResults.Count} search results for viewport-based loading");
				
				// Log sample of items before modification
				if (searchResults.Count > 0)
				{
					var sample = searchResults.First();
					App.Logger?.LogDebug($"[SEARCH-PREP] Sample item before: Path={sample.ItemPath}, LoadFileIcon={sample.LoadFileIcon}, HasThumbnail={sample.FileImage != null}, NeedsPlaceholder={sample.NeedsPlaceholderGlyph}");
				}
				
				// Simply ensure all items are properly configured for viewport-based loading
				// Don't set LoadFileIcon=true to avoid triggering individual loads
				await Task.Run(() =>
				{
					foreach (var item in searchResults)
					{
						if (cancellationToken.IsCancellationRequested)
							break;
							
						try
						{
							// Ensure item is ready for loading
							// IMPORTANT: Keep LoadFileIcon=true from search service to allow hybrid loading
							// The viewport system will handle batch loading, but individual items can still load if needed
							if (!item.LoadFileIcon)
							{
								item.LoadFileIcon = true; // Enable thumbnail loading
							}
							
							// Don't reset ItemPropertiesInitialized or NeedsPlaceholderGlyph
							// Let the natural loading process handle these
							
							// Don't clear existing thumbnails - they might be cached
						}
						catch (Exception ex)
						{
							App.Logger?.LogDebug(ex, "Error preparing item {Path} for viewport loading", item.ItemPath);
						}
					}
				}, cancellationToken);
				
				// Log sample of items after modification
				if (searchResults.Count > 0)
				{
					var sample = searchResults.First();
					App.Logger?.LogDebug($"[SEARCH-PREP] Sample item after: Path={sample.ItemPath}, LoadFileIcon={sample.LoadFileIcon}, HasThumbnail={sample.FileImage != null}, NeedsPlaceholder={sample.NeedsPlaceholderGlyph}");
				}
				
				App.Logger?.LogInformation($"[SEARCH-PREP] Prepared {searchResults.Count} search results for viewport loading");
			}
			catch (OperationCanceledException)
			{
				App.Logger?.LogInformation("Search result preparation was cancelled");
			}
			catch (Exception ex)
			{
				App.Logger?.LogError(ex, "Error preparing search results for viewport loading");
			}
		}
	}

	public sealed class PageTypeUpdatedEventArgs
	{
		public bool IsTypeCloudDrive { get; set; }

		public bool IsTypeRecycleBin { get; set; }

		public bool IsTypeGitRepository { get; set; }

		public bool IsTypeSearchResults { get; set; }
	}

	public sealed class WorkingDirectoryModifiedEventArgs : EventArgs
	{
		public string? Path { get; set; }

		public string? Name { get; set; }

		public bool IsLibrary { get; set; }
	}

	public sealed class ItemLoadStatusChangedEventArgs : EventArgs
	{
		public enum ItemLoadStatus
		{
			Starting,
			InProgress,
			Complete
		}

		public ItemLoadStatus Status { get; set; }

		/// <summary>
		/// This property may not be provided consistently if Status is not Complete
		/// </summary>
		public string? PreviousDirectory { get; set; }

		/// <summary>
		/// This property may not be provided consistently if Status is not Complete
		/// </summary>
		public string? Path { get; set; }
	}

	public enum GitProperties
	{
		None,
		Status,
		Commit,
		All,
	}
}
