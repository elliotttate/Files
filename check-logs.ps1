# Check for Files app logs

Write-Host "Checking for log files..."

# Check app directory
$appDir = "C:\Users\ellio\OneDrive\Documents\GitHub\Files"
Write-Host "`nChecking app directory: $appDir"
Get-ChildItem -Path $appDir -Filter "*.log" -File | ForEach-Object {
    Write-Host "Found: $($_.FullName) - Size: $($_.Length) bytes - Modified: $($_.LastWriteTime)"
}
Get-ChildItem -Path $appDir -Filter "*.txt" -File | Where-Object { $_.Name -like "*log*" -or $_.Name -like "*debug*" } | ForEach-Object {
    Write-Host "Found: $($_.FullName) - Size: $($_.Length) bytes - Modified: $($_.LastWriteTime)"
}

# Check LocalAppData
Write-Host "`nChecking LocalAppData packages..."
$packagesPath = "$env:LOCALAPPDATA\Packages"
if (Test-Path $packagesPath) {
    Get-ChildItem -Path $packagesPath -Directory | Where-Object { $_.Name -like "*Files*" } | ForEach-Object {
        Write-Host "Found package: $($_.FullName)"
        $logPath = Join-Path $_.FullName "LocalState\debug.log"
        if (Test-Path $logPath) {
            Write-Host "  - Log file exists: $logPath ($(Get-Item $logPath).Length bytes)"
            Write-Host "  - Last 5 lines:"
            Get-Content $logPath -Tail 5 | ForEach-Object { Write-Host "    $_" }
        }
    }
}

# Check if process is running
Write-Host "`nChecking if Files.exe is running..."
Get-Process | Where-Object { $_.ProcessName -eq "Files" } | ForEach-Object {
    Write-Host "Files.exe is running - PID: $($_.Id), StartTime: $($_.StartTime)"
}