# build_mcp_server.ps1
# Builds the standalone MCP stdio server (net10.0). Run once after changing the server; the
# .mcp.json entry points at src/TABCoopMod.Mcp/bin/Release/net10.0/TABCoopMod.Mcp.dll.
$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$proj = Join-Path $RepoRoot "src\TABCoopMod.Mcp\TABCoopMod.Mcp.csproj"

& dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { Write-Error "MCP server build failed"; exit $LASTEXITCODE }
Write-Host "MCP server built: src\TABCoopMod.Mcp\bin\Release\net10.0\TABCoopMod.Mcp.dll"
