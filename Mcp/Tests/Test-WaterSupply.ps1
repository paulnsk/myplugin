param([string]$Url = 'http://127.0.0.1:19426/mcp/')

# Только чтение. Запускать после CW_MCP_START в ВК с открытым проектом.
$ErrorActionPreference = 'Stop'
$script:requestId = 0
function Invoke-Rpc([string]$Method, [hashtable]$Parameters = @{}) {
    $script:requestId++
    $body = @{ jsonrpc = '2.0'; id = $script:requestId; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 20 -Compress
    $response = Invoke-RestMethod -Uri $Url -Method Post -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'MCP-Protocol-Version' = '2025-11-25' } -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 180
    if ($response.error) { throw ($response.error | ConvertTo-Json -Compress) }
    return $response.result
}
function Read-Tool([string]$Name, [hashtable]$Arguments = @{}) {
    $result = Invoke-Rpc 'tools/call' @{ name = $Name; arguments = $Arguments }
    if ($result.isError) { throw $result.content[0].text }
    $bytes = [Text.Encoding]::UTF8.GetByteCount($result.content[0].text)
    Write-Host "$Name : $bytes bytes"
    return ($result.content[0].text | ConvertFrom-Json).value
}

$null = Invoke-Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'watersupply-smoke'; version = '1' } }
$catalog = Invoke-Rpc 'tools/list'
$names = @($catalog.tools | ForEach-Object name)
foreach ($name in @('watersupply_consumers_get', 'watersupply_consumer_set', 'watersupply_systems_get',
    'watersupply_configuration_set', 'watersupply_pipes_apply', 'watersupply_refresh', 'watersupply_calculate',
    'watersupply_stands_transfer', 'watersupply_stand_cut', 'watersupply_fixture_insert')) {
    if ($names -notcontains $name) { throw "Missing tool: $name" }
}
foreach ($topic in @('watersupply', 'watersupply-settings', 'watersupply-pipes', 'watersupply-calculation')) {
    $null = Read-Tool 'help_read' @{ name = $topic }
}
$null = Read-Tool 'watersupply_consumers_get' @{ limit = 2 }
$null = Read-Tool 'watersupply_systems_get' @{ limit = 2 }
Write-Host 'WaterSupply discovery/read smoke passed. Drawing and calculations require a separate CAD run.'
