# Files App Build Instructions

This document contains build instructions for the Files app, including commands that have been tested and work.

## UPDATE: What Actually Worked (July 16, 2025)

After implementing the reentrancy fixes, the existing debug build from earlier today already included our changes. No rebuild was necessary.

**To run and test:**
```powershell
powershell -ExecutionPolicy Bypass -File "C:\Users\ellio\OneDrive\Documents\GitHub\Files\launch-files-app.ps1"
```

This launches the FilesDev package and monitors the debug log. The app successfully handled scroll velocities up to 588 events/sec without crashing!

## Prerequisites

1. **MSBuild** (comes with Visual Studio 2022 or Build Tools for Visual Studio)
2. **.NET 9.0 SDK**
3. **Windows SDK** (10.0.26100.67 or compatible)
4. **Developer Mode** enabled in Windows Settings

## Build Commands

### Quick Build (x64 Debug)

```powershell
# Navigate to project root
cd "C:\Users\ellio\OneDrive\Documents\GitHub\Files"

# Restore NuGet packages
msbuild Files.slnx -t:Restore -p:Platform=x64 -p:Configuration=Debug

# Build the application
msbuild "src\Files.App (Package)\Files.Package.wapproj" -t:Build -p:Configuration=Debug -p:Platform=x64 -p:AppxBundle=Never
```

### Build with Package (for deployment)

```powershell
# Build and create APPX package
msbuild "src\Files.App (Package)\Files.Package.wapproj" `
  -t:Build `
  -t:_GenerateAppxPackage `
  -p:Configuration=Debug `
  -p:Platform=x64 `
  -p:AppxBundle=Always `
  -p:UapAppxPackageBuildMode=SideloadOnly `
  -p:AppxPackageDir=".\AppxPackages"
```

### Alternative Build Methods

If MSBuild is not in PATH, you can use:

1. **Visual Studio Developer Command Prompt**
   - Open "Developer Command Prompt for VS 2022" from Start Menu
   - Navigate to project directory and run MSBuild commands

2. **Direct MSBuild Path** (example):
   ```powershell
   & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" Files.slnx -t:Restore -p:Platform=x64 -p:Configuration=Debug
   ```

## Running the Built Application

### Option 1: Direct Execution (May crash due to package context)
```powershell
# This often fails with ApplicationData.get_Current() errors
& "C:\Users\ellio\OneDrive\Documents\GitHub\Files\src\Files.App (Package)\bin\x64\Debug\AppX\Files.App\Files.exe"
```

### Option 2: Launch Installed Package (Recommended)
```powershell
# Use the launch script
powershell -ExecutionPolicy Bypass -File "C:\Users\ellio\OneDrive\Documents\GitHub\Files\launch-files-app.ps1"
```

### Option 3: Deploy and Launch
```powershell
# Deploy the package first
Add-AppxPackage -Path ".\AppxPackages\Files.Package_3.9.11.0_x64_Debug_Test\Files.Package_3.9.11.0_x64_Debug.appx" -DeveloperMode

# Then launch using the app's protocol
Start-Process "files-dev:"
```

## Troubleshooting

### MSBuild Not Found
- Install Visual Studio 2022 or Build Tools for Visual Studio
- Or use Visual Studio Developer Command Prompt

### Package Context Errors (0xe0434352)
- The app must be run as a packaged Windows app
- Use the launch scripts or deploy the APPX package

### Build Errors with dotnet CLI
- The project requires MSBuild, not dotnet CLI
- Some projects in the solution are WinRT components that need MSBuild

## Important Files and Paths

- **Solution**: `Files.slnx` (new solution format)
- **Package Project**: `src\Files.App (Package)\Files.Package.wapproj`
- **Built EXE**: `src\Files.App (Package)\bin\x64\Debug\AppX\Files.App\Files.exe`
- **Log File**: `%LOCALAPPDATA%\Packages\FilesDev_ykqwq8d6ps0ag\LocalState\debug.log`

## Recent Changes (July 16, 2025)

Added fixes for XAML reentrancy crashes:
1. Debounced `ApplyFilesAndFoldersChangesAsync` with 100ms timer
2. Added semaphore to prevent concurrent UI updates
3. Batched search updates to occur every 200ms or 100 items

These changes are in `src\Files.App\ViewModels\ShellViewModel.cs`.