# extract_sdk.ps1
# Decrypts and extracts embedded assemblies (DXVision.dll, DXPlatform_Desktop.dll, Steamworks.NET.dll, SmartPropertyGrid.dll)
# from TheyAreBillions.exe in memory without modifying any game files on disk.

param(
    [string]$GameDir = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$GameDir = Resolve-TabGameDir $GameDir
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Resolve-TabRepoRoot) "lib"
}

if (-not (Test-Path "$GameDir\TheyAreBillions.exe")) {
    throw "TheyAreBillions.exe not found at: $GameDir"
}

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

Write-Host "[1/3] Loading TheyAreBillions.exe into reflection context..."
$asm = [System.Reflection.Assembly]::LoadFrom("$GameDir\TheyAreBillions.exe")

Write-Host "[2/3] Running <Module>..cctor to initialize Eazfuscator resource resolver..."
[System.Runtime.CompilerServices.RuntimeHelpers]::RunModuleConstructor($asm.ManifestModule.ModuleHandle)

$loaderType = $asm.GetType("#=qfkZ`$KkimwzG_5GjOFAkJH7Cbg6LrXJCCJODXq7ULSO4=")
$nested = $loaderType.GetNestedType("#=zrm4Tcr8sWQul4rpdL1wKVzfwDh`$s", [System.Reflection.BindingFlags]"NonPublic,Static")
$itemType = $nested.GetNestedType("l11ll1ll1l11lll11", [System.Reflection.BindingFlags]"NonPublic,Public")
$extractMethod = $nested.GetMethod("#=zFUNIIXy4ZVvr_E2jFCnWVOeaqXUN", [System.Reflection.BindingFlags]"NonPublic,Static")

$map = [ordered]@{
    "DXVision.dll"          = "b9a9b1ca9f93492687e026fb08597a75"
    "DXPlatform_Desktop.dll"= "2e1a46ead7a4460ababa0e338efe2ccb"
    "Steamworks.NET.dll"    = "dd64f0b32b994eb3ba7dd334c48bd40f"
    "SmartPropertyGrid.dll" = "3822cc7909fd4dfdac2ffabd4f86c34a"
}

Write-Host "[3/3] Extracting embedded assemblies to $OutDir..."
foreach ($kv in $map.GetEnumerator()) {
    $item = [System.Activator]::CreateInstance($itemType)
    $itemType.GetField("#=zx4yyK3PWRv1Ra3UfRvWMQrrrgPh9").SetValue($item, $kv.Value)
    $itemType.GetField("#=z7VXcqaW9cnAt_HhdTbMd71v94gnl").SetValue($item, $true)
    $itemType.GetField("#=zO5mhfqOKC8IrpN1ldeUHWw4=").SetValue($item, $true)
    $bytes = $extractMethod.Invoke($null, @($item))
    $targetPath = Join-Path $OutDir $kv.Key
    [System.IO.File]::WriteAllBytes($targetPath, $bytes)
    Write-Host ("  -> {0,-25} ({1,10:N0} bytes)" -f $kv.Key, $bytes.Length)
}

# Copy read-only reference of TheyAreBillions.exe, SlimDX.dll and System.ValueTuple.dll for MSBuild compile-time reference
Copy-Item "$GameDir\TheyAreBillions.exe" (Join-Path $OutDir "TheyAreBillions.exe") -Force
Copy-Item "$GameDir\SlimDX.dll" (Join-Path $OutDir "SlimDX.dll") -Force
if (Test-Path "$GameDir\System.ValueTuple.dll") {
    Copy-Item "$GameDir\System.ValueTuple.dll" (Join-Path $OutDir "System.ValueTuple.dll") -Force
}
Write-Host "SDK extraction completed successfully!"
