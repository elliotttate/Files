// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Foundation.Metadata;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Files.App.Utils;

namespace Files.App.Helpers
{
	internal static class BitmapHelper
	{
		// Track consecutive failures for throttling error logs
		private static int _consecutiveFailures = 0;
		private static readonly int _maxConsecutiveFailures = 10;

		public static async Task<BitmapImage?> ToBitmapAsync(this byte[]? data, int decodeSize = -1)
		{
			// Robust data validation (Fix 2)
			if (data is null)
			{
				App.Logger?.LogDebug("ToBitmapAsync: data is null");
				return await GetFallbackIconAsync();
			}

			if (data.Length == 0)
			{
				App.Logger?.LogWarning("ToBitmapAsync: data is empty (zero bytes)");
				return await GetFallbackIconAsync();
			}

			if (data.Length > 50 * 1024 * 1024) // 50MB limit to prevent OOM
			{
				App.Logger?.LogWarning("ToBitmapAsync: data is too large ({Length} bytes), rejecting to prevent OOM", data.Length);
				return await GetFallbackIconAsync();
			}

			App.Logger?.LogInformation("ToBitmapAsync: Converting {ByteCount} bytes to BitmapImage, decodeSize={DecodeSize}", data.Length, decodeSize);

			try
			{
				// Use retry helper for bitmap creation
				return await ThumbnailRetryHelper.ExecuteWithRetryAsync(
					async () => await CreateBitmapImageAsync(data, decodeSize),
					"ToBitmapAsync",
					$"DataLength:{data.Length}",
					CancellationToken.None);
			}
			catch (Exception ex)
			{
				App.Logger?.LogError(ex, "ToBitmapAsync: Failed to create bitmap after retries, data length: {Length}", data.Length);
				return await GetFallbackIconAsync();
			}
		}

		/// <summary>
		/// Creates a BitmapImage from byte data with proper UI thread handling
		/// </summary>
		private static async Task<BitmapImage?> CreateBitmapImageAsync(byte[] data, int decodeSize)
		{
			// Check if we're already on the UI thread
			if (MainWindow.Instance.DispatcherQueue.HasThreadAccess)
			{
				// We're on UI thread, create directly
				using var ms = new MemoryStream(data);
				var image = new BitmapImage();
				if (decodeSize > 0)
				{
					image.DecodePixelWidth = decodeSize;
					image.DecodePixelHeight = decodeSize;
				}
				image.DecodePixelType = DecodePixelType.Logical;
				await image.SetSourceAsync(ms.AsRandomAccessStream());
				
				try
				{
					App.Logger?.LogInformation("ToBitmapAsync: Successfully created BitmapImage {Width}x{Height}", image.PixelWidth, image.PixelHeight);
				}
				catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x8001010E))
				{
					// RPC_E_WRONG_THREAD - Just log without dimensions
					App.Logger?.LogInformation("ToBitmapAsync: Successfully created BitmapImage");
				}
				
				// Reset failure counter on success
				_consecutiveFailures = 0;
				return image;
			}
			else
			{
				// We're on a background thread, dispatch to UI thread
				var tcs = new TaskCompletionSource<BitmapImage?>();
				
				// UI-thread dispatch safety (Fix 3)
				bool dispatched = MainWindow.Instance.DispatcherQueue.TryEnqueue(async () =>
				{
					try
					{
						using var ms = new MemoryStream(data);
						var image = new BitmapImage();
						if (decodeSize > 0)
						{
							image.DecodePixelWidth = decodeSize;
							image.DecodePixelHeight = decodeSize;
						}
						image.DecodePixelType = DecodePixelType.Logical;
						await image.SetSourceAsync(ms.AsRandomAccessStream());
						
						try
						{
							App.Logger?.LogInformation("ToBitmapAsync: Successfully created BitmapImage {Width}x{Height}", image.PixelWidth, image.PixelHeight);
						}
						catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x8001010E))
						{
							// RPC_E_WRONG_THREAD - Just log without dimensions
							App.Logger?.LogInformation("ToBitmapAsync: Successfully created BitmapImage");
						}
						
						// Reset failure counter on success
						_consecutiveFailures = 0;
						tcs.SetResult(image);
					}
					catch (Exception ex)
					{
						tcs.SetException(ex);
					}
				});

				if (!dispatched)
				{
					throw new InvalidOperationException("Failed to dispatch to UI thread - queue rejected");
				}

				return await tcs.Task;
			}
		}

		private static async Task<BitmapImage?> HandleBitmapCreationFailure(Exception ex, int dataLength)
		{
			_consecutiveFailures++;
			
			// Only log detailed errors for the first few failures to avoid spam
			if (_consecutiveFailures <= _maxConsecutiveFailures)
			{
				App.Logger?.LogError(ex, "ToBitmapAsync: Failed to create BitmapImage, data length: {Length}", dataLength);
			}
			else if (_consecutiveFailures == _maxConsecutiveFailures + 1)
			{
				App.Logger?.LogWarning("ToBitmapAsync: Suppressing further bitmap creation error logs after {Count} consecutive failures", _maxConsecutiveFailures);
			}

			return await GetFallbackIconAsync();
		}

		private static async Task<BitmapImage?> GetFallbackIconAsync()
		{
			try
			{
				// Provide a simple fallback icon - create a basic empty BitmapImage
				var fallback = new BitmapImage();
				return fallback;
			}
			catch (Exception ex)
			{
				App.Logger?.LogError(ex, "ToBitmapAsync: Failed to create fallback icon");
				return null;
			}
		}

		/// <summary>
		/// Rotates the image at the specified file path.
		/// </summary>
		/// <param name="filePath">The file path to the image.</param>
		/// <param name="rotation">The rotation direction.</param>
		/// <remarks>
		/// https://learn.microsoft.com/uwp/api/windows.graphics.imaging.bitmapdecoder?view=winrt-22000
		/// https://learn.microsoft.com/uwp/api/windows.graphics.imaging.bitmapencoder?view=winrt-22000
		/// </remarks>
		public static async Task RotateAsync(string filePath, BitmapRotation rotation)
		{
			try
			{
				if (string.IsNullOrEmpty(filePath))
				{
					return;
				}

				var file = await StorageHelpers.ToStorageItem<IStorageFile>(filePath);
				if (file is null)
				{
					return;
				}

				var fileStreamRes = await FilesystemTasks.Wrap(() => file.OpenAsync(FileAccessMode.ReadWrite).AsTask());
				using IRandomAccessStream fileStream = fileStreamRes.Result;
				if (fileStream is null)
				{
					return;
				}

				BitmapDecoder decoder = await BitmapDecoder.CreateAsync(fileStream);
				using var memStream = new InMemoryRandomAccessStream();
				BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(memStream, decoder);

				for (int i = 0; i < decoder.FrameCount - 1; i++)
				{
					encoder.BitmapTransform.Rotation = rotation;
					await encoder.GoToNextFrameAsync();
				}

				encoder.BitmapTransform.Rotation = rotation;

				await encoder.FlushAsync();

				memStream.Seek(0);
				fileStream.Seek(0);
				fileStream.Size = 0;

				await RandomAccessStream.CopyAsync(memStream, fileStream);
			}
			catch (Exception ex)
			{
				var errorDialog = new ContentDialog()
				{
					Title = Strings.FailedToRotateImage.GetLocalizedResource(),
					Content = ex.Message,
					PrimaryButtonText = Strings.OK.GetLocalizedResource(),
				};

				if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
					errorDialog.XamlRoot = MainWindow.Instance.Content.XamlRoot;

				await errorDialog.TryShowAsync();
			}
		}

		/// <summary>
		/// This function encodes a software bitmap with the specified encoder and saves it to a file
		/// </summary>
		/// <param name="softwareBitmap"></param>
		/// <param name="outputFile"></param>
		/// <param name="encoderId">The guid of the image encoder type</param>
		/// <returns></returns>
		public static async Task SaveSoftwareBitmapToFileAsync(SoftwareBitmap softwareBitmap, BaseStorageFile outputFile, Guid encoderId)
		{
			using IRandomAccessStream stream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
			// Create an encoder with the desired format
			BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, stream);

			// Set the software bitmap
			encoder.SetSoftwareBitmap(softwareBitmap);

			try
			{
				await encoder.FlushAsync();
			}
			catch (Exception err)
			{
				const int WINCODEC_ERR_UNSUPPORTEDOPERATION = unchecked((int)0x88982F81);
				switch (err.HResult)
				{
					case WINCODEC_ERR_UNSUPPORTEDOPERATION:
						// If the encoder does not support writing a thumbnail, then try again
						// but disable thumbnail generation.
						encoder.IsThumbnailGenerated = false;
						break;

					default:
						throw;
				}
			}

			if (encoder.IsThumbnailGenerated == false)
			{
				await encoder.FlushAsync();
			}
		}
	}
}