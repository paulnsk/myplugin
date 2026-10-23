[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConnectionFile,
    [switch]$CreateElectricalSample,
    [string]$SampleDirectory = ''
)

$ErrorActionPreference = 'Stop'
$connection = Get-Content -LiteralPath $ConnectionFile -Raw -Encoding UTF8 | ConvertFrom-Json
$headers = @{ 'MCP-Protocol-Version' = '2025-11-25' }
$script:requestId = 0

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-Rpc([string]$Method, [hashtable]$Parameters = @{}) {
    $script:requestId++
    $body = @{ jsonrpc = '2.0'; id = $script:requestId; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 30 -Compress
    $response = Invoke-RestMethod -Uri $connection.url -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 180
    Assert-That ($response.id -eq $script:requestId) 'JSON-RPC response id mismatch.'
    if ($response.error) { throw ($response.error | ConvertTo-Json -Compress) }
    if ($Method -eq 'tools/call') {
        return [pscustomobject]@{ isError = $response.result.isError; data = ($response.result.content[0].text | ConvertFrom-Json) }
    }
    return $response.result
}

function Invoke-Tool([string]$Name, [hashtable]$Arguments = @{}) {
    $result = Invoke-Rpc 'tools/call' @{ name = $Name; arguments = $Arguments }
    if ($result.isError) { throw ($result.data | ConvertTo-Json -Depth 20) }
    return $result.data.value
}

# Один элемент пакетной команды; wire-формат всегда items.
function Invoke-One([string]$Name, [hashtable]$Arguments) {
    $batch = Invoke-Tool $Name @{ items = @($Arguments) }
    return $batch.results[0]
}

function Assert-HttpStatus([int]$Expected, [hashtable]$RequestHeaders, [string]$Method = 'Get') {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $connection.url -Method $Method -Headers $RequestHeaders -TimeoutSec 10 | Out-Null
        throw "Expected HTTP $Expected but request succeeded."
    }
    catch [System.Net.WebException] {
        Assert-That ([int]$_.Exception.Response.StatusCode -eq $Expected) "Expected HTTP $Expected, received $($_.Exception.Response.StatusCode)."
    }
}

$initialized = Invoke-Rpc 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'psc-smoke-test'; version = '1' } }
Assert-That ($initialized.protocolVersion -eq '2025-11-25') 'Protocol negotiation failed.'
$notification = @{ jsonrpc = '2.0'; method = 'notifications/initialized' } | ConvertTo-Json -Compress
$accepted = Invoke-WebRequest -UseBasicParsing -Uri $connection.url -Method Post -Headers $headers -ContentType 'application/json' -Body $notification
Assert-That ($accepted.StatusCode -eq 202) 'Notification was not accepted.'
$null = Invoke-Rpc 'ping'
$catalog = Invoke-Rpc 'tools/list'
$names = @($catalog.tools | ForEach-Object { $_.name })
Assert-That ($names.Count -eq @($names | Select-Object -Unique).Count) 'Duplicate tool names.'
foreach ($required in @('app_context', 'project_create', 'plan_create', 'plan_import', 'plan_get_bounds', 'ugo_search', 'types_search', 'types_describe', 'database_import', 'objects_create', 'objects_get', 'contour_create', 'traces_connect', 'help_read')) {
    Assert-That ($names -contains $required) "Tool missing: $required"
}
foreach ($skillName in @('platform', 'electrica', 'heating')) {
    $skill = Invoke-Tool 'help_read' @{ name = $skillName }
    Assert-That ($skill.markdown -match 'description:') "Invalid skill: $skillName"
}
foreach ($name in @('plan_import', 'plan_get_bounds', 'objects_get', 'objects_update', 'objects_delete', 'contour_create', 'traces_connect', 'objects_structure', 'objects_add_child', 'ports_connect')) {
    $schema = ($catalog.tools | Where-Object name -eq $name).inputSchema
    Assert-That (@($schema.properties.PSObject.Properties.Name).Count -eq 1 -and $schema.required[0] -eq 'items' -and $schema.properties.items.items.type -eq 'object') "Expected only items schema: $name"
    $rejected = Invoke-Rpc 'tools/call' @{ name = $name; arguments = @{} }
    Assert-That ($rejected.isError -eq $true) "Missing items was accepted: $name"
}
$rejected = Invoke-Rpc 'tools/call' @{ name = 'help_read'; arguments = @{ name = '../outside' } }
Assert-That ($rejected.isError -eq $true) 'Help path traversal was not rejected.'
$rejected = Invoke-Rpc 'tools/call' @{ name = 'help_read'; arguments = @{ name = 'platform'; surprise = 1 } }
Assert-That ($rejected.isError -eq $true) 'Unknown tool argument was silently accepted.'
Assert-HttpStatus 405 @{}
$badOrigin = @{ Origin = 'https://example.invalid' }
Assert-HttpStatus 403 $badOrigin
$status = Invoke-Tool 'server_status'
Write-Output "Protocol/discovery/help/security: OK ($($names.Count) tools). Log: $($status.logPath)"

if (-not $CreateElectricalSample) { return }
$context = Invoke-Tool 'app_context'
Assert-That (-not $context.workspacePath) 'Close the current workspace manually before the sample test; it will not be replaced.'
Assert-That (@($context.projectTypes | Where-Object { $_.name -eq 'Электрика' }).Count -eq 1) 'Run the sample test in Electrica.'
if (-not $SampleDirectory) { $SampleDirectory = Join-Path 'C:\test\ProjectStudioMcp' ([Guid]::NewGuid().ToString()) }
$project = Invoke-Tool 'project_create' @{ directory = $SampleDirectory; projectType = 'Электрика' }
Write-Output "Project: $($project.workspacePath)"
$plan = Invoke-Tool 'plan_create' @{ name = 'MCP-1'; projectPath = $project.projectPath }
$null = Invoke-Tool 'project_save'
# Копируем ещё пустой DWG: проверка импорта не должна дублировать ID уже нарисованных BIM-объектов.
$sourceDirectory = Join-Path $SampleDirectory 'import-source'
[IO.Directory]::CreateDirectory($sourceDirectory) | Out-Null
$sourcePlan = Join-Path $sourceDirectory 'Imported.dwg'
[IO.File]::Copy($plan.planPath, $sourcePlan, $false)
$imported = Invoke-One 'plan_import' @{ path = $sourcePlan; modelScale = 1; paperScale = 100 }
Assert-That ($imported.planPath -eq (Join-Path $SampleDirectory 'Imported.dwg') -and (Test-Path -LiteralPath $sourcePlan)) 'Plan was not copied or the source disappeared.'
$duplicateImport = Invoke-Rpc 'tools/call' @{ name = 'plan_import'; arguments = @{ items = @(@{ path = $sourcePlan }) } }
Assert-That ($duplicateImport.isError -eq $true) 'Duplicate plan import was accepted.'
$null = Invoke-Tool 'plan_activate' @{ planPath = $plan.planPath }

function Find-Type([string]$Query) {
    $found = Invoke-Tool 'types_search' @{ query = $Query }
    $exact = @($found.types | Where-Object { ($_.type -split '\.')[-1] -eq $Query })
    Assert-That ($exact.Count -eq 1) "Expected one registered $Query, received $($exact.Count). Inspect types_search."
    return $exact[0].type
}

$floorType = Find-Type 'ScsFloorContour'
$floor = Invoke-One 'contour_create' @{
    planPath = $plan.planPath; type = $floorType
    vertices = @(@(0,0), @(16000,0), @(16000,10000), @(0,10000))
    properties = @{ BuildingIndex = 1; RoomFloor = 1; FloorAbsolutHeight = 0 }
    marker = @{ position = @(0,0); label = 'MCP-F1'; height = 0; rotationDegrees = 0 }
}
Assert-That ([bool]$floor.marker.id) 'Floor marker was not returned.'
$marker = Invoke-One 'objects_get' @{ id = $floor.marker.id }
Assert-That ($marker.markerContourId -eq $floor.contour.id) 'Marker is not explicitly bound to floor.'
$null = Invoke-One 'objects_update' @{
    id = $floor.contour.id
    vertices = @(@(0,0), @(18000,0), @(18000,12000), @(0,12000))
}
$resized = Invoke-One 'objects_get' @{ id = $floor.contour.id; geometry = $true }
Assert-That ($resized.vertices.Count -eq 5 -and $resized.vertices[2][0] -eq 18000 -and $resized.vertices[2][1] -eq 12000) 'Contour resize was not persisted.'
$bounds = (Invoke-One 'plan_get_bounds' @{ planPath = $plan.planPath }).bounds
Assert-That ($null -ne $bounds -and $bounds.min.Count -eq 3 -and $bounds.max.Count -eq 3) 'Expected two 3D bounds points.'
Assert-That ($bounds.min[0] -le 0 -and $bounds.min[1] -le 0 -and $bounds.max[0] -ge 18000 -and $bounds.max[1] -ge 12000) 'Drawing bounds do not contain the resized floor.'
$markerAfter = Invoke-One 'objects_get' @{ id = $floor.marker.id }
Assert-That (($markerAfter.position -join ',') -eq ($marker.position -join ',')) 'Resize unexpectedly moved marker.'
$rejected = Invoke-Rpc 'tools/call' @{ name = 'objects_update'; arguments = @{ items = @(@{ id = $floor.contour.id; vertices = @(@(0,0),@(1,1)) }) } }
Assert-That ($rejected.isError -eq $true) 'Invalid polygon accepted.'

# Explicit heights must not depend on the incoming JSON property order.
foreach ($heights in @([ordered]@{ FloorHeightMm = 3300; RoomFloorHeight = 3000 }, [ordered]@{ RoomFloorHeight = 3000; FloorHeightMm = 3600 })) {
    $null = Invoke-One 'objects_update' @{ id = $floor.contour.id; properties = $heights }
    $actual = Invoke-One 'objects_get' @{ id = $floor.contour.id; fields = @('FloorHeightMm','RoomFloorHeight') }
    Assert-That ($actual.properties.FloorHeightMm -eq $heights.FloorHeightMm -and $actual.properties.RoomFloorHeight -eq 3000) 'Explicit floor/room heights depend on JSON property order.'
}
$null = Invoke-One 'objects_update' @{ id = $floor.contour.id; properties = @{ FloorHeightMm = 3000; RoomFloorHeight = 3000 } }
# RoomContour is current; the old product-specific ScsRoomContour is a legacy conversion type.
$roomType = Find-Type 'RoomContour'
$roomDescription = Invoke-Tool 'types_describe' @{ type = $roomType }
$roomProperties = @{}
if (@($roomDescription.properties.name) -contains 'RoomName') { $roomProperties.RoomName = 'MCP test room' }
$room = Invoke-One 'contour_create' @{
    planPath = $plan.planPath; type = $roomType
    vertices = @(@(1000,1000), @(15000,1000), @(15000,9000), @(1000,9000)); properties = $roomProperties
}

$lighting = Find-Type 'ElLighting'
$lightDescription = Invoke-Tool 'types_describe' @{ type = $lighting }
$ugos = Invoke-Tool 'ugo_search' @{ type = $lighting; limit = 1 }
Assert-That ($ugos.items.Count -eq 1 -and [bool]$ugos.items[0].name) 'No luminaire UGO.'
Assert-That (@($ugos.items[0].PSObject.Properties.Name).Count -eq 2) 'UGO response should contain only name/file, no image payload.'
$chosen = $null
foreach ($dbType in $lightDescription.databaseTypes) {
    $products = Invoke-Tool 'database_search' @{ type = $dbType; limit = 10 }
    $chosen = $products.products | Sort-Object -Property isProject -Descending | Select-Object -First 1
    if ($chosen) { break }
}
Assert-That ([bool]$chosen) 'No luminaire products in loaded databases. Load a suitable product database and resume; no fake product will be used.'
if (-not $chosen.isProject) {
    $chosen = Invoke-Tool 'database_import' @{ type = $chosen.dbType; dbId = $chosen.dbId; sourcePath = $chosen.sourcePath }
}
$lights = Invoke-Tool 'objects_create' @{
    planPath = $plan.planPath
    defaults = @{ type = $lighting; dbType = $chosen.dbType; dbId = $chosen.dbId; ugoName = $ugos.items[0].name; ugoFile = $ugos.items[0].file; properties = @{ MountingHeightMm = 2500 } }
    items = @(
        @{ position = @(4000,5000) },
        @{ position = @(8000,5000); properties = @{ MountingHeightMm = 3000 } },
        @{ position = @(11000,5000) }
    )
}
Assert-That ($lights.createdObjectIds.Count -eq 3) 'Expected three created lights.'
$readBatch = Invoke-Tool 'objects_get' @{ items = @($lights.createdObjectIds | ForEach-Object { @{ id = $_; fields = @('MountingHeightMm') } }) }
Assert-That ($readBatch.results.Count -eq 3) 'Batch read count mismatch.'
Assert-That (($readBatch.results.properties.MountingHeightMm -join ',') -eq '2500,3000,2500') 'Default properties or item override failed.'
$rejectedDefaults = Invoke-Rpc 'tools/call' @{ name = 'objects_create'; arguments = @{ planPath = $plan.planPath; defaults = @{ type = $lighting; position = @(4000,5000) }; items = @(@{ position = @(4000,5000) }) } }
Assert-That ($rejectedDefaults.isError -eq $true) 'Position was accepted in defaults.'
$foundFirst = Invoke-Tool 'objects_find' @{ type = $lighting; planPath = $plan.planPath; limit = 1 }
Assert-That ($foundFirst.total -eq 3 -and $foundFirst.objects.Count -eq 1 -and $foundFirst.nextOffset -eq 1) 'First search page mismatch.'
Assert-That ((($foundFirst.objects[0].PSObject.Properties.Name | Sort-Object) -join ',') -eq 'id,title') 'Search card is not minimal.'
$foundRest = Invoke-Tool 'objects_find' @{ type = $lighting; planPath = $plan.planPath; offset = $foundFirst.nextOffset; limit = 2 }
Assert-That ($foundRest.objects.Count -eq 2 -and $null -eq $foundRest.nextOffset) 'Last search page mismatch.'
Assert-That (@((@($foundFirst.objects.id) + @($foundRest.objects.id)) | Select-Object -Unique).Count -eq 3) 'Search pages overlap.'
$updatedBatch = Invoke-Tool 'objects_update' @{ items = @($lights.createdObjectIds | ForEach-Object { @{ id = $_; properties = @{ MountingHeightMm = 3000 } } }) }
Assert-That ($updatedBatch.results.Count -eq 3) 'Batch update count mismatch.'
$partial = Invoke-Rpc 'tools/call' @{ name = 'objects_get'; arguments = @{ items = @(@{ id = $lights.createdObjectIds[0] }, @{ id = 'not-a-guid' }) } }
Assert-That ($partial.isError -eq $true -and $partial.data.completedResults.Count -eq 1 -and $partial.data.failedItemIndex -eq 1) 'Batch partial failure details missing.'
foreach ($id in $lights.createdObjectIds) {
    $actual = Invoke-One 'objects_get' @{ id = $id; fields = @('MountingHeightMm') }
    Assert-That ($actual.properties.MountingHeightMm -eq 3000) 'Light mounting height mismatch.'
    Assert-That ($actual.dbId -eq $chosen.dbId) 'Luminaire DB binding mismatch.'
}
$traceType = Find-Type 'ScsTrace'
$traceBatch = Invoke-Tool 'traces_connect' @{
    items = @(
        @{ planPath = $plan.planPath; type = $traceType; firstId = $lights.createdObjectIds[0]; secondId = $lights.createdObjectIds[1]; height = 3000; waypoints = @(@(6000,7000)) },
        @{ planPath = $plan.planPath; type = $traceType; firstId = $lights.createdObjectIds[1]; secondId = $lights.createdObjectIds[2]; height = 3000 }
    )
}
Assert-That ($traceBatch.results.Count -eq 2) 'Batch trace count mismatch.'
$trace = $traceBatch.results[0]
$traceActual = Invoke-One 'objects_get' @{ id = $trace.trace.id; fields = @('FirstPointHeight','LastPointHeight') }
Assert-That ($traceActual.properties.FirstPointHeight -eq 3000 -and $traceActual.properties.LastPointHeight -eq 3000) 'Trace height mismatch.'
$routeBefore = Invoke-One 'objects_get' @{ id = $trace.trace.id; geometry = $true }
Assert-That ($routeBefore.vertices.Count -eq 3) 'Creation waypoints were not persisted.'
$null = Invoke-One 'objects_update' @{ id = $trace.trace.id; waypoints = @(@(4000,8000),@(8000,8000)) }
$routeAfter = Invoke-One 'objects_get' @{ id = $trace.trace.id; geometry = $true }
Assert-That ($routeAfter.vertices.Count -eq 4 -and $routeAfter.vertices[1][1] -eq 8000 -and $routeAfter.vertices[2][1] -eq 8000) 'Edited waypoints were not persisted.'
Assert-That (($routeBefore.vertices[0] -join ',') -eq ($routeAfter.vertices[0] -join ',') -and ($routeBefore.vertices[-1] -join ',') -eq ($routeAfter.vertices[-1] -join ',')) 'Trace endpoints moved.'
Assert-That ($routeAfter.connectedObjectIds -contains $lights.createdObjectIds[0] -and $routeAfter.connectedObjectIds -contains $lights.createdObjectIds[1]) 'Trace endpoint links were lost.'
$null = Invoke-One 'objects_update' @{ id = $trace.trace.id; waypoints = @() }
$straight = Invoke-One 'objects_get' @{ id = $trace.trace.id; geometry = $true }
Assert-That ($straight.vertices.Count -eq 2 -and $straight.connectedObjectIds -contains $lights.createdObjectIds[0] -and $straight.connectedObjectIds -contains $lights.createdObjectIds[1]) 'Straightening lost geometry or links.'
$temporary = Invoke-Tool 'objects_create' @{ planPath = $plan.planPath; defaults = @{ type = $lighting; dbType = $chosen.dbType; dbId = $chosen.dbId }; items = @(@{ position = @(16000,10000) }) }
$deleted = Invoke-Tool 'objects_delete' @{ items = @(@{ id = $temporary.createdObjectIds[0] },@{ id = $trace.trace.id }) }
Assert-That ($deleted.results.Count -eq 2) 'Delete batch failed.'
$missing = Invoke-Rpc 'tools/call' @{ name = 'objects_get'; arguments = @{ items = @(@{ id = $trace.trace.id }) } }
Assert-That ($missing.isError -eq $true) 'Deleted trace can still be opened.'
$oldTraceId = $trace.trace.id
$trace = Invoke-One 'traces_connect' @{ planPath = $plan.planPath; type = $traceType; firstId = $lights.createdObjectIds[0]; secondId = $lights.createdObjectIds[1]; height = 3000 }
Assert-That ($trace.trace.id -ne $oldTraceId) 'Reconnection returned the deleted trace.'
$boardType = Find-Type 'ElBoard'
$boardDescription = Invoke-Tool 'types_describe' @{ type = $boardType }
$boardProduct = (Invoke-Tool 'database_search' @{ type = $boardDescription.databaseTypes[0]; limit = 1 }).products[0]
Assert-That ([bool]$boardProduct) 'No board product in loaded databases.'
if (-not $boardProduct.isProject) {
    $boardProduct = Invoke-Tool 'database_import' @{ type = $boardProduct.dbType; dbId = $boardProduct.dbId; sourcePath = $boardProduct.sourcePath }
}
$board = Invoke-Tool 'objects_create' @{
    planPath = $plan.planPath
    items = @(@{ type = $boardType; position = @(13000,3000); dbType = $boardProduct.dbType; dbId = $boardProduct.dbId })
}
$group = Invoke-One 'objects_add_child' @{ id = $board.createdObjectIds[0] }
Assert-That ($group.createdObjectIds.Count -eq 1) 'Expected one new outgoing group.'
$groupType = Invoke-Tool 'types_describe' @{ id = $group.createdObjectIds[0]; limit = 3 }
Assert-That ([bool]$groupType.type -and $groupType.properties.Count -le 3) 'Internal group metadata or paging failed.'
$connected = Invoke-One 'ports_connect' @{ portId = $group.createdObjectIds[0]; slaveIds = @($lights.createdObjectIds) }
Assert-That ($connected.connectedObjectIds.Count -eq 3) 'Not all lights connected to group.'
$slaves = Invoke-One 'objects_structure' @{ id = $group.createdObjectIds[0]; relation = 'slaves' }
Assert-That ($slaves.total -eq 3) 'Connections not persisted.'
Assert-That (@($slaves.objects.id | Where-Object { $_ -notin $lights.createdObjectIds }).Count -eq 0) 'Wrong connected consumers.'
$binding = Invoke-One 'objects_update' @{ id = $lights.createdObjectIds[0]; dbType = $chosen.dbType; dbId = $chosen.dbId }
$bound = Invoke-One 'objects_get' @{ id = $binding.id }
Assert-That ($bound.dbId -eq $chosen.dbId -and $bound.dbType -eq $chosen.dbType) 'DB binding was not persisted.'
$branchNodes = Invoke-Tool 'objects_create' @{
    planPath = $plan.planPath
    defaults = @{ type = $lighting; dbType = $chosen.dbType; dbId = $chosen.dbId }
    items = @(@{ position = @(20000,2000) }, @{ position = @(26000,2000) }, @{ position = @(23000,6000) })
}
$main = Invoke-One 'traces_connect' @{ planPath = $plan.planPath; type = $traceType; firstId = $branchNodes.createdObjectIds[0]; secondId = $branchNodes.createdObjectIds[1]; height = 3000 }
$branch = Invoke-One 'traces_connect' @{ planPath = $plan.planPath; type = $traceType; firstId = $main.trace.id; secondId = $branchNodes.createdObjectIds[2]; branch = $true; height = 3000 }
$branchGeometry = Invoke-One 'objects_get' @{ id = $branch.trace.id; geometry = $true }
Assert-That ($branchGeometry.vertices.Count -ge 2 -and $branchGeometry.connectedObjectIds -contains $branchNodes.createdObjectIds[2]) 'Branch geometry or connection was not persisted.'
$null = Invoke-Tool 'project_save'
$null = Invoke-Tool 'view_zoom_all'
Write-Output "Electrical sample: OK. Workspace: $($project.workspacePath)"
Write-Output "Floor: $($floor.contour.id); room: $($room.contour.id); lights: $($lights.createdObjectIds -join ', '); trace: $($trace.trace.id)"
