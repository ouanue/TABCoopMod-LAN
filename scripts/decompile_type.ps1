# decompile_type.ps1
# Helper script for AI Agents to decompile any class/struct/enum from TheyAreBillions.exe or DXVision.dll on demand.
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/decompile_type.ps1 -Assembly "TheyAreBillions.exe" -TypeName "ZX.Commands.Train"

param(
    [Parameter(Mandatory=$true)]
    [string]$TypeName,

    [string]$Assembly = "TheyAreBillions.exe",
    [string]$WorkspaceDir = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($WorkspaceDir)) {
    $WorkspaceDir = Split-Path -Parent $PSScriptRoot
}

$libDir = Join-Path $WorkspaceDir "lib"
$targetAsm = Join-Path $libDir $Assembly
if (-not (Test-Path $targetAsm)) {
    Write-Host "Library $targetAsm not found, running extract_sdk.ps1 first..."
    & (Join-Path $WorkspaceDir "scripts\extract_sdk.ps1")
}

$globalStore = Join-Path $env:USERPROFILE ".dotnet\tools\.store"
$ilspyExe = Get-ChildItem -Path $globalStore -Filter "ilspycmd.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName

if (-not $ilspyExe) {
    $toolsDir = Join-Path $WorkspaceDir "scripts\.tools"
    $ilspyExe = Get-ChildItem -Path $toolsDir -Filter "ilspycmd.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if (-not $ilspyExe) {
        Write-Host "Installing ilspycmd into $toolsDir..."
        dotnet tool install --tool-path $toolsDir ilspycmd | Out-Null
        $ilspyExe = Get-ChildItem -Path $toolsDir -Filter "ilspycmd.exe" -Recurse | Select-Object -First 1 -ExpandProperty FullName
    }
}

& $ilspyExe -r $libDir -t $TypeName $targetAsm
