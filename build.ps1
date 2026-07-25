$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet publish .\OledGuardSimple.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true

Write-Host ""
Write-Host "EXE créé dans :"
Write-Host "$PSScriptRootin\Release
et8.0-windows\win-x64\publish\OledGuardSimple.exe"
