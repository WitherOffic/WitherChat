[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$QtRoot,
    [Parameter(Mandatory)][string]$ObsSource,
    [string]$CMakeExe = 'cmake',
    [string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This prototype requires Windows x64.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactRoot ('obs-dock-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be a new directory within this repository/artifacts.'
}
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory already exists; choose a new path.' }
$qt = (Resolve-Path -LiteralPath $QtRoot).Path
$obs = (Resolve-Path -LiteralPath $ObsSource).Path
if (-not (Test-Path -LiteralPath (Join-Path $obs 'libobs/obs-module.h'))) {
    throw 'ObsSource must contain the official OBS Studio 32.2.2 source tree.'
}
$compiler = Get-Command cl.exe -ErrorAction SilentlyContinue
if ($null -eq $compiler -or $null -eq (Get-Command nmake.exe -ErrorAction SilentlyContinue)) {
    throw 'Run from an x64 Native Tools terminal with MSVC and Windows SDK configured.'
}
if (-not $compiler.Source.Replace('/', '\').Contains('\Hostx64\x64\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use the x64 host/x64 target MSVC toolchain.'
}
$cmake = (Get-Command $CMakeExe -ErrorAction Stop).Source
$native = Join-Path $output 'native'
& $cmake -S (Join-Path $repo 'plugins/obs-witherchat') -B $native -G 'NMake Makefiles' '-DCMAKE_BUILD_TYPE=Release' "-DCMAKE_PREFIX_PATH=$qt" "-DOBS_SOURCE_DIR=$obs" '-DWITHERCHAT_OBS_DOCK_DIAGNOSTICS=OFF'
if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed: $LASTEXITCODE" }
& $cmake --build $native
if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
$binary = Join-Path $native 'witherchat-obs.dll'
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'DLL missing after build.' }
Get-FileHash -LiteralPath $binary -Algorithm SHA256
Write-Output "Built production DLL: $binary"
