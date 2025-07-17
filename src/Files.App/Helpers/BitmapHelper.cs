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

namespace Files.App.Helpers
{
	internal static class BitmapHelper
	{
		public static async Task<BitmapImage?> ToBitmapAsync(this byte[]? data, int decodeSize = -1)
		{
			if (data is null)
			{
				App.Logger?.LogInformation("ToBitmapAsync: data is null");
				return null;
			}

			App.Logger?.LogInformation("ToBitmapAsync: Converting {ByteCount} bytes to BitmapImage, decodeSize={DecodeSize}", data.Length, decodeSize);

			try
			{
				// Check if we're already on the UI thread
				if (MainWindow.Instance.DispatcherQueue.HasThreadAccess)
				{
					// We're on UI thread, create directly
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
						App.Logger?.LogInformation("ToBitmapAsync: Successfully created BitmapImage {Width}x{Height}", image.PixelWidth, image.PixelHeight);
						return image;
					}
					catch (Exception ex)
					{
						App.Logger?.LogError(ex, "ToBitmapAsync: Failed to create BitmapImage on UI thread, data length: {Length}", data.Length);
						return null;
					}
				}
				else
				{
					// We're on a background thread, dispatch to UI thread
					var tcs = new TaskCompletionSource<BitmapImage?>();
					MainWindow.Instance.DispatcherQueue.TryEnqueue(async () =>
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
							App.Logger?.LogInformation("ToBitmapAsync: Successfully created BitmapImage {Width}x{Height}", image.PixelWidth, image.PixelHeight);
							tcs.SetResult(image);
						}
						catch (Exception ex)
						{
							App.Logger?.LogError(ex, "ToBitmapAsync: Failed to create BitmapImage on UI thread, data length: {Length}", data.Length);
							tcs.SetResult(null);
						}
					});
					return await tcs.Task;
				}
			}
			catch (Exception ex)
			{
				App.Logger?.LogError(ex, "ToBitmapAsync: Failed to dispatch to UI thread");
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