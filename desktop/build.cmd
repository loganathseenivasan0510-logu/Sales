@echo off
rem Baut SalesofLoga.exe lokal (benötigt .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0)
setlocal
cd /d "%~dp0"
dotnet publish SalesofLoga.Desktop\SalesofLoga.Desktop.csproj -c Release -r win-x64 -o ..\publish || exit /b 1
echo.
echo Fertig: %~dp0..\publish\SalesofLoga.exe
echo Sicherheitstests: powershell -ExecutionPolicy Bypass -File ..\tests\security\Invoke-SecurityTests.ps1 -Open
endlocal
