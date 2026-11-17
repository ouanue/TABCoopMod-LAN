# control_smoke.ps1
# Minimal smoke test for the in-process TABCoopMod control server (no MCP layer).
# Usage: powershell -ExecutionPolicy Bypass -File scripts/control_smoke.ps1 [-Port 27901]
param([int]$Port = 27901)

$ErrorActionPreference = "Stop"
$client = New-Object System.Net.Sockets.TcpClient("127.0.0.1", $Port)
$stream = $client.GetStream()
$writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))
$writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($stream, (New-Object System.Text.UTF8Encoding($false)))

function Send-Tool([int]$id, [string]$tool, [string]$argsJson) {
    $req = '{"id":' + $id + ',"tool":"' + $tool + '","args":' + $argsJson + '}'
    $writer.WriteLine($req)
    $resp = $reader.ReadLine()
    Write-Host ("[{0}] {1}" -f $tool, $resp)
    return $resp
}

try {
    Send-Tool 1 "ping" "{}" | Out-Null
    Send-Tool 2 "get_state" "{}" | Out-Null
    Send-Tool 3 "list_entities" '{"onlyPlayer":true,"limit":8}' | Out-Null
    Send-Tool 4 "screenshot" '{"tag":"smoke"}' | Out-Null
}
finally {
    $client.Close()
}
