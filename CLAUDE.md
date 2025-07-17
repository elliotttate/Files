# Claude Code Memory - Files App

This file contains important information for Claude Code to remember when working on the Files app.

## Build Commands

### Quick Build
```powershell
cd "C:\Users\ellio\OneDrive\Documents\GitHub\Files"
msbuild Files.slnx -t:Restore -p:Platform=x64 -p:Configuration=Debug
msbuild "src\Files.App (Package)\Files.Package.wapproj" -t:Build -p:Configuration=Debug -p:Platform=x64 -p:AppxBundle=Never
```

### Launch Application (What Actually Works)
```powershell
powershell -ExecutionPolicy Bypass -File "launch-files-app.ps1"
```

**Note**: The build from July 16, 2025 18:13:50 already includes the reentrancy fixes. When running the launch script:
- It finds the FilesDev package
- Launches it successfully
- Monitors the debug log at `%LOCALAPPDATA%\Packages\FilesDev_ykqwq8d6ps0ag\LocalState\debug.log`
- Shows our logging including scroll velocity warnings

## Recent Issues Fixed

### XAML Reentrancy Crashes (July 16, 2025) ✅ FIXED
- **Error**: System crash with exception code 0xc000027b in Microsoft.UI.Xaml.dll
- **Cause**: Rapid UI updates during Everything search with fast scrolling (up to 314 events/sec)
- **Solution**: 
  - Added 100ms debouncing to `ApplyFilesAndFoldersChangesAsync`
  - Protected against concurrent executions with SemaphoreSlim
  - Batched search updates to every 200ms or 100 items
- **Files Modified**: `src\Files.App\ViewModels\ShellViewModel.cs`
- **Result**: Successfully tested with scroll velocities up to 588 events/sec without crashes!

### COM Exception 0x80070490 "Element not found"
- **Error**: COMException when accessing shell items via PIDL
- **Solution**: Added specific error handling in:
  - `ShellFolderExtensions.cs`
  - `LibraryManager.cs`

## Important Paths

- **Debug Log**: `%LOCALAPPDATA%\Packages\FilesDev_ykqwq8d6ps0ag\LocalState\debug.log`
- **Built App**: `src\Files.App (Package)\bin\x64\Debug\AppX\Files.App\Files.exe`
- **Package Name**: `FilesDev_3.9.11.0_x64__ykqwq8d6ps0ag`

## Testing Instructions

1. Build the app using the quick build commands
2. Launch using the PowerShell script
3. Search for "uevr" using Everything search
4. Scroll rapidly to test for crashes
5. Check debug.log for warnings about high scroll velocity

## Key Services to Remember

- **ViewportThumbnailLoaderService**: Manages thumbnail loading for visible items
- **SafeViewportThumbnailLoaderService**: Thread-safe implementation with background processing
- **EverythingSearchService**: Integration with Everything search engine

## Common Build Issues

1. **MSBuild not found**: Use Visual Studio Developer Command Prompt
2. **ApplicationData.get_Current() crashes**: App must run as packaged Windows app
3. **dotnet build fails**: Use MSBuild instead, as the project contains WinRT components