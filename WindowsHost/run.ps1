Stop-Process -Name "TabMirror.Host" -ErrorAction SilentlyContinue 
Write-Host "Starting Tab Mirror Host..." -ForegroundColor Cyan
dotnet run
