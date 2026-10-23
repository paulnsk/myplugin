param([Parameter(Mandatory = $true)][string]$BuildDirectory)

$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$latest = [IO.Path]::GetFullPath((Join-Path $pluginRoot 'artifacts\LATEST'))
$core = Join-Path $BuildDirectory 'MyPlugin.dll'
$module = Join-Path $BuildDirectory 'MyPlugin.Electrica.dll'
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
if (Test-Path -LiteralPath $latest) { Remove-Item -LiteralPath $latest -Recurse -Force }
New-Item -ItemType Directory -Path $latest | Out-Null
Copy-Item -LiteralPath $core, $module -Destination $latest
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'Mcp') -Destination $latest -Recurse
Copy-Item -LiteralPath (Join-Path $pluginRoot 'README.md') -Destination $latest
Write-Output "LATEST=$latest ($version)"
