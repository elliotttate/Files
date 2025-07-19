// Copyright (c) Files Community
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Vanara.PInvoke;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;

namespace Files.App.Utils.Storage;

public sealed partial class SystemStorageFolder : BaseStorageFolder
{
	public StorageFolder Folder { get; }

	public override string Path => Folder.Path;
	public override string Name => Folder.Name;
	public override string DisplayName => Folder.DisplayName;
	public override string DisplayType => Folder.DisplayType;
	public override string FolderRelativeId => Folder.FolderRelativeId;

	public override DateTimeOffset DateCreated => Folder.DateCreated;
	public override Windows.Storage.FileAttributes Attributes => Folder.Attributes;
	public override IStorageItemExtraProperties Properties => Folder.Properties;

	public SystemStorageFolder(StorageFolder folder) => Folder = folder ?? throw new ArgumentNullException(nameof(folder));

	/// <summary>
	/// Gets dynamically resolved problematic Windows system paths that should be skipped.
	/// These paths often cause COM exceptions or access denied errors.
	/// </summary>
	private static string[] GetProblematicWindowsPaths()
	{
		var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		return new[]
		{
			System.IO.Path.Combine(windowsDir, "TAPI"),
			System.IO.Path.Combine(windowsDir, "assembly"),
			System.IO.Path.Combine(windowsDir, "Installer"),
			System.IO.Path.Combine(windowsDir, "WinSxS"),
			System.IO.Path.Combine(windowsDir, "servicing"),
			System.IO.Path.Combine(windowsDir, "CSC"),
			System.IO.Path.Combine(windowsDir, "Resources"),
			System.IO.Path.Combine(windowsDir, "Temp")
		};
	}

	/// <summary>
	/// Performs a fast access check on a path using Win32 API.
	/// </summary>
	/// <returns>True if the path is accessible, false otherwise.</returns>
	private static bool TryFastAccessCheck(string path)
	{
		try
		{
			// Use Vanara's GetFileAttributesEx for a lightweight access check
			if (Kernel32.GetFileAttributesEx(path, Kernel32.GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard, out var fileData))
			{
				return true;
			}

			var error = Win32Error.GetLastError();
			if (error == Win32Error.ERROR_ACCESS_DENIED)
			{
				App.Logger?.LogInformation("Access denied to path {Path}", path);
			}
			else if (error == Win32Error.ERROR_FILE_NOT_FOUND || error == Win32Error.ERROR_PATH_NOT_FOUND)
			{
				App.Logger?.LogInformation("Path not found {Path}", path);
			}
			else
			{
				App.Logger?.LogInformation("Cannot access path {Path}: {Error}", path, error);
			}
			return false;
		}
		catch (Exception ex)
		{
			App.Logger?.LogWarning(ex, "Exception during fast access check for path {Path}", path);
			return false;
		}
	}

	/// <summary>
	/// Checks if a path is known to be problematic for Windows Storage API access.
	/// </summary>
	private static bool IsProblematicPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			App.Logger?.LogInformation("Empty or whitespace path provided.");
			return true;
		}

		var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		
		// Check for problematic GUID paths in Windows directory (e.g., C:\Windows\{GUID})
		if (path.StartsWith(System.IO.Path.Combine(windowsDir, "{"), StringComparison.OrdinalIgnoreCase) && 
			path.EndsWith("}", StringComparison.OrdinalIgnoreCase))
		{
			App.Logger?.LogWarning("Skipping problematic Windows GUID path {Path}", path);
			return true;
		}

		// Check for known problematic paths
		var problematicPaths = GetProblematicWindowsPaths();
		if (problematicPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
		{
			App.Logger?.LogInformation("Skipping known problematic Windows system path {Path}", path);
			return true;
		}

		// Check for root drives (e.g., "C:\", "D:\") which often fail due to UWP sandboxing
		if (System.IO.Path.GetPathRoot(path) == path)
		{
			App.Logger?.LogInformation("Skipping root drive path (access often denied in UWP) {Path}", path);
			return true;
		}

		// Check for problematic folder patterns that often cause COM exceptions
		var folderName = System.IO.Path.GetFileName(path);
		var problematicFolders = new[]
		{
			".github",      // GitHub metadata folder
			".git",         // Git repository folder
			".svn",         // Subversion folder
			".hg",          // Mercurial folder
			".bzr",         // Bazaar folder
			"node_modules", // Node.js dependencies (can be very deep)
			".vs",          // Visual Studio folder
			".vscode",      // VS Code folder
			"__pycache__",  // Python cache folder
			".pytest_cache", // Pytest cache
			".mypy_cache",  // MyPy cache
			".tox",         // Tox virtual environments
			".cache",       // Generic cache folders
			"$RECYCLE.BIN", // Recycle bin
			"System Volume Information", // System folder
			"Recovery",     // Windows recovery
			"Config.Msi"    // Windows installer cache
		};

		if (problematicFolders.Any(folder => string.Equals(folderName, folder, StringComparison.OrdinalIgnoreCase)))
		{
			App.Logger?.LogInformation("Skipping problematic folder pattern {FolderName} in path {Path}", folderName, path);
			return true;
		}

		return false;
	}

	/// <summary>
	/// Verifies that a directory exists and is accessible using fast Win32 API check.
	/// </summary>
	private static bool IsAccessibleDirectory(string path)
	{
		// Use fast Win32 API check instead of expensive enumeration
		if (!TryFastAccessCheck(path))
		{
			return false;
		}

		try
		{
			// Additional check for reparse points if needed
			if (Kernel32.GetFileAttributesEx(path, Kernel32.GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard, out var fileData))
			{
				// Check if it's actually a directory
				if (!fileData.dwFileAttributes.HasFlag(FileFlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY))
				{
					App.Logger?.LogInformation("Path is not a directory {Path}", path);
					return false;
				}

				// Optional: Skip reparse points (symlinks/junctions)
				if (fileData.dwFileAttributes.HasFlag(FileFlagsAndAttributes.FILE_ATTRIBUTE_REPARSE_POINT))
				{
					App.Logger?.LogInformation("Skipping reparse point {Path}", path);
					return false;
				}
			}
			
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.LogInformation("Cannot verify directory attributes for {Path}: {Message}", path, ex.Message);
			return false;
		}
	}

	private static bool HandleStorageException(Exception ex, string path)
	{
		switch (ex)
		{
			case UnauthorizedAccessException:
			case FileNotFoundException:
				return true;
				
			case System.Runtime.InteropServices.COMException comEx when comEx.HResult is unchecked((int)0x80070005) or unchecked((int)0xC000027B):
				App.Logger?.LogWarning(comEx, "Known COM exception accessing folder {Path} {HResult}", path, $"0x{comEx.HResult:X8}");
				return true;
				
			case System.Runtime.InteropServices.COMException comEx:
				App.Logger?.LogWarning(comEx, "COM exception accessing folder {Path} {HResult}", path, $"0x{comEx.HResult:X8}");
				return true;
				
			case Exception generalEx when generalEx.HResult == unchecked((int)0xC000027B):
				App.Logger?.LogWarning(generalEx, "Application-internal exception accessing folder {Path}", path);
				return true;
				
			default:
				App.Logger?.LogWarning(ex, "Failed to get folder from path {Path}", path);
				return true;
		}
	}

	/// <summary>
	/// Handles library (.library-ms) file paths by resolving to the library's default save folder.
	/// </summary>
	private static IAsyncOperation<BaseStorageFolder> GetLibraryFolderAsync(string path)
	{
		return AsyncInfo.Run<BaseStorageFolder>(async (cancellationToken) =>
		{
			try
			{
				using var shellItem = new ShellLibraryEx(Shell32.ShellUtil.GetShellItemForPath(path), true);
				if (shellItem is ShellLibraryEx library)
				{
					var libraryItem = ShellFolderExtensions.GetShellLibraryItem(library, path);
					
					// TODO: Use library's default save folder if available (libraryItem?.DefaultSaveFolder)
					// For now, fall back to first folder
					var targetFolder = libraryItem?.Folders.FirstOrDefault();

					if (targetFolder != null)
					{
						try
						{
							var folder = await StorageFolder.GetFolderFromPathAsync(targetFolder).AsTask().ConfigureAwait(false);
							return new SystemStorageFolder(folder);
						}
						catch (Exception ex)
						{
							App.Logger?.LogWarning(ex, "Failed to access library folder {Path}", targetFolder);
							return null;
						}
					}
					else
					{
						App.Logger?.LogWarning("No folders found in library {Path}", path);
					}
				}
			}
			catch (Exception e)
			{
				App.Logger?.LogWarning(e, "Failed to process library path {Path}", path);
			}
			
			// Library handling failed, return null (don't fall through to regular folder handling)
			return null;
		});
	}

	/// <summary>
	/// Creates a SystemStorageFolder from a file system path.
	/// Returns null if the path is invalid, inaccessible, or known to be problematic.
	/// </summary>
	public static IAsyncOperation<BaseStorageFolder> FromPathAsync(string path)
	{
		// Validate and normalize path
		if (string.IsNullOrWhiteSpace(path))
		{
			App.Logger?.LogInformation("Empty or whitespace path provided to FromPathAsync.");
			return Task.FromResult<BaseStorageFolder>(null).AsAsyncOperation();
		}

		try
		{
			// Normalize path (handles relative paths, trailing slashes, etc.)
			path = System.IO.Path.GetFullPath(path);
		}
		catch (Exception ex)
		{
			App.Logger?.LogWarning(ex, "Invalid path format {Path}", path);
			return Task.FromResult<BaseStorageFolder>(null).AsAsyncOperation();
		}

		// Handle library paths (.library-ms files)
		if (path.EndsWith(ShellLibraryItem.EXTENSION, StringComparison.OrdinalIgnoreCase))
		{
			return GetLibraryFolderAsync(path);
		}

		// Handle regular folder paths
		return AsyncInfo.Run<BaseStorageFolder>(async (cancellationToken) =>
		{
			// Skip known problematic paths
			if (IsProblematicPath(path))
				return null;

			// Verify directory exists and is accessible
			if (!IsAccessibleDirectory(path))
				return null;

			try
			{
				// Double-check that the path still exists right before the API call
				if (!Directory.Exists(path))
				{
					App.Logger?.LogInformation("Directory no longer exists before StorageFolder call {Path}", path);
					return null;
				}

				// Enhanced safety check for paths that might cause 0xC000027B
				if (IsLikelyToTriggerStorageApiException(path))
				{
					App.Logger?.LogInformation("Skipping path that may cause StorageFolder API exception {Path}", path);
					return null;
				}

				StorageFolder? folder;
				// Add timeout to prevent hanging on problematic paths
				using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
				
				try
				{
					folder = await StorageFolder.GetFolderFromPathAsync(path).AsTask(combinedCts.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
				{
					App.Logger?.LogWarning("StorageFolder API timeout for path {Path}", path);
					return null;
				}
				catch (System.Runtime.InteropServices.COMException comEx) when (comEx.HResult == unchecked((int)0xC000027B))
				{
					// This specific error code is causing the crash - handle it explicitly
					App.Logger?.LogWarning("Application-internal COM exception (0xC000027B) for path {Path}", path);
					return null;
				}
				catch (Exception ex) when (ex.HResult == unchecked((int)0xC000027B))
				{
					// Sometimes wrapped differently
					App.Logger?.LogWarning("Application-internal exception (0xC000027B) for path {Path}", path);
					return null;
				}

				return folder != null ? new SystemStorageFolder(folder) : null;
			}
			catch (Exception ex) when (HandleStorageException(ex, path))
			{
				return null;
			}
		});
	}

	/// <summary>
	/// Enhanced check for paths that are likely to trigger Windows Storage API exceptions.
	/// This includes broader pattern matching and heuristics to catch problematic paths.
	/// </summary>
	private static bool IsLikelyToTriggerStorageApiException(string path)
	{
		try
		{
			// Check for Git-related paths (existing check)
			if (path.Contains(".git", StringComparison.OrdinalIgnoreCase) || 
				path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			// Check for paths that contain development/project-related keywords that often cause issues
			var problematicKeywords = new[]
			{
				"node_modules", "target", "build", "dist", "out", "bin", "obj",
				".vs", ".vscode", ".idea", ".gradle", ".m2", ".nuget",
				"__pycache__", ".pytest_cache", ".mypy_cache", ".tox",
				"vendor", "packages", "deps", "dependencies",
				"temp", "tmp", "cache", ".cache", ".temp"
			};

			var pathSegments = path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
			foreach (var segment in pathSegments)
			{
				if (problematicKeywords.Any(keyword => 
					string.Equals(segment, keyword, StringComparison.OrdinalIgnoreCase) ||
					segment.StartsWith(keyword + ".", StringComparison.OrdinalIgnoreCase)))
				{
					return true;
				}
			}

			// Check for very deep paths (more than 15 levels) which can cause issues
			if (pathSegments.Length > 15)
			{
				App.Logger?.LogInformation("Skipping very deep path {Path} with {Depth} levels", path, pathSegments.Length);
				return true;
			}

			// Check for paths with very long names (individual segments > 100 characters)
			if (pathSegments.Any(segment => segment.Length > 100))
			{
				return true;
			}

			// Check if path contains special characters that might cause issues
			var fileName = System.IO.Path.GetFileName(path);
			if (!string.IsNullOrEmpty(fileName))
			{
				// Check for unusual characters that might cause COM exceptions
				var problematicChars = new[] { '[', ']', '{', '}', '|', '<', '>', '?', '*', '"' };
				if (fileName.IndexOfAny(problematicChars) >= 0)
				{
					return true;
				}

				// Check for names that are all numbers or very short cryptic names
				if (fileName.All(char.IsDigit) || (fileName.Length <= 3 && fileName.All(char.IsLower)))
				{
					return true;
				}
			}

			// Check for paths under program files or system directories that often have restrictions
			var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			
			if (path.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) ||
				path.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase) ||
				path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
			{
				// Allow only specific safe subdirectories
				var safeProgramSubdirs = new[] { "WindowsPowerShell", "Microsoft", "Common Files" };
				if (!safeProgramSubdirs.Any(safe => path.Contains(System.IO.Path.Combine(programFiles, safe), StringComparison.OrdinalIgnoreCase) ||
												   path.Contains(System.IO.Path.Combine(programFilesX86, safe), StringComparison.OrdinalIgnoreCase)))
				{
					return true;
				}
			}

			return false;
		}
		catch (Exception ex)
		{
			App.Logger?.LogWarning(ex, "Exception in IsLikelyToTriggerStorageApiException for path {Path}", path);
			return true; // If we can't safely check, assume it's problematic
		}
	}

	public override IAsyncOperation<StorageFolder> ToStorageFolderAsync() => Task.FromResult(Folder).AsAsyncOperation();

	public override bool IsEqual(IStorageItem item) => Folder.IsEqual(item);
	public override bool IsOfType(StorageItemTypes type) => Folder.IsOfType(type);

	public override IAsyncOperation<BaseStorageFolder> GetParentAsync()
		=> AsyncInfo.Run<BaseStorageFolder>(async (cancellationToken) =>
		{
			try
			{
				var parent = await Folder.GetParentAsync().AsTask().ConfigureAwait(false);
				return parent != null ? new SystemStorageFolder(parent) : null;
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "Failed to get parent folder for {Path}", Path);
				return null;
			}
		});
	public override IAsyncOperation<BaseBasicProperties> GetBasicPropertiesAsync()
		=> AsyncInfo.Run<BaseBasicProperties>(async (cancellationToken) => 
			new SystemFolderBasicProperties(await Folder.GetBasicPropertiesAsync().AsTask().ConfigureAwait(false), DateCreated));

	public override IAsyncOperation<IndexedState> GetIndexedStateAsync() => Folder.GetIndexedStateAsync();

	public override IAsyncOperation<IStorageItem> GetItemAsync(string name)
		=> Folder.GetItemAsync(name);
	public override IAsyncOperation<IStorageItem> TryGetItemAsync(string name)
		=> Folder.TryGetItemAsync(name);
	public override IAsyncOperation<IReadOnlyList<IStorageItem>> GetItemsAsync()
		=> Folder.GetItemsAsync();
	public override IAsyncOperation<IReadOnlyList<IStorageItem>> GetItemsAsync(uint startIndex, uint maxItemsToRetrieve)
		=> Folder.GetItemsAsync(startIndex, maxItemsToRetrieve);

	public override IAsyncOperation<BaseStorageFile> GetFileAsync(string name)
		=> AsyncInfo.Run<BaseStorageFile>(async (cancellationToken) => new SystemStorageFile(await Folder.GetFileAsync(name)));
	public override IAsyncOperation<IReadOnlyList<BaseStorageFile>> GetFilesAsync()
		=> AsyncInfo.Run<IReadOnlyList<BaseStorageFile>>(async (cancellationToken) =>
		{
			var files = await Folder.GetFilesAsync().AsTask().ConfigureAwait(false);
			var result = new BaseStorageFile[files.Count];
			for (int i = 0; i < files.Count; i++)
			{
				result[i] = new SystemStorageFile(files[i]);
			}
			return result;
		});
	public override IAsyncOperation<IReadOnlyList<BaseStorageFile>> GetFilesAsync(CommonFileQuery query)
		=> AsyncInfo.Run<IReadOnlyList<BaseStorageFile>>(async (cancellationToken)
			=> (await Folder.GetFilesAsync(query)).Select(x => new SystemStorageFile(x)).ToList());
	public override IAsyncOperation<IReadOnlyList<BaseStorageFile>> GetFilesAsync(CommonFileQuery query, uint startIndex, uint maxItemsToRetrieve)
		=> AsyncInfo.Run<IReadOnlyList<BaseStorageFile>>(async (cancellationToken)
			=> (await Folder.GetFilesAsync(query, startIndex, maxItemsToRetrieve)).Select(x => new SystemStorageFile(x)).ToList());

	public override IAsyncOperation<BaseStorageFolder> GetFolderAsync(string name)
		=> AsyncInfo.Run<BaseStorageFolder>(async (cancellationToken) => new SystemStorageFolder(await Folder.GetFolderAsync(name)));
	public override IAsyncOperation<IReadOnlyList<BaseStorageFolder>> GetFoldersAsync()
		=> AsyncInfo.Run<IReadOnlyList<BaseStorageFolder>>(async (cancellationToken) =>
		{
			var folders = await Folder.GetFoldersAsync().AsTask().ConfigureAwait(false);
			var result = new BaseStorageFolder[folders.Count];
			for (int i = 0; i < folders.Count; i++)
			{
				result[i] = new SystemStorageFolder(folders[i]);
			}
			return result;
		});
	public override IAsyncOperation<IReadOnlyList<BaseStorageFolder>> GetFoldersAsync(CommonFolderQuery query)
		=> AsyncInfo.Run<IReadOnlyList<BaseStorageFolder>>(async (cancellationToken)
			=> (await Folder.GetFoldersAsync(query)).Select(x => new SystemStorageFolder(x)).ToList());
	public override IAsyncOperation<IReadOnlyList<BaseStorageFolder>> GetFoldersAsync(CommonFolderQuery query, uint startIndex, uint maxItemsToRetrieve)
		=> AsyncInfo.Run<IReadOnlyList<BaseStorageFolder>>(async (cancellationToken)
			=> (await Folder.GetFoldersAsync(query, startIndex, maxItemsToRetrieve)).Select(x => new SystemStorageFolder(x)).ToList());

	public override IAsyncOperation<BaseStorageFile> CreateFileAsync(string desiredName)
		=> AsyncInfo.Run<BaseStorageFile>(async (cancellationToken) => new SystemStorageFile(await Folder.CreateFileAsync(desiredName)));
	public override IAsyncOperation<BaseStorageFile> CreateFileAsync(string desiredName, CreationCollisionOption options)
		=> AsyncInfo.Run<BaseStorageFile>(async (cancellationToken) => new SystemStorageFile(await Folder.CreateFileAsync(desiredName, options)));

	public override IAsyncOperation<BaseStorageFolder> CreateFolderAsync(string desiredName)
		=> AsyncInfo.Run<BaseStorageFolder>(async (cancellationToken) => new SystemStorageFolder(await Folder.CreateFolderAsync(desiredName)));
	public override IAsyncOperation<BaseStorageFolder> CreateFolderAsync(string desiredName, CreationCollisionOption options)
		=> AsyncInfo.Run<BaseStorageFolder>(async (cancellationToken) => new SystemStorageFolder(await Folder.CreateFolderAsync(desiredName, options)));

	/// <summary>
	/// Move operations are not supported in this wrapper implementation.
	/// Use copy + delete operations if needed.
	/// </summary>
	public override IAsyncOperation<BaseStorageFolder> MoveAsync(IStorageFolder destinationFolder) => throw new NotSupportedException("Folder move operations are not supported in this wrapper.");
	public override IAsyncOperation<BaseStorageFolder> MoveAsync(IStorageFolder destinationFolder, NameCollisionOption option) => throw new NotSupportedException("Folder move operations are not supported in this wrapper.");

	public override IAsyncAction RenameAsync(string desiredName) => Folder.RenameAsync(desiredName);
	public override IAsyncAction RenameAsync(string desiredName, NameCollisionOption option) => Folder.RenameAsync(desiredName, option);

	public override IAsyncAction DeleteAsync() => Folder.DeleteAsync();
	public override IAsyncAction DeleteAsync(StorageDeleteOption option) => Folder.DeleteAsync(option);

	public override bool AreQueryOptionsSupported(QueryOptions queryOptions) => Folder.AreQueryOptionsSupported(queryOptions);
	public override bool IsCommonFileQuerySupported(CommonFileQuery query) => Folder.IsCommonFileQuerySupported(query);
	public override bool IsCommonFolderQuerySupported(CommonFolderQuery query) => Folder.IsCommonFolderQuerySupported(query);

	public override StorageItemQueryResult CreateItemQuery()
		=> Folder.CreateItemQuery();
	public override BaseStorageItemQueryResult CreateItemQueryWithOptions(QueryOptions queryOptions)
		=> new SystemStorageItemQueryResult(Folder.CreateItemQueryWithOptions(queryOptions));

	public override StorageFileQueryResult CreateFileQuery()
		=> Folder.CreateFileQuery();
	public override StorageFileQueryResult CreateFileQuery(CommonFileQuery query)
		=> Folder.CreateFileQuery(query);
	public override BaseStorageFileQueryResult CreateFileQueryWithOptions(QueryOptions queryOptions)
		=> new SystemStorageFileQueryResult(Folder.CreateFileQueryWithOptions(queryOptions));

	public override StorageFolderQueryResult CreateFolderQuery()
		=> Folder.CreateFolderQuery();
	public override StorageFolderQueryResult CreateFolderQuery(CommonFolderQuery query)
		=> Folder.CreateFolderQuery(query);
	public override BaseStorageFolderQueryResult CreateFolderQueryWithOptions(QueryOptions queryOptions)
		=> new SystemStorageFolderQueryResult(Folder.CreateFolderQueryWithOptions(queryOptions));

	public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode)
		=> Folder.GetThumbnailAsync(mode);
	public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize)
		=> Folder.GetThumbnailAsync(mode, requestedSize);
	public override IAsyncOperation<StorageItemThumbnail> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize, ThumbnailOptions options)
		=> Folder.GetThumbnailAsync(mode, requestedSize, options);

	private sealed partial class SystemFolderBasicProperties : BaseBasicProperties
	{
		private readonly IStorageItemExtraProperties basicProps;
		private readonly DateTimeOffset dateCreated;
		private ulong? _cachedSize;
		private DateTimeOffset? _cachedDateModified;

		public override ulong Size => _cachedSize ??= (basicProps as BasicProperties)?.Size ?? 0;

		public override DateTimeOffset DateCreated => dateCreated;
		
		public override DateTimeOffset DateModified => 
			_cachedDateModified ??= (basicProps as BasicProperties)?.DateModified ?? DateTimeOffset.Now;

		public SystemFolderBasicProperties(IStorageItemExtraProperties basicProps, DateTimeOffset dateCreated)
		{
			this.basicProps = basicProps;
			this.dateCreated = dateCreated;
		}

		public override IAsyncOperation<IDictionary<string, object>> RetrievePropertiesAsync(IEnumerable<string> propertiesToRetrieve)
			=> basicProps.RetrievePropertiesAsync(propertiesToRetrieve);

		public override IAsyncAction SavePropertiesAsync()
			=> basicProps.SavePropertiesAsync();
		public override IAsyncAction SavePropertiesAsync([HasVariant] IEnumerable<KeyValuePair<string, object>> propertiesToSave)
			=> basicProps.SavePropertiesAsync(propertiesToSave);
	}
}
