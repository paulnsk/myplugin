[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConnectionFile,
    [switch]$CreateSample,
    [string]$SampleDirectory = ''
)

# Пользователь запускает CAD/сервер. Без CreateSample проверяются только протокол, каталог и справка.
. (Join-Path $PSScriptRoot 'Test-Mcp.ps1') -ConnectionFile $ConnectionFile -CreateElectricalSample:$CreateSample -SampleDirectory $SampleDirectory

$electricalTools = @('electrica_lighting_place', 'electrica_cables_route', 'electrica_cables_inspect',
    'electrica_cables_bind', 'electrica_calculate', 'electrica_group_equipment_add', 'electrica_supply_get', 'electrica_supply_update')
foreach ($name in $electricalTools) {
    Assert-That ($names -contains $name) "Missing product tool: $name"
    $tool = $catalog.tools | Where-Object name -eq $name
    Assert-That ($tool.annotations.readOnlyHint -eq ($name -eq 'electrica_supply_get')) "Incorrect mutation annotation: $name"
}
foreach ($topic in @('electrica', 'electrica-lighting', 'electrica-network', 'electrica-cables')) {
    $help = Invoke-Tool 'help_read' @{ name = $topic }
    $bytes = [Text.Encoding]::UTF8.GetByteCount($help.markdown)
    Assert-That ($bytes -lt 8000) "Help topic is too large: $topic ($bytes bytes)"
    Write-Output "Help $topic : $bytes UTF-8 bytes"
}
$roots = Invoke-Tool 'help_list'
Assert-That ($roots.skills -notcontains 'electrica-lighting') 'Root help unexpectedly includes every detailed topic.'
$topics = Invoke-Tool 'help_list' @{ prefix = 'electrica' }
Assert-That ($topics.skills -contains 'electrica-cables' -and $topics.skills.Count -eq 4) 'Product topic discovery failed.'
Write-Output "Full catalog: $([Text.Encoding]::UTF8.GetByteCount(($catalog | ConvertTo-Json -Depth 40 -Compress))) UTF-8 bytes"

if (-not $CreateSample) { return }

function Import-TestProduct([string]$Type, [string]$Query = '') {
    $product = (Invoke-Tool 'database_search' @{ type = $Type; query = $Query; limit = 1 }).products[0]
    Assert-That ([bool]$product) "Missing fixture product: $Type / $Query"
    if ($product.isProject) { return $product }
    return Invoke-Tool 'database_import' @{ type = $product.dbType; dbId = $product.dbId; sourcePath = $product.sourcePath }
}

# Отдельная прямоугольная комната; уже проверенные объекты базового smoke не удаляются.
$kiRoom = Invoke-One 'contour_create' @{
    planPath = $plan.planPath; type = $roomType
    vertices = @(@(30000,1000), @(36000,1000), @(36000,7000), @(30000,7000))
    properties = @{ RoomName = 'MCP Ki'; RoomHeightMm = 3000 }
}
$kiProduct = Import-TestProduct 'DbElLighting' '418 (595)'
$kiArgs = @{
    planPath = $plan.planPath; roomId = $kiRoom.contour.id; dbId = $kiProduct.dbId
    lux = 300; mountingHeight = 3000; workSurfaceHeight = 800
    ceilingReflectance = 0.7; wallsReflectance = 0.5; floorReflectance = 0.3; reserveFactor = 1.3
}
$ki = Invoke-One 'electrica_lighting_place' $kiArgs
Assert-That ($ki.count -gt 0 -and $ki.createdObjectIds.Count -eq $ki.count) 'Ki did not place luminaires.'
$kiObjects = Invoke-Tool 'objects_get' @{ items = @($ki.createdObjectIds | ForEach-Object { @{ id = $_; fields = @('MountingHeightMm') } }) }
Assert-That (@($kiObjects.results | Where-Object { $_.properties.MountingHeightMm -ne 3000 }).Count -eq 0) 'Ki mounting height was not preserved.'
$repeat = Invoke-Rpc 'tools/call' @{ name = 'electrica_lighting_place'; arguments = @{ items = @($kiArgs) } }
Assert-That ($repeat.isError -eq $true) 'Ki replaced populated room without explicit permission.'
$afterRepeat = Invoke-Tool 'objects_get' @{ items = @($ki.createdObjectIds | ForEach-Object { @{ id = $_ } }) }
Assert-That ($afterRepeat.results.Count -eq $ki.count) 'Rejected Ki call removed luminaires.'

$kiGroup = Invoke-One 'objects_add_child' @{ id = $board.createdObjectIds[0] }
$kiGroupId = $kiGroup.createdObjectIds[0]
$null = Invoke-One 'ports_connect' @{ portId = $kiGroupId; slaveIds = @($ki.createdObjectIds) }
$routeItems = @($ki.createdObjectIds | ForEach-Object {
    @{ planPath = $plan.planPath; type = $traceType; firstId = $board.createdObjectIds[0]; secondId = $_; height = 3000 }
})
$null = Invoke-Tool 'traces_connect' @{ items = $routeItems }
$route = Invoke-Tool 'electrica_cables_route' @{ ids = @($kiGroupId) }
Assert-That ($route.unroutedCount -eq 0) 'Test luminaires could not be routed.'
$cables = Invoke-Tool 'electrica_cables_inspect' @{ groupId = $kiGroupId; limit = 100 }
Assert-That ($cables.cables.Count -gt 0 -and -not $cables.hasMore) 'Missing or unexpectedly large cable fixture.'

# Берём изделие по свойствам, а не по случайному ID записи демонстрационной базы.
$wireProducts = (Invoke-Tool 'database_search' @{ type = 'ElWire'; limit = 100 }).products
$wireProduct = $null
foreach ($candidate in $wireProducts) {
    $card = Invoke-Tool 'database_get' @{
        type = 'ElWire'; dbId = $candidate.dbId; sourcePath = $candidate.sourcePath
        fields = @('ConductorsNumberL', 'ConductorsNumberPEN')
    }
    if (($card.properties.ConductorsNumberL + $card.properties.ConductorsNumberPEN) -ge 5) { $wireProduct = $candidate; break }
}
Assert-That ([bool]$wireProduct) 'Fixture needs a cable with at least five conductors.'
if (-not $wireProduct.isProject) {
    $wireProduct = Invoke-Tool 'database_import' @{ type = 'ElWire'; dbId = $wireProduct.dbId; sourcePath = $wireProduct.sourcePath }
}
$binding = Invoke-One 'electrica_cables_bind' @{ groupId = $kiGroupId; dbId = $wireProduct.dbId }
Assert-That ($binding.updated -eq $cables.cables.Count) 'Not all group cables were bound.'
$boundCables = Invoke-Tool 'electrica_cables_inspect' @{ groupId = $kiGroupId; limit = 100 }
Assert-That (@($boundCables.cables | Where-Object { $_.dbId -ne $wireProduct.dbId }).Count -eq 0) 'Cable bindings were not persisted.'

$automat = Import-TestProduct 'DbElAutomat'
$device = Invoke-One 'electrica_group_equipment_add' @{ groupId = $kiGroupId; dbType = 'DbElAutomat'; dbId = $automat.dbId }
$children = Invoke-One 'objects_structure' @{ id = $kiGroupId }
Assert-That ($children.objects.id -contains $device.createdObjectIds[0]) 'Added apparatus not attached to group.'

# Проверяем не только DbId, но и штатную перестройку числа клавиш при создании/смене изделия.
$twoKeyProduct = Import-TestProduct 'DbElSwitch' '052'
$oneKeyProduct = Import-TestProduct 'DbElSwitch' '067'
$testSwitch = Invoke-Tool 'objects_create' @{
    planPath = $plan.planPath
    defaults = @{ type = 'ElSwitch'; dbType = 'DbElSwitch'; dbId = $twoKeyProduct.dbId; ugoName = 'EL_SWITCH_02' }
    items = @(@{ position = @(30000,1500) })
}
$switchId = $testSwitch.createdObjectIds[0]
$keys = Invoke-One 'objects_structure' @{ id = $switchId; relation = 'ports' }
Assert-That ($keys.total -eq 2) 'Two-key switch was created without its second section.'
$null = Invoke-One 'objects_update' @{ id = $switchId; dbType = 'DbElSwitch'; dbId = $oneKeyProduct.dbId }
$keys = Invoke-One 'objects_structure' @{ id = $switchId; relation = 'ports' }
Assert-That ($keys.total -eq 1) 'Changing the switch product did not update its sections.'
$null = Invoke-One 'objects_update' @{ id = $switchId; dbType = 'DbElSwitch'; dbId = $twoKeyProduct.dbId }
$keys = Invoke-One 'objects_structure' @{ id = $switchId; relation = 'ports' }
Assert-That ($keys.total -eq 2) 'Changing back to a two-key product did not restore the second section.'

$transformer = Import-TestProduct 'DbElTransformer'
$supply = @{ boardId = $board.createdObjectIds[0]; transformerDbId = $transformer.dbId; cables = @(@{ dbId = $wireProduct.dbId; lengthMeters = 50; count = 1 }) }
$null = Invoke-One 'electrica_supply_update' $supply
$readSupply = Invoke-One 'electrica_supply_get' @{ boardId = $board.createdObjectIds[0] }
Assert-That ($readSupply.transformerDbId -eq $transformer.dbId -and $readSupply.cables[0].lengthMeters -eq 50) 'Supply settings not persisted.'
$badSupply = Invoke-Rpc 'tools/call' @{ name = 'electrica_supply_update'; arguments = @{ items = @(@{ boardId = $board.createdObjectIds[0]; cables = @(@{ dbId = $wireProduct.dbId; lengthMeters = -1 }) }) } }
Assert-That ($badSupply.isError -eq $true) 'Negative supply cable length accepted.'
$unchangedSupply = Invoke-One 'electrica_supply_get' @{ boardId = $board.createdObjectIds[0] }
Assert-That ($unchangedSupply.cables[0].lengthMeters -eq 50) 'Invalid supply update partially replaced the cable list.'
$calc = Invoke-Tool 'electrica_calculate' @{ groupIds = @($kiGroupId); mark = $true; limit = 1 }
Assert-That ($calc.calculated -and $calc.groups.Count -eq 1 -and $calc.groups[0].currentA -gt 0) 'Electrical calculation returned no load.'
$dbName = (Invoke-Tool 'database_get' @{ type = 'DbElLighting'; dbId = $kiProduct.dbId; fields = @('DbName') }).properties.DbName
Assert-That ([bool]$dbName) 'Fixture DbName is not readable.'
$null = Invoke-One 'database_update' @{ type = 'DbElLighting'; dbId = $kiProduct.dbId; properties = @{ DbName = "$dbName MCP" } }
$updatedName = (Invoke-Tool 'database_get' @{ type = 'DbElLighting'; dbId = $kiProduct.dbId; fields = @('DbName') }).properties.DbName
Assert-That ($updatedName -eq "$dbName MCP") 'Project database edit was not saved.'
$null = Invoke-One 'database_update' @{ type = 'DbElLighting'; dbId = $kiProduct.dbId; properties = @{ DbName = $dbName } }
$null = Invoke-Tool 'project_save'
$null = Invoke-Tool 'view_zoom_all'
Write-Output "Electrical operations: OK. Ki=$($ki.count), cable records=$($binding.updated), current=$($calc.groups[0].currentA) A."
