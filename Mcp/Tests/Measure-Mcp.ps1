[CmdletBinding()]
param(
    [string]$Url,
    [ValidateRange(1, 100)][int]$Repeats = 5
)

# Только чтение: nanoCAD и сервер заранее запускает пользователь.
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $Url) { $Url = "http://127.0.0.1:$($settings.port)/mcp/" }
Add-Type -AssemblyName System.Net.Http
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(60)
$session = [Guid]::NewGuid().ToString('N')
$samples = New-Object 'System.Collections.Generic.List[object]'
$sequence = 0
$cases = @(
    @{ label = 'initialize'; method = 'initialize'; params = @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'psc-profiler'; version = '1' } } },
    @{ label = 'ping'; method = 'ping'; params = @{} },
    @{ label = 'tools/list'; method = 'tools/list'; params = @{} },
    @{ label = 'help_read'; method = 'tools/call'; params = @{ name = 'help_read'; arguments = @{ name = 'platform' } } },
    @{ label = 'server_status'; method = 'tools/call'; params = @{ name = 'server_status'; arguments = @{} } },
    @{ label = 'app_context'; method = 'tools/call'; params = @{ name = 'app_context'; arguments = @{} } },
    @{ label = 'types_search'; method = 'tools/call'; params = @{ name = 'types_search'; arguments = @{ query = 'Contour'; limit = 5 } } }
)

try {
    foreach ($case in $cases) {
        $count = if ($case.label -eq 'initialize') { 1 } else { $Repeats }
        for ($iteration = 1; $iteration -le $count; $iteration++) {
            $sequence++
            $id = "profile-$session-$sequence"
            $json = @{ jsonrpc = '2.0'; id = $id; method = $case.method; params = $case.params } | ConvertTo-Json -Depth 10 -Compress
            $body = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
            $response = $null
            $watch = [Diagnostics.Stopwatch]::StartNew()
            try {
                $response = $client.PostAsync($Url, $body).GetAwaiter().GetResult()
                $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                $watch.Stop()
                $response.EnsureSuccessStatusCode() | Out-Null
                $rpc = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
                if ($rpc.error -or $rpc.result.isError) { throw ([Text.Encoding]::UTF8.GetString($bytes)) }
                $payload = if ($case.method -eq 'tools/call') { $rpc.result.content[0].text | ConvertFrom-Json } else { $null }
                $samples.Add([pscustomobject]@{
                    requestId = $id; callId = $payload.callId
                    name = $case.label; iteration = $iteration
                    elapsedMs = $watch.Elapsed.TotalMilliseconds; responseBytes = $bytes.Length
                })
            }
            finally {
                $body.Dispose()
                if ($response) { $response.Dispose() }
            }
        }
    }
}
finally { $client.Dispose() }

$runtime = $settings.runtimeDirectory
[IO.Directory]::CreateDirectory($runtime) | Out-Null
$path = Join-Path $runtime "client-timing-$session.json"
$report = @{ url = $Url; time = [DateTimeOffset]::Now.ToString('o'); samples = $samples.ToArray() } | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($path, $report, (New-Object Text.UTF8Encoding($false)))
$samples | Format-Table name, iteration, @{ Name = 'ms'; Expression = { [Math]::Round($_.elapsedMs, 2) } }, responseBytes -AutoSize
Write-Output "Client timings: $path"
