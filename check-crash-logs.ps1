# Check for recent application crashes in Windows Event Log
Write-Host "Checking for recent application crashes..."

# Get Application Error events from the last 10 minutes
$startTime = (Get-Date).AddMinutes(-10)
$events = Get-WinEvent -FilterHashtable @{LogName='Application'; ID=1000; StartTime=$startTime} -ErrorAction SilentlyContinue

if ($events) {
    Write-Host "`nFound $($events.Count) application crash event(s):"
    foreach ($event in $events) {
        $message = $event.Message
        if ($message -like "*Files.exe*" -or $message -like "*Files.dll*") {
            Write-Host "`n=== CRASH EVENT ==="
            Write-Host "Time: $($event.TimeCreated)"
            Write-Host "Message:"
            Write-Host $message
            Write-Host "==================="
        }
    }
} else {
    Write-Host "No application crashes found in the last 10 minutes."
}

# Also check for .NET Runtime errors
Write-Host "`nChecking for .NET Runtime errors..."
$netEvents = Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime'; StartTime=$startTime} -ErrorAction SilentlyContinue

if ($netEvents) {
    foreach ($event in $netEvents) {
        $message = $event.Message
        if ($message -like "*Files*") {
            Write-Host "`n=== .NET RUNTIME ERROR ==="
            Write-Host "Time: $($event.TimeCreated)"
            Write-Host "Level: $($event.LevelDisplayName)"
            Write-Host "Message:"
            Write-Host $message
            Write-Host "=========================="
        }
    }
}