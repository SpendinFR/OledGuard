$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'Le SDK .NET 8 x64 est requis pour compiler OledGuard.' -ForegroundColor Yellow
    Write-Host 'Installez le SDK .NET 8, puis relancez build.cmd.'
    exit 1
}

$project = Join-Path $PSScriptRoot 'src\OledGuard\OledGuard.csproj'
$output = Join-Path $PSScriptRoot 'dist\OledGuard'

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

Write-Host 'Publication de OledGuard...' -ForegroundColor Cyan

dotnet publish $project `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $output

Write-Host ''
Write-Host 'Compilation terminée :' -ForegroundColor Green
Write-Host (Join-Path $output 'OledGuard.exe')
