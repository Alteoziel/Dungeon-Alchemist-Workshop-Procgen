$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "0.5.5"
$dist = Join-Path $root "dist"
$package = Join-Path $dist "package"
$payload = Join-Path $package "payload"
$bepinexZip = Join-Path $dist "BepInEx_win_x64_5.4.23.5.zip"
$bepinexUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
$releaseZip = Join-Path $dist "WorkshopProcgen-$version.zip"

if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload | Out-Null

dotnet build (Join-Path $root "WorkshopProcgen\WorkshopProcgen.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "The plugin build failed." }

if (-not (Test-Path -LiteralPath $bepinexZip)) {
    Invoke-WebRequest -Uri $bepinexUrl -OutFile $bepinexZip
}
Expand-Archive -LiteralPath $bepinexZip -DestinationPath $payload -Force

$pluginDest = Join-Path $payload "BepInEx\plugins\WorkshopProcgen"
New-Item -ItemType Directory -Force -Path $pluginDest | Out-Null
Copy-Item -LiteralPath (Join-Path $root "WorkshopProcgen\bin\Release\WorkshopProcgen.dll") -Destination $pluginDest
Copy-Item -LiteralPath (Join-Path $root "docs\manual.md") -Destination (Join-Path $pluginDest "manual.md")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.ps1") -Destination $package
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Install Workshop Procgen.bat") -Destination $package

if (Test-Path -LiteralPath $releaseZip) { Remove-Item -LiteralPath $releaseZip -Force }
Compress-Archive -Path (Join-Path $package "*") -DestinationPath $releaseZip
Write-Output $releaseZip
