@echo off
echo [DevPanel] Encerrando instancia anterior...
taskkill /F /FI "IMAGENAME eq DevAutomation.Server.exe" >nul 2>&1
taskkill /F /FI "IMAGENAME eq pwsh.exe" >nul 2>&1
timeout /t 2 /nobreak >nul
echo [DevPanel] Iniciando servidor .NET (ASP.NET Core + SignalR)...
echo [DevPanel] O browser abrira automaticamente em http://localhost:8080
dotnet run --project "%~dp0..\src\DevAutomation.Server\DevAutomation.Server.csproj" --configuration Release
