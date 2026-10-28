param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Release,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$pluginRoot = $PSScriptRoot
if ($Release -and $NoBuild) { throw 'A new release must be built.' }
if ($Release) {
    # Подготовка версии релиза; публикация на GitHub выполняется отдельно.
    $versionPath = Join-Path $pluginRoot 'Version.props'
    [xml]$state = Get-Content -LiteralPath $versionPath -Raw -Encoding UTF8
    $state.Project.PropertyGroup.PluginMinor = [string]([int]$state.Project.PropertyGroup.PluginMinor + 1)
    $state.Project.PropertyGroup.PluginBuild = '0'
    $state.Save($versionPath)
}
if (-not $NoBuild) {
    & dotnet build (Join-Path $pluginRoot 'MyPlugin.sln') -c $Configuration --nologo -m:1 -nr:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
}

$core = Join-Path $pluginRoot "bin\$Configuration\net6.0-windows"
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $core 'MyPlugin.dll')).ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid plugin version: $version" }
$modules = foreach ($name in @('Electrica', 'WaterSupply')) {
    $file = Join-Path $pluginRoot "$name\bin\$Configuration\net6.0-windows\MyPlugin.$name.dll"
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($file).ProductVersion -ne $version) {
        throw "Plugin module version differs: $file"
    }
    $file
}

# Новый каталог на каждую упаковку: старые файлы никогда не попадают в новую поставку.
$artifacts = Join-Path $pluginRoot 'artifacts'
$package = Join-Path $artifacts ('MyPlugin-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $version)
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item -LiteralPath (Join-Path $core 'MyPlugin.dll') -Destination $package
Copy-Item -LiteralPath $modules -Destination $package
Copy-Item -LiteralPath (Join-Path $core 'Mcp') -Destination $package -Recurse
Copy-Item -LiteralPath (Join-Path $pluginRoot 'README.md') -Destination $package
$archive = $package + '.zip'
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
Write-Output "PLUGIN=$package"
Write-Output "ZIP=$archive"
