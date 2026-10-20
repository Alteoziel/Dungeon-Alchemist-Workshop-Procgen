param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Dungeon Alchemist",
    [string]$Assembly = "Assembly-CSharp.dll"
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $GameDir "Dungeon Alchemist_Data\Managed"
$dll = Join-Path $managed $Assembly
$outDir = Join-Path $root ("decompiled\" + [IO.Path]::GetFileNameWithoutExtension($Assembly))

Push-Location $root
try {
    if (-not (Test-Path ".config\dotnet-tools.json")) {
        dotnet new tool-manifest | Out-Null
    }
    $installed = dotnet tool list --local | Select-String -SimpleMatch "ilspycmd"
    if (-not $installed) {
        # ilspycmd 10.x packages require the .NET 10 SDK
        dotnet tool install --local ilspycmd --version 9.1.0.7988
        if ($LASTEXITCODE -ne 0) { throw "ilspycmd install failed" }
    }
    dotnet tool restore | Out-Null

    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    dotnet tool run ilspycmd -p -o $outDir -r $managed $dll
    if ($LASTEXITCODE -ne 0) { throw "ilspycmd failed with exit code $LASTEXITCODE" }
    Write-Host "Decompiled $dll -> $outDir"
}
finally {
    Pop-Location
}
