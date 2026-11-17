param(
    [string]$GameDir = "",
    [string]$OutputDir = "",
    [int]$Resolution = 2048,
    [int]$BootTimeoutSec = 45
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path (Resolve-TabRepoRoot) "reports"
}

function Invoke-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$CmdArgs = @{})
    $payload = @{ id = 1; tool = $Cmd; args = $CmdArgs } | ConvertTo-Json -Compress -Depth 6
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.ReceiveTimeout = 120000
        $client.SendTimeout = 10000
        $client.Connect("127.0.0.1", $Port)
        $stream = $client.GetStream()
        $writer = New-Object System.IO.StreamWriter($stream, [System.Text.Encoding]::UTF8)
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        $writer.WriteLine($payload)
        $writer.Flush()
        $line = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($line)) {
            throw "Empty response from control port $Port for $Cmd"
        }
        $obj = $line | ConvertFrom-Json
        if (-not $obj.ok) {
            throw "Control command '$Cmd' on port $Port failed: $($obj.error)"
        }
        return $obj.result
    }
    finally {
        $client.Close()
    }
}

function Try-Control {
    param([int]$Port, [string]$Cmd, [hashtable]$CmdArgs = @{})
    try { return Invoke-Control $Port $Cmd $CmdArgs } catch { return $null }
}

$panoDir = Join-Path $OutputDir "panoramas"
New-Item -ItemType Directory -Force -Path $panoDir | Out-Null

Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$logFile = Join-Path $GameDir "TABCoopMod.pano_gen.log"
if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Join-Path $GameDir "TheyAreBillions.exe")
$psi.WorkingDirectory = $GameDir
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["TABCOOP_CONTROL_PORT"] = "27901"
$psi.EnvironmentVariables["TABCOOP_AUTO_SHOT"] = "0"
$psi.EnvironmentVariables["TABCOOP_ROLE"] = "pano_gen"
$psi.EnvironmentVariables["TABCOOP_LOG_NAME"] = "TABCoopMod.pano_gen.log"
$psi.EnvironmentVariables.Remove("TABCOOP_TEST")

$scenarios = @(
    @{
        id = "br_100"
        title = "BR 100% Standard Survival"
        subtitle = "Seed 202601 | Pop 1.0x | Duration 1.0x | Vanilla (256) vs 2P (280) vs 3P (304)"
        seed = 202601
        theme = "BR"
        factorPop = 1.0
        factorDuration = 1.0
        modes = @("vanilla", "coop2p", "coop3p")
    },
    @{
        id = "ds_120"
        title = "DS 120% Arid Highlands"
        subtitle = "Seed 202602 | Pop 1.2x | Duration 1.0x | Vanilla (256) vs 2P (280)"
        seed = 202602
        theme = "DS"
        factorPop = 1.2
        factorDuration = 1.0
        modes = @("vanilla", "coop2p")
    },
    @{
        id = "fa_120"
        title = "FA 120% Frozen Tundra"
        subtitle = "Seed 202603 | Pop 1.2x | Duration 1.0x | Vanilla (256) vs 3P (304)"
        seed = 202603
        theme = "FA"
        factorPop = 1.2
        factorDuration = 1.0
        modes = @("vanilla", "coop3p")
    },
    @{
        id = "vo_900"
        title = "VO 900% Caustic Nightmare"
        subtitle = "Seed 202604 | Pop 1.5x | Duration 0.8x | Vanilla (256) vs 2P (280) vs 3P (304)"
        seed = 202604
        theme = "VO"
        factorPop = 1.5
        factorDuration = 0.8
        modes = @("vanilla", "coop2p", "coop3p")
    }
)

$proc = $null
$reportEntries = @()

try {
    Write-Host "[1/3] Launching game instance for panorama generation..."
    $proc = [System.Diagnostics.Process]::Start($psi)

    $deadline = (Get-Date).AddSeconds($BootTimeoutSec)
    while ($true) {
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for mainMenuReady" }
        $st = Try-Control 27901 "lobby_state"
        if ($st -and $st.mainMenuReady) { break }
        Start-Sleep -Milliseconds 300
    }
    Write-Host "[2/3] Main menu ready. Generating same-seed panoramas..."

    foreach ($sc in $scenarios) {
        $scResult = @{
            id = $sc.id
            title = $sc.title
            subtitle = $sc.subtitle
            seed = $sc.seed
            theme = $sc.theme
            factorPop = $sc.factorPop
            factorDuration = $sc.factorDuration
            variants = @()
        }

        foreach ($mode in $sc.modes) {
            $imgName = "$($sc.id)_$mode.jpg"
            $imgPath = Join-Path $panoDir $imgName
            if (Test-Path $imgPath) { Remove-Item $imgPath -Force }

            Write-Host "  -> Generating $($sc.id) [$mode] seed=$($sc.seed) theme=$($sc.theme)..."
            $genRes = Invoke-Control 27901 "start_survival_gen" @{
                seed = $sc.seed
                theme = $sc.theme
                factorPop = $sc.factorPop
                factorDuration = $sc.factorDuration
                mode = $mode
            }
            if (-not $genRes.ok) {
                throw "start_survival_gen failed to start for $($sc.id)/$mode"
            }

            $genDeadline = (Get-Date).AddSeconds(90)
            while ($true) {
                if ((Get-Date) -gt $genDeadline) { throw "Timed out waiting for async survival gen ($($sc.id)/$mode)" }
                Start-Sleep -Milliseconds 350
                $st = Try-Control 27901 "lobby_state"
                if ($st -and -not $st.survivalGenBusy) {
                    if (-not $st.survivalGenOk) {
                        throw "Async survival gen error ($($sc.id)/$mode): $($st.survivalGenError)"
                    }
                    if ($st.inLevel -and -not $st.loading) { break }
                }
            }

            Start-Sleep -Milliseconds 600
            $capRes = Invoke-Control 27901 "capture_panorama" @{
                path = $imgPath
                resolution = $Resolution
                reveal = $true
            }
            $metrics = Invoke-Control 27901 "map_metrics" @{}

            $actualFile = $capRes.path
            if (-not (Test-Path $actualFile)) {
                $pngFallback = [System.IO.Path]::ChangeExtension($imgPath, ".png")
                if (Test-Path $pngFallback) { $actualFile = $pngFallback }
                else { throw "Panorama image missing at $imgPath" }
            }
            $relImg = "panoramas/" + [System.IO.Path]::GetFileName($actualFile)
            $fileBytes = (Get-Item $actualFile).Length

            Write-Host "     Captured $relImg ($([Math]::Round($fileBytes / 1024.0, 1)) KB) | NCells=$($metrics.survivalNCells) Units=$($metrics.rangers)R+$($metrics.soldiers)S Zombies=$($metrics.zombiesTotal) (G=$($metrics.giants),M=$($metrics.mutants)) DV=$($metrics.doomVillages)"

            $scResult.variants += @{
                mode = $mode
                image = $relImg
                sizeKB = [Math]::Round($fileBytes / 1024.0, 1)
                metrics = $metrics
            }
        }
        $reportEntries += $scResult
    }

    $jsonPath = Join-Path $OutputDir "panorama_metrics.json"
    $reportEntries | ConvertTo-Json -Depth 10 | Set-Content -Path $jsonPath -Encoding UTF8
    Write-Host "[3/3] Done! Saved metrics JSON to $jsonPath"
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc | Stop-Process -Force -ErrorAction SilentlyContinue }
    Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force
}
