@echo off
taskkill /F /IM TabMirror.Host.exe /T 2>nul
echo Starting Tab Mirror Host...
dotnet run
