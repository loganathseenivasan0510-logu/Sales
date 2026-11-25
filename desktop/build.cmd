@echo off
rem Baut publish\SalesofLoga.exe (benötigt .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" || exit /b 1
echo.
echo Sicherheitstests: powershell -ExecutionPolicy Bypass -File "%~dp0..\tests\security\Invoke-SecurityTests.ps1" -Open
