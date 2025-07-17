# Quick rebuild script for Files app
Write-Host "Quick rebuild of Files app..." -ForegroundColor Cyan

$projectPath = "C:\Users\ellio\OneDrive\Documents\GitHub\Files\src\Files.App\Files.App.csproj"

Write-Host "Building Files.App project..." -ForegroundColor Yellow
dotnet build $projectPath -c Debug --no-restore

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build completed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "To test the changes, run:" -ForegroundColor Cyan
    Write-Host "powershell -ExecutionPolicy Bypass -File launch-files-app.ps1" -ForegroundColor White
} else {
    Write-Host "Build failed!" -ForegroundColor Red
}