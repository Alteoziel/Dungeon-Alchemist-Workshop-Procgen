param(
    [string]$GamePath,
    [switch]$Elevated,
    [switch]$Unattended
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms

function Show-Info([string]$Text) {
    if ($Unattended) {
        Write-Output $Text
        return
    }
    [System.Windows.Forms.MessageBox]::Show($Text, "Workshop Procgen", "OK", "Information") | Out-Null
}

function Show-Problem([string]$Text) {
    if ($Unattended) {
        Write-Error $Text
        return
    }
    [System.Windows.Forms.MessageBox]::Show($Text, "Workshop Procgen", "OK", "Error") | Out-Null
}

function Ask-Yes([string]$Text) {
    if ($Unattended) { return $true }
    return [System.Windows.Forms.MessageBox]::Show($Text, "Workshop Procgen", "YesNo", "Question") -eq "Yes"
}

function Get-LibraryPaths {
    $paths = New-Object System.Collections.Generic.List[string]
    $steam = $null
    foreach ($key in @(
            "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
            "HKLM:\SOFTWARE\Valve\Steam",
            "HKCU:\Software\Valve\Steam")) {
        try {
            $install = (Get-ItemProperty -Path $key -ErrorAction Stop).InstallPath
        }
        catch {
            continue
        }
        if ($install -and (Test-Path -LiteralPath $install)) {
            $steam = $install
            break
        }
    }
    if (-not $steam) { return @() }
    $paths.Add($steam) | Out-Null
    $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
    if (Test-Path -LiteralPath $vdf) {
        $text = Get-Content -LiteralPath $vdf -Raw
        foreach ($match in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
            $folder = $match.Groups[1].Value.Replace("\\", "\")
            if ($folder -and (Test-Path -LiteralPath $folder)) {
                $paths.Add($folder) | Out-Null
            }
        }
    }
    return @($paths | Select-Object -Unique)
}

function Find-GameFolders {
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($library in Get-LibraryPaths) {
        $dir = Join-Path $library "steamapps\common\Dungeon Alchemist"
        $exe = Join-Path $dir "Dungeon Alchemist.exe"
        if (Test-Path -LiteralPath $exe) {
            $found.Add($dir) | Out-Null
        }
    }
    return , @($found | Select-Object -Unique)
}

function Select-GameFolder {
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Select the folder that contains Dungeon Alchemist.exe"
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne "OK") { return $null }
    $exe = Join-Path $dialog.SelectedPath "Dungeon Alchemist.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        Show-Problem "That folder does not contain Dungeon Alchemist.exe. In Steam, open the game page, choose the gear icon, Manage, then Browse local files."
        return $null
    }
    return $dialog.SelectedPath
}

function Copy-Mod([string]$Game) {
    $payload = Join-Path $PSScriptRoot "payload"
    $pluginSrc = Join-Path $payload "BepInEx\plugins\WorkshopProcgen"
    if (-not (Test-Path -LiteralPath (Join-Path $pluginSrc "WorkshopProcgen.dll"))) {
        throw "The installer folder is incomplete. Extract the whole zip, then run Install Workshop Procgen from the extracted folder."
    }

    $core = Join-Path $Game "BepInEx\core\BepInEx.dll"
    $loader = Join-Path $Game "winhttp.dll"
    if (-not (Test-Path -LiteralPath $core) -or -not (Test-Path -LiteralPath $loader)) {
        Get-ChildItem -LiteralPath $payload -Force | ForEach-Object {
            $dest = Join-Path $Game $_.Name
            if ($_.PSIsContainer) {
                Copy-Item -LiteralPath $_.FullName -Destination $dest -Recurse -Force
            }
            else {
                Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
            }
        }
    }

    $pluginDest = Join-Path $Game "BepInEx\plugins\WorkshopProcgen"
    New-Item -ItemType Directory -Force -Path $pluginDest | Out-Null
    Copy-Item -LiteralPath (Join-Path $pluginSrc "*") -Destination $pluginDest -Force
}

function Install-Mod([string]$Game) {
    if (Get-Process -Name "Dungeon Alchemist" -ErrorAction SilentlyContinue) {
        Show-Problem "Close Dungeon Alchemist, then run the installer again."
        exit 1
    }

    try {
        Copy-Mod $Game
    }
    catch {
        $denied = $_.Exception.Message -match "denied|unauthorized|access"
        if (-not $Elevated -and -not $Unattended -and $denied) {
            Show-Info "Windows will ask for permission to copy files into the game folder. Choose Yes."
            $arg = "-NoProfile -ExecutionPolicy Bypass -STA -File `"$PSCommandPath`" -GamePath `"$Game`" -Elevated"
            $proc = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $arg -PassThru -Wait
            if ($null -eq $proc) { exit 1 }
            exit $proc.ExitCode
        }
        Show-Problem "The mod was not copied. Close Dungeon Alchemist and run the installer again. If it still fails, choose the game folder from Steam with Manage, then Browse local files.`r`n`r`n$($_.Exception.Message)"
        exit 1
    }

    Show-Info "Workshop Procgen is installed.`r`n`r`nStart Dungeon Alchemist, open a map, and press F6. The Manual button in that window explains the rest."
    exit 0
}

$payloadRoot = Join-Path $PSScriptRoot "payload"
if (-not (Test-Path -LiteralPath $payloadRoot)) {
    Show-Problem "Extract the zip first. In Downloads, right-click the zip, choose Extract All, open the new folder, and double-click Install Workshop Procgen."
    exit 1
}

if ($GamePath) {
    $exe = Join-Path $GamePath "Dungeon Alchemist.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        Show-Problem "The game was not found in $GamePath"
        exit 1
    }
    Install-Mod $GamePath
}

$games = @(Find-GameFolders)
$chosen = $null
if ($games.Count -eq 1) {
    $chosen = $games[0]
}
elseif ($games.Count -gt 1) {
    $list = $games -join "`r`n"
    if (Ask-Yes "More than one Dungeon Alchemist folder was found. Install into the first one?`r`n`r`n$list") {
        $chosen = $games[0]
    }
}

if (-not $chosen) {
    if ($Unattended) {
        Show-Problem "Dungeon Alchemist was not found."
        exit 1
    }
    Show-Info "The installer could not choose the game folder on its own. Select the folder that contains Dungeon Alchemist.exe."
    $chosen = Select-GameFolder
    if (-not $chosen) { exit 1 }
}

$question = "Install Workshop Procgen into this folder?`r`n`r`n$chosen`r`n`r`nYour maps are left as they are. Close the game first."
if (-not (Ask-Yes $question)) { exit 1 }
Install-Mod $chosen
