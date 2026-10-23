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
    & dotnet build (Join-Path $pluginRoot 'Electrica\MyPlugin.Electrica.csproj') -c $Configuration --nologo -m:1 -nr:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
}

$core = Join-Path $pluginRoot "bin\$Configuration\net6.0-windows"
$electrica = Join-Path $pluginRoot "Electrica\bin\$Configuration\net6.0-windows"
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $core 'MyPlugin.dll')).ProductVersion
$moduleVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $electrica 'MyPlugin.Electrica.dll')).ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $moduleVersion -ne $version) {
    throw "Invalid or mismatched plugin versions: core=$version; module=$moduleVersion"
}

# Новый каталог на каждую упаковку: старые файлы никогда не попадают в новую поставку.
$artifacts = Join-Path $pluginRoot 'artifacts'
$package = Join-Path $artifacts ('MyPlugin-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $version)
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item -LiteralPath (Join-Path $core 'MyPlugin.dll') -Destination $package
Copy-Item -LiteralPath (Join-Path $electrica 'MyPlugin.Electrica.dll') -Destination $package
Copy-Item -LiteralPath (Join-Path $core 'Mcp') -Destination $package -Recurse
Copy-Item -LiteralPath (Join-Path $pluginRoot 'README.md') -Destination $package
$archive = $package + '.zip'
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
Write-Output "PLUGIN=$package"
Write-Output "ZIP=$archive"
