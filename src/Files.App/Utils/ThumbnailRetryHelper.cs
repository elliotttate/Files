// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO;
using System.Runtime.InteropServices;

namespace Files.App.Utils
{
	/// <summary>
	/// Provides retry logic with exponential backoff for thumbnail loading operations
	/// </summary>
	public static class ThumbnailRetryHelper
	{
		// Configuration
		public static int MaxRetryAttempts { get; set; } = 5;
		public static int BaseDelayMs { get; set; } = 500;
		public static int MaxDelayMs { get; set; } = 8000;
		public static bool LogRetryAttempts { get; set; } = true;

		/// <summary>
		/// Executes an operation with retry logic and exponential backoff
		/// </summary>
		/// <typeparam name="T">Return type</typeparam>
		/// <param name="operation">The operation to execute</param>
		/// <param name="operationName">Name for logging</param>
		/// <param name="path">Path being processed (for logging)</param>
		/// <param name="cancellationToken">Cancellation token</param>
		/// <returns>Result of the operation</returns>
		public static async Task<T?> ExecuteWithRetryAsync<T>(
			Func<Task<T?>> operation,
			string operationName,
			string path,
			CancellationToken cancellationToken = default)
		{
			int attempt = 0;
			Exception? lastException = null;

			while (attempt < MaxRetryAttempts)
			{
				try
				{
					var result = await operation();
					
					// Reset retry counter on success
					if (attempt > 0 && LogRetryAttempts)
					{
						App.Logger?.LogInformation("{OperationName}: Succeeded on attempt {Attempt} for path: {Path}", 
							operationName, attempt + 1, path);
					}
					
					return result;
				}
				catch (Exception ex)
				{
					lastException = ex;
					
					// Check if this is a transient error worth retrying
					if (!IsTransientError(ex))
					{
						if (LogRetryAttempts && attempt > 0)
						{
							App.Logger?.LogDebug("{OperationName}: Non-transient error on attempt {Attempt} for path: {Path}: {Error}", 
								operationName, attempt + 1, path, ex.Message);
						}
						throw; // Don't retry non-transient errors
					}
					
					attempt++;
					
					// If we've reached max attempts, throw the last exception
					if (attempt >= MaxRetryAttempts)
					{
						if (LogRetryAttempts)
						{
							App.Logger?.LogWarning("{OperationName}: Failed after {MaxAttempts} attempts for path: {Path}: {Error}", 
								operationName, MaxRetryAttempts, path, ex.Message);
						}
						throw;
					}
					
					// Calculate delay with exponential backoff
					var delay = Math.Min(BaseDelayMs * (int)Math.Pow(2, attempt - 1), MaxDelayMs);
					
					if (LogRetryAttempts)
					{
						App.Logger?.LogDebug("{OperationName}: Attempt {Attempt} failed for path: {Path}, retrying in {Delay}ms: {Error}", 
							operationName, attempt, path, delay, ex.Message);
					}
					
					// Wait before retrying
					try
					{
						await Task.Delay(delay, cancellationToken);
					}
					catch (OperationCanceledException)
					{
						// If cancelled during delay, throw the last exception
						throw lastException ?? ex;
					}
				}
			}

			// This should never be reached, but just in case
			throw lastException ?? new InvalidOperationException($"{operationName} failed after {MaxRetryAttempts} attempts");
		}

		/// <summary>
		/// Synchronous version of ExecuteWithRetryAsync
		/// </summary>
		public static T? ExecuteWithRetry<T>(
			Func<T?> operation,
			string operationName,
			string path)
		{
			return ExecuteWithRetryAsync(
				() => Task.FromResult(operation()),
				operationName,
				path).GetAwaiter().GetResult();
		}

		/// <summary>
		/// Determines if an exception represents a transient error worth retrying
		/// </summary>
		private static bool IsTransientError(Exception ex)
		{
			return ex switch
			{
				// COM errors that might be transient
				COMException comEx => comEx.HResult switch
				{
					unchecked((int)0x80004005) => true,  // E_FAIL - generic failure, might be transient
					unchecked((int)0x80070005) => true,  // E_ACCESSDENIED - might be temporary
					unchecked((int)0x80070020) => true,  // ERROR_SHARING_VIOLATION - file in use
					unchecked((int)0x80070021) => true,  // ERROR_LOCK_VIOLATION - file locked
					unchecked((int)0x80070057) => false, // E_INVALIDARG - not transient
					unchecked((int)0x80070002) => false, // ERROR_FILE_NOT_FOUND - not transient
					unchecked((int)0x80070003) => false, // ERROR_PATH_NOT_FOUND - not transient
					unchecked((int)0x80070490) => false, // ERROR_NOT_FOUND - not transient
					_ => false
				},
				
				// These specific IO exceptions are not transient (must come before base IOException)
				FileNotFoundException => false,
				DirectoryNotFoundException => false,
				
				// IO exceptions
				IOException ioEx => ioEx.HResult switch
				{
					unchecked((int)0x80070020) => true,  // ERROR_SHARING_VIOLATION
					unchecked((int)0x80070021) => true,  // ERROR_LOCK_VIOLATION
					_ => false
				},
				
				// These are typically not transient
				UnauthorizedAccessException => false,
				ArgumentNullException => false,  // Must come before base ArgumentException
				ArgumentException => false,
				
				// Network-related errors that might be transient
				System.Net.NetworkInformation.NetworkInformationException => true,
				
				// Task cancellation is not transient (TaskCanceledException must come before base OperationCanceledException)
				TaskCanceledException => false,
				OperationCanceledException => false,
				
				// Generic exceptions might be transient
				_ => true
			};
		}

		/// <summary>
		/// Gets the current retry configuration as a string for logging
		/// </summary>
		public static string GetRetryConfiguration()
		{
			return $"MaxRetryAttempts: {MaxRetryAttempts}, BaseDelayMs: {BaseDelayMs}, MaxDelayMs: {MaxDelayMs}";
		}
	}
} 