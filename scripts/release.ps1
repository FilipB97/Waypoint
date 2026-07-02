<#
.SYNOPSIS
  Buduje self-contained Waypoint.exe i (opcjonalnie) publikuje release na GitHub.

.DESCRIPTION
  - Domyslnie: testy + publish -> dist\Waypoint-<wersja>-win-x64.exe (jeden plik, bez instalacji .NET).
  - Z -Publish: dodatkowo tworzy tag v<wersja> i wypycha go na origin, co uruchamia workflow
    GitHub Actions "Release" (on: push tags v*), ktory buduje i publikuje release z zalacznikiem.
    Dzieki temu publikacja nie wymaga narzedzia 'gh'.

.EXAMPLE
  .\scripts\release.ps1                     # build z wersja z .csproj
.EXAMPLE
  .\scripts\release.ps1 -Version 1.1.0      # build z podana wersja
.EXAMPLE
  .\scripts\release.ps1 -Version 1.1.0 -Publish   # build + tag v1.1.0 + push (release na GitHub)
#>
[CmdletBinding()]
param(
  [string]$Version,
  [switch]$Publish,
  [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

# Katalog repo = rodzic folderu 'scripts'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$Csproj = 'src\RdpManager\RdpManager.csproj'

# dotnet: lokalny SDK .NET 8 jesli jest (systemowy bywa za stary), inaczej z PATH
$Dotnet = if (Test-Path 'C:\dotnet8\dotnet.exe') { 'C:\dotnet8\dotnet.exe' } else { 'dotnet' }

# Wersja: z parametru albo z <Version> w .csproj
if (-not $Version) {
  $m = Select-String -Path $Csproj -Pattern '<Version>(.*?)</Version>' | Select-Object -First 1
  $Version = if ($m) { $m.Matches[0].Groups[1].Value } else { '1.0.0' }
}
Write-Host ">> Waypoint release - wersja $Version" -ForegroundColor Cyan

# Ubij dzialajaca instancje (blokuje plik .exe podczas kopiowania)
Get-Process Waypoint, RdpManager -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $SkipTests) {
  Write-Host ">> Testy..." -ForegroundColor Cyan
  & $Dotnet test RdpManager.sln -c Release --nologo
  if ($LASTEXITCODE -ne 0) { throw "Testy nie przeszly - przerwano." }
}

Write-Host ">> Publish (self-contained single-file win-x64)..." -ForegroundColor Cyan
& $Dotnet publish $Csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
  -p:Version=$Version -o publish
if ($LASTEXITCODE -ne 0) { throw "Publish nie powiodl sie." }

New-Item -ItemType Directory -Force dist | Out-Null
$Out = "dist\Waypoint-$Version-win-x64.exe"
Copy-Item 'publish\Waypoint.exe' $Out -Force
$SizeMB = [math]::Round((Get-Item $Out).Length / 1MB, 1)
Write-Host ">> Gotowe: $Out ($SizeMB MB)" -ForegroundColor Green

if ($Publish) {
  Write-Host ">> Publikacja: tag v$Version + push (uruchomi GitHub Actions 'Release')..." -ForegroundColor Cyan
  if (& git tag --list "v$Version") { throw "Tag v$Version juz istnieje - podnies wersje." }
  & git tag "v$Version"
  # schannel = natywny TLS Windows (dziala tez za firmowym proxy z inspekcja SSL)
  & git -c http.sslBackend=schannel push origin "v$Version"
  if ($LASTEXITCODE -ne 0) { & git tag -d "v$Version" | Out-Null; throw "Push taga nie powiodl sie (tag lokalny cofniety)." }
  Write-Host ">> Tag wypchniety. GitHub zbuduje i opublikuje release:" -ForegroundColor Green
  Write-Host "   https://github.com/FilipB97/Waypoint/actions" -ForegroundColor Green
}
