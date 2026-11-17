# _resolve_paths.ps1
# Shared path resolution helpers so scripts avoid hardcoded local machine paths.

function Resolve-TabRepoRoot {
    return (Split-Path -Parent $PSScriptRoot)
}

function Resolve-TabGameDir {
    param([string]$ExplicitGameDir = "")

    if (-not [string]::IsNullOrWhiteSpace($ExplicitGameDir)) {
        return $ExplicitGameDir
    }
    if (-not [string]::IsNullOrWhiteSpace($env:TAB_GAME_DIR) -and (Test-Path (Join-Path $env:TAB_GAME_DIR "TheyAreBillions.exe"))) {
        return $env:TAB_GAME_DIR
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    try {
        $steamPath = (Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -Name "SteamPath" -ErrorAction SilentlyContinue).SteamPath
        if (-not [string]::IsNullOrWhiteSpace($steamPath)) {
            $steamPath = $steamPath -replace "/", "\"
            $candidates.Add((Join-Path $steamPath "steamapps\common\They Are Billions"))
            $vdf = Join-Path $steamPath "steamapps\libraryfolders.vdf"
            if (Test-Path $vdf) {
                $matches = Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches
                foreach ($m in $matches.Matches) {
                    $lib = ($m.Groups[1].Value -replace '\\\\', '\')
                    $candidates.Add((Join-Path $lib "steamapps\common\They Are Billions"))
                }
            }
        }
    } catch { }

    foreach ($drv in @("C", "D", "E", "F")) {
        $candidates.Add("${drv}:\Program Files (x86)\Steam\steamapps\common\They Are Billions")
        $candidates.Add("${drv}:\SteamLibrary\steamapps\common\They Are Billions")
    }

    foreach ($c in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($c) -and (Test-Path (Join-Path $c "TheyAreBillions.exe"))) {
            return $c
        }
    }

    throw "Could not locate They Are Billions game directory. Pass -GameDir or set `$env:TAB_GAME_DIR."
}

function Resolve-TabSavePath {
    param([string]$FileName = "1_Backup.zxsav")
    $docs = [Environment]::GetFolderPath('MyDocuments')
    return (Join-Path $docs "My Games\They Are Billions\Saves\$FileName")
}
