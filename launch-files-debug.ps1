# Launch Files app with debug logging
$appPath = "C:\Users\ellio\OneDrive\Documents\GitHub\Files\src\Files.App (Package)\bin\x64\Debug\AppX\Files.App\Files.exe"

Write-Host "Launching Files app from: $appPath"
Write-Host "Current time: $(Get-Date)"

# Start the process and capture output
try {
    $process = Start-Process -FilePath $appPath -PassThru -WindowStyle Normal
    Write-Host "Files app started with PID: $($process.Id)"
    
    # Wait a bit for the app to start
    Start-Sleep -Seconds 2
    
    Write-Host "App should now be running. Please perform the following:"
    Write-Host "1. Open a folder with many files"
    Write-Host "2. Search for something using Everything (e.g., 'uevr')"
    Write-Host "3. Scroll rapidly up and down to try to trigger the crash"
    Write-Host ""
    Write-Host "Monitoring for log files..."
    
    # Keep the script running
    Write-Host "Press Ctrl+C to stop monitoring"
    while ($true) {
        Start-Sleep -Seconds 1
    }
}
catch {
    Write-Error "Failed to start Files app: $_"
}