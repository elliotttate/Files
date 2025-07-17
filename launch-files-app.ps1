# Launch the installed Files app

Write-Host "Looking for installed Files app packages..."

# Check for installed Files packages
$packages = Get-AppxPackage | Where-Object { $_.Name -like "*Files*" }

if ($packages) {
    Write-Host "Found $($packages.Count) Files package(s):"
    foreach ($pkg in $packages) {
        Write-Host "  - $($pkg.Name): $($pkg.PackageFullName)"
    }
    
    # Try to launch FilesDev if it exists
    $filesDev = $packages | Where-Object { $_.Name -eq "FilesDev" } | Select-Object -First 1
    if ($filesDev) {
        Write-Host "`nLaunching FilesDev..."
        $appId = (Get-AppxPackageManifest $filesDev).Package.Applications.Application.Id
        Write-Host "App ID: $appId"
        
        try {
            Start-Process "shell:AppsFolder\$($filesDev.PackageFamilyName)!$appId"
            Write-Host "FilesDev launched successfully!"
            
            # Monitor the log file
            $logPath = "$env:LOCALAPPDATA\Packages\$($filesDev.PackageFamilyName)\LocalState\debug.log"
            if (Test-Path $logPath) {
                Write-Host "`nMonitoring log file at: $logPath"
                Write-Host "Perform fast scrolling to trigger the issue..."
                Write-Host "Press Ctrl+C to stop monitoring"
                
                # Clear the log first
                Clear-Content $logPath -ErrorAction SilentlyContinue
                
                # Monitor the log
                Get-Content $logPath -Wait | ForEach-Object {
                    $line = $_
                    # Highlight important lines
                    if ($line -like "*Warning*" -or $line -like "*Error*") {
                        Write-Host $line -ForegroundColor Yellow
                    } elseif ($line -like "*scroll velocity*" -or $line -like "*ViewportUpdateTimer*") {
                        Write-Host $line -ForegroundColor Cyan
                    } else {
                        Write-Host $line
                    }
                }
            }
        }
        catch {
            Write-Error "Failed to launch FilesDev: $_"
        }
    }
} else {
    Write-Host "No Files packages found installed."
    Write-Host "You may need to deploy/install the app package first."
}