// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Storage.FileProperties;
using Files.App.Utils;
using Files.App.Utils.Diagnostics;

namespace Files.App.Utils.Storage
{
	public static class FileThumbnailHelper
	{
		/// <summary>
		/// Returns icon or thumbnail for given file or folder
		/// </summary>
		public static async Task<byte[]?> GetIconAsync(string path, uint requestedSize, bool isFolder, IconOptions iconOptions)
		{
			var pathHash = path?.GetHashCode() ?? 0;
			
			try
			{
				// Validate inputs
				if (string.IsNullOrWhiteSpace(path))
				{
					App.Logger?.LogWarning("GetIconAsync: Path is null or empty");
					return null;
				}

				if (requestedSize == 0)
				{
					App.Logger?.LogWarning("GetIconAsync: Requested size is 0 for path: {Path}", path);
					return null;
				}

				// Clamp thumbnail size to prevent OOM (Fix 7)
				var clampedSize = Math.Min(requestedSize, 512u);
				if (clampedSize != requestedSize)
				{
					App.Logger?.LogInformation("GetIconAsync: Clamped size from {Original} to {Clamped} for path: {Path}", requestedSize, clampedSize, path);
				}
				System.Diagnostics.Debug.WriteLine($"[THUMB-{pathHash:X8}] GetIconAsync START: path={path}, size={clampedSize}, isFolder={isFolder}");
				
				// DPI sanity check (Fix 4)
				var dpi = Math.Max(1f, App.AppModel.AppWindowDPI);
				var size = iconOptions.HasFlag(IconOptions.UseCurrentScale) ? clampedSize * dpi : clampedSize;

				// Shell/COM exception fallback with retry logic (Fix 8 + 10)
				byte[]? result = null;
				try
				{
					// Use retry helper for icon loading
					System.Diagnostics.Debug.WriteLine($"[THUMB-{pathHash:X8}] Starting retry helper");
					
					using (DeadlockDetector.TrackOperation($"GetIcon-{path}"))
					{
						result = await ThumbnailRetryHelper.ExecuteWithRetryAsync(
							async () => await Win32Helper.StartSTATask(() => Win32Helper.GetIcon(path, (int)size, isFolder, iconOptions)),
							"GetIcon",
							path,
							CancellationToken.None);
					}
				}
				catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80004005))
				{
					System.Diagnostics.Debug.WriteLine($"[THUMB-{pathHash:X8}] COM error 0x80004005, using fallback");
					App.Logger?.LogWarning("GetIconAsync: COM error (0x80004005) after retries for path: {Path}, falling back to generic icon", path);
					// Fall back to generic file/folder icon with retry
					try
					{
						result = await ThumbnailRetryHelper.ExecuteWithRetryAsync(
							async () => await Win32Helper.StartSTATask(() => Win32Helper.GetIcon(
								isFolder ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : "C:\\Windows\\System32\\shell32.dll",
								(int)size, isFolder, iconOptions | IconOptions.ReturnIconOnly)),
							"GetIcon-Fallback",
							path,
							CancellationToken.None);
					}
					catch (Exception fallbackEx)
					{
						App.Logger?.LogError(fallbackEx, "GetIconAsync: Fallback icon failed after retries for path: {Path}", path);
						return null;
					}
				}
				catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070490))
				{
					App.Logger?.LogWarning("GetIconAsync: COM error (Element not found) after retries for path: {Path}", path);
					return null;
				}
				catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070002))
				{
					App.Logger?.LogWarning("GetIconAsync: COM error (File not found) after retries for path: {Path}", path);
					return null;
				}
				catch (System.Runtime.InteropServices.COMException ex)
				{
					App.Logger?.LogError(ex, "GetIconAsync: COM error (0x{HResult:X8}) after retries for path: {Path}", ex.HResult, path);
					return null;
				}
				catch (System.UnauthorizedAccessException ex)
				{
					App.Logger?.LogWarning(ex, "GetIconAsync: Access denied after retries for path: {Path}", path);
					return null;
				}
				catch (System.IO.FileNotFoundException ex)
				{
					App.Logger?.LogWarning(ex, "GetIconAsync: File not found after retries for path: {Path}", path);
					return null;
				}
				catch (System.IO.DirectoryNotFoundException ex)
				{
					App.Logger?.LogWarning(ex, "GetIconAsync: Directory not found after retries for path: {Path}", path);
					return null;
				}
				
				// Robust icon-data validation (Fix 1)
				if (result == null || result.Length == 0)
				{
					System.Diagnostics.Debug.WriteLine($"[THUMB-{pathHash:X8}] GetIconAsync FAILED: No icon data returned");
					App.Logger?.LogDebug("GetIconAsync: No icon data returned for path: {Path}", path);
					return null;
				}

				System.Diagnostics.Debug.WriteLine($"[THUMB-{pathHash:X8}] GetIconAsync SUCCESS: {result.Length} bytes");
				return result;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[THUMB-{pathHash:X8}] GetIconAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
				App.Logger?.LogError(ex, "GetIconAsync failed for path: {Path}", path);
				return null;
			}
		}

		/// <summary>
		/// Returns overlay for given file or folder
		/// /// </summary>
		/// <param name="path"></param>
		/// <param name="isFolder"></param>
		/// <returns></returns>
		public static async Task<byte[]?> GetIconOverlayAsync(string path, bool isFolder)
			=> await Win32Helper.StartSTATask(() => Win32Helper.GetIconOverlay(path, isFolder));

		[Obsolete]
		public static async Task<byte[]?> LoadIconFromPathAsync(string filePath, uint thumbnailSize, ThumbnailMode thumbnailMode, ThumbnailOptions thumbnailOptions, bool isFolder = false)
		{
			var result = await GetIconAsync(filePath, thumbnailSize, isFolder, IconOptions.None);
			return result;
		}
	}
}