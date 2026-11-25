<#
.SYNOPSIS
  Baut SalesofLoga.exe als EINE Datei (win-x64, self-contained, Web-Assets eingebettet).
.DESCRIPTION
  1. Publish ohne Single-File → liefert wwwroot\ + Endpoint-Manifest (inkl. blazor.web.js)
  2. diese Dateien als webassets.zip packen
  3. Publish als Single-File mit eingebettetem Zip
  Beim Start entpackt die EXE die Assets einmalig nach %LOCALAPPDATA%\SalesofLoga\app\<version>\.
.EXAMPLE
  .\desktop\publish.ps1            # Ergebnis: .\publish\SalesofLoga.exe
#>
param([string]$Output = (Join-Path $PSScriptRoot '..\publish'), [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'SalesofLoga.Desktop\SalesofLoga.Desktop.csproj'
$stage = Join-Path $PSScriptRoot 'SalesofLoga.Desktop\obj\webstage'
$zipDir = Join-Path $PSScriptRoot 'SalesofLoga.Desktop\obj\webassets'
Remove-Item $stage, $zipDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $zipDir | Out-Null

Write-Host '== 1/3 Publish web assets' -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r win-x64 -p:PublishSingleFile=false -p:SelfContained=false -o $stage
if ($LASTEXITCODE) { throw 'publish (stage) failed' }

Write-Host '== 2/3 Pack webassets.zip' -ForegroundColor Cyan
$pack = Join-Path $zipDir 'pack'
New-Item -ItemType Directory -Force $pack | Out-Null
Copy-Item (Join-Path $stage 'wwwroot') $pack -Recurse
Copy-Item (Join-Path $stage 'SalesofLoga.staticwebassets.endpoints.json') $pack
Compress-Archive -Path (Join-Path $pack '*') -DestinationPath (Join-Path $zipDir 'webassets.zip') -CompressionLevel Optimal
Get-ChildItem $pack -Recurse -File | Select-Object @{n='File';e={$_.FullName.Substring($pack.Length + 1)}}, Length | Format-Table -AutoSize

Write-Host '== 3/3 Publish single-file EXE' -ForegroundColor Cyan
Remove-Item $Output -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish $proj -c $Configuration -r win-x64 -p:EmbedWebAssets=true -o $Output
if ($LASTEXITCODE) { throw 'publish (exe) failed' }
# Nur die EXE ausliefern – Assets stecken eingebettet darin
Get-ChildItem $Output | Where-Object Name -ne 'SalesofLoga.exe' | Remove-Item -Recurse -Force
Get-ChildItem $Output | Format-Table Name, Length -AutoSize
