[CmdletBinding()]
param([string]$Url)

# Отдельный пробный проект. Не закрывает и не изменяет уже открытый рабочий набор.
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $Url) { $Url = "http://127.0.0.1:$($settings.port)/mcp/" }
Add-Type -AssemblyName System.Net.Http
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromSeconds(120)
$session = [Guid]::NewGuid().ToString('N')
$samples = New-Object 'System.Collections.Generic.List[object]'
$script:sequence = 0

function Invoke-MeasuredTool([string]$Name, [hashtable]$Arguments) {
    $script:sequence++
    $id = "creation-$session-$script:sequence"
    $json = @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } } | ConvertTo-Json -Depth 20 -Compress
    $body = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
    $response = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = $client.PostAsync($Url, $body).GetAwaiter().GetResult()
        $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $watch.Stop()
        $response.EnsureSuccessStatusCode() | Out-Null
        $rpc = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        $payload = $rpc.result.content[0].text | ConvertFrom-Json
        $samples.Add([pscustomobject]@{
            requestId = $id; callId = $payload.callId; name = $Name
            elapsedMs = $watch.Elapsed.TotalMilliseconds; responseBytes = $bytes.Length
            itemCount = @($Arguments.items).Count; isError = [bool]($rpc.error -or $rpc.result.isError)
        })
        if ($rpc.error -or $rpc.result.isError) { throw ([Text.Encoding]::UTF8.GetString($bytes)) }
        return $payload.value
    }
    finally {
        $body.Dispose()
        if ($response) { $response.Dispose() }
    }
}

function Invoke-MeasuredItem([string]$Name, [hashtable]$Arguments) {
    $batch = Invoke-MeasuredTool $Name @{ items = @($Arguments) }
    return $batch.results[0]
}

try {
    $context = Invoke-MeasuredTool 'app_context' @{}
    if ($context.workspacePath) { throw 'Уже открыт рабочий набор. Этот тест требует закрыть его вручную.' }
    if (-not ($context.projectTypes | Where-Object name -eq 'Электрика')) { throw 'Этот проверочный сценарий предназначен для Электрики.' }
    $project = Invoke-MeasuredTool 'project_create' @{ directory = "C:\test\ProjectStudioMcp\$session"; projectType = 'Электрика' }
    $plan = Invoke-MeasuredTool 'plan_create' @{ name = 'Performance'; projectPath = $project.projectPath }
    $null = Invoke-MeasuredItem 'contour_create' @{
        planPath = $plan.planPath; type = 'ScsFloorContour'
        vertices = @(@(0,0), @(17000,0), @(17000,10000), @(0,10000))
        properties = @{ BuildingIndex = 1; RoomFloor = 1; FloorAbsolutHeight = 0 }
        marker = @{ position = @(0,0); height = 0; rotationDegrees = 0; label = 'PERF-F1' }
    }
    $null = Invoke-MeasuredItem 'contour_create' @{
        planPath = $plan.planPath; type = 'Nanosoft.CAE.RoomsAndFloors.RoomContour'
        vertices = @(@(1000,1000), @(16000,1000), @(16000,9000), @(1000,9000))
        properties = @{ RoomName = 'Профилирование размещения' }
    }
    $dbType = 'Nanosoft.ObjectLib.ESN.Db.DbImpl.DbElLighting'
    $products = Invoke-MeasuredTool 'database_search' @{ type = $dbType; limit = 1 }
    $product = $products.products | Select-Object -First 1
    if (-not $product) { throw 'В загруженных БД нет светильников.' }
    if (-not $product.isProject) {
        $product = Invoke-MeasuredTool 'database_import' @{ type = $dbType; dbId = $product.dbId; sourcePath = $product.sourcePath }
    }
    $lightIds = New-Object 'System.Collections.Generic.List[string]'
    # Четыре отдельных вызова, чтобы не спрятать задержку одного светильника за пакетным API.
    foreach ($x in @(3000,6500,10000,13500)) {
        $created = Invoke-MeasuredTool 'objects_create' @{
            planPath = $plan.planPath
            items = @(@{ type = 'ElLighting'; position = @($x,3000); dbType = $dbType; dbId = $product.dbId; properties = @{ MountingHeightMm = 3000 } })
        }
        $lightIds.Add([string]$created.createdObjectIds[0])
    }
    # Ещё три светильника одним пакетом для сравнения фиксированных расходов вызова.
    $items = @(foreach ($x in @(3000,6500,10000)) {
        @{ type = 'ElLighting'; position = @($x,7000); dbType = $dbType; dbId = $product.dbId; properties = @{ MountingHeightMm = 3000 } }
    })
    $created = Invoke-MeasuredTool 'objects_create' @{ planPath = $plan.planPath; items = $items }
    foreach ($id in $created.createdObjectIds) { $lightIds.Add([string]$id) }
    foreach ($index in @(0,1,2,4,5)) {
        $trace = Invoke-MeasuredItem 'traces_connect' @{
            planPath = $plan.planPath; type = 'ScsTrace'; firstId = $lightIds[$index]; secondId = $lightIds[$index+1]; height = 3000
        }
        $actual = Invoke-MeasuredItem 'objects_get' @{ id = $trace.trace.id; fields = @('FirstPointHeight','LastPointHeight') }
        if ($actual.properties.FirstPointHeight -ne 3000 -or $actual.properties.LastPointHeight -ne 3000) { throw 'Неверная высота трассы.' }
    }
    foreach ($id in $lightIds) {
        $actual = Invoke-MeasuredItem 'objects_get' @{ id = $id; fields = @('MountingHeightMm') }
        if ($actual.dbId -ne $product.dbId -or $actual.properties.MountingHeightMm -ne 3000) { throw 'Неверная привязка или высота светильника.' }
    }
    $null = Invoke-MeasuredTool 'project_save' @{}
    $null = Invoke-MeasuredTool 'view_zoom_all' @{}
    Write-Output "Проект: $($project.workspacePath)"
}
finally {
    $client.Dispose()
    $runtime = $settings.runtimeDirectory
    [IO.Directory]::CreateDirectory($runtime) | Out-Null
    $path = Join-Path $runtime "creation-timing-$session.json"
    $report = @{ url = $Url; time = [DateTimeOffset]::Now.ToString('o'); samples = $samples.ToArray() } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($path, $report, (New-Object Text.UTF8Encoding($false)))
    $samples | Format-Table name, itemCount, @{ Name = 'ms'; Expression = { [Math]::Round($_.elapsedMs, 2) } }, responseBytes, isError -AutoSize | Out-Host
    Write-Host "Клиентские замеры: $path"
}
