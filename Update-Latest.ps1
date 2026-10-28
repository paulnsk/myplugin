param(
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('MyPlugin', 'MyPlugin.Electrica', 'MyPlugin.WaterSupply')][string]$AssemblyName
)

$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$latest = [IO.Path]::GetFullPath((Join-Path $pluginRoot 'artifacts\LATEST'))
$core = Join-Path $BuildDirectory 'MyPlugin.dll'
$module = Join-Path $BuildDirectory ($AssemblyName + '.dll')
foreach ($file in @($core, $module)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing build output: $file" }
}
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($core).ProductVersion
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($module).ProductVersion -ne $version) {
    throw 'Cannot update LATEST: plugin DLL versions differ.'
}

# Только этот каталог сгенерированных файлов; не следуем ссылке за пределы artifacts.
if ($latest -ne (Join-Path $pluginRoot 'artifacts\LATEST')) { throw 'Unexpected LATEST path.' }
foreach ($directory in @((Join-Path $pluginRoot 'artifacts'), $latest)) {
    if (Test-Path -LiteralPath $directory) {
        if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to replace a linked directory: $directory"
        }
    }
}
# Каждый проект обновляет только свой результат. Модули могут собираться параллельно.
New-Item -ItemType Directory -Path $latest -Force | Out-Null
Copy-Item -LiteralPath $module -Destination $latest -Force
if ($AssemblyName -eq 'MyPlugin') {
    Copy-Item -LiteralPath (Join-Path $BuildDirectory 'Mcp') -Destination $latest -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $pluginRoot 'README.md') -Destination $latest -Force
}
Write-Output "LATEST=$latest ($version)"
