#Requires -Version 5.1
<#
.SYNOPSIS
    从 scripts/TABCoopMod_LAN_installer.ps1 重新生成自解压引导器 TABCoopMod_LAN_AutoInstall.bat。

.DESCRIPTION
    AutoInstall.bat 的结构是：一段固定的 cmd 头 + 若干行
        >>"%B64FILE%" echo <base64>
    把 installer.ps1（UTF-8 带 BOM）的 base64 写进临时文件，再解码成 ps1 执行。

    为什么需要这个脚本：
      - .bat 是唯一发给用户的入口，但它本身不可读，改不了
      - installer.ps1 改了以后必须重新生成 .bat，否则用户拿到的还是旧逻辑
      - base64 必须基于 **UTF-8 带 BOM** 的字节，否则 PowerShell 5.1 会把中文
        按 ANSI 解读，界面全是乱码

    改版本时的注意事项：
      installer.ps1 顶部的 $ExpectedZipSize / $ExpectedDllSize / $ExpectedDllHash /
      $ExpectedBuild 必须与本次打包的产物一致，否则装完自校验会误报失败。

.EXAMPLE
    .\scripts\build_autinstall.ps1
    .\scripts\build_autinstall.ps1 -OutFile .\dist\TABCoopMod_LAN_AutoInstall.bat
#>
[CmdletBinding()]
param(
    [string]$InstallerScript = (Join-Path $PSScriptRoot 'TABCoopMod_LAN_installer.ps1'),
    [string]$OutFile = (Join-Path $PSScriptRoot 'TABCoopMod_LAN_AutoInstall.bat'),
    # base64 每行长度。cmd 单行上限约 8191 字符，120 留足余量。
    [int]$ChunkSize = 120
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $InstallerScript)) {
    throw "找不到安装脚本：$InstallerScript"
}

Write-Host "[1/3] 读取安装脚本 ..." -ForegroundColor Cyan
$text = [System.IO.File]::ReadAllText($InstallerScript)
if ($text -notmatch '[\u4e00-\u9fff]') {
    Write-Host "      注意：脚本里没有中文，可能读错了文件。" -ForegroundColor Yellow
}

# 语法检查，避免把坏脚本打进 .bat
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile($InstallerScript, [ref]$null, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -gt 0) {
    $parseErrors | ForEach-Object { Write-Host "      L$($_.Extent.StartLineNumber): $($_.Message)" -ForegroundColor Red }
    throw "安装脚本有 $($parseErrors.Count) 个语法错误，已中止。"
}
Write-Host "      语法检查通过（$($text.Length) 字符）" -ForegroundColor Green

Write-Host "[2/3] 编码为 base64（UTF-8 带 BOM）..." -ForegroundColor Cyan
$bom = [byte[]](0xEF, 0xBB, 0xBF)
$body = [System.Text.Encoding]::UTF8.GetBytes($text)
$all = New-Object byte[] ($bom.Length + $body.Length)
[Array]::Copy($bom, 0, $all, 0, $bom.Length)
[Array]::Copy($body, 0, $all, $bom.Length, $body.Length)
$b64 = [Convert]::ToBase64String($all)
Write-Host "      $($all.Length) 字节 -> $($b64.Length) 个 base64 字符" -ForegroundColor Green

Write-Host "[3/3] 生成引导器 ..." -ForegroundColor Cyan
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('@echo off')
[void]$sb.AppendLine('setlocal')
[void]$sb.AppendLine('title TABCoopMod LAN installer')
[void]$sb.AppendLine('set "B64FILE=%TEMP%\tabcoop_lan_b64.txt"')
[void]$sb.AppendLine('set "PS1FILE=%TEMP%\TABCoopMod_LAN_installer.ps1"')
[void]$sb.AppendLine('if exist "%B64FILE%" del /q "%B64FILE%" >nul 2>&1')
for ($i = 0; $i -lt $b64.Length; $i += $ChunkSize) {
    $len = [Math]::Min($ChunkSize, $b64.Length - $i)
    [void]$sb.AppendLine('>>"%B64FILE%" echo ' + $b64.Substring($i, $len))
}
[void]$sb.AppendLine('powershell -NoProfile -ExecutionPolicy Bypass -Command "[IO.File]::WriteAllBytes($env:PS1FILE,[Convert]::FromBase64String(([IO.File]::ReadAllText($env:B64FILE)).Trim()))"')
[void]$sb.AppendLine('if not exist "%PS1FILE%" (')
[void]$sb.AppendLine('  echo Failed to extract installer script.')
[void]$sb.AppendLine('  pause')
[void]$sb.AppendLine('  exit /b 1')
[void]$sb.AppendLine(')')
[void]$sb.AppendLine('del /q "%B64FILE%" >nul 2>&1')
[void]$sb.AppendLine('powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1FILE%"')
[void]$sb.AppendLine('del /q "%PS1FILE%" >nul 2>&1')
[void]$sb.AppendLine('endlocal')

# .bat 本身不能带 BOM，cmd 会把 BOM 当命令的一部分
$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

# 回读校验：从生成的 .bat 里把 base64 抽回来解码，必须与原脚本逐字符相同
$b64Back = @()
foreach ($line in (Get-Content -LiteralPath $OutFile)) {
    if ($line -match '^\s*>>\s*"%B64FILE%"\s+echo\s+(.+?)\s*$') { $b64Back += $Matches[1] }
}
$decodedBytes = [Convert]::FromBase64String(($b64Back -join ''))
$decodedText = [System.Text.Encoding]::UTF8.GetString($decodedBytes, 3, $decodedBytes.Length - 3)
$roundTripOk = ($decodedText -eq $text)

Write-Host ''
Write-Host "已生成：$OutFile" -ForegroundColor Green
Write-Host ("  .bat 大小      {0:N0} 字节" -f (Get-Item -LiteralPath $OutFile).Length)
Write-Host ("  base64 行数    {0}" -f $b64Back.Count)
Write-Host ("  回读校验       {0}" -f $(if ($roundTripOk) { '通过（解码结果与原脚本逐字符一致）' } else { '失败！' })) -ForegroundColor $(if ($roundTripOk) { 'Green' } else { 'Red' })

if (-not $roundTripOk) { exit 1 }
