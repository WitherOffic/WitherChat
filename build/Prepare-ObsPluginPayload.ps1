[CmdletBinding()]
param([Parameter(Mandatory)][string]$NativeDll, [Parameter(Mandatory)][string]$AppPackage)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dll = (Resolve-Path -LiteralPath $NativeDll).Path
$app = (Resolve-Path -LiteralPath $AppPackage).Path
if ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll)).Contains('native probe completed')) {
    throw 'Diagnostic DLL is not distributable.'
}
$stage = Join-Path $repo ('artifacts/obs-payload-' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $stage 'data/obs-plugins/witherchat-obs'
$bin = Join-Path $stage 'obs-plugins/64bit'
$source = Join-Path $repo ('artifacts/obs-payload-source-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($bin)
[void][IO.Directory]::CreateDirectory($data)
Copy-Item -LiteralPath $dll -Destination (Join-Path $bin 'witherchat-obs.dll')
foreach ($file in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'DOTNET-RUNTIME-LICENSE.txt',
    'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt', 'SKIASHARP-HARFBUZZ-THIRD-PARTY-NOTICES.txt', 'Assets/Fonts/OFL-Inter.txt')) {
    $destination = Join-Path $data $file
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    Copy-Item -LiteralPath (Join-Path $app $file) -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $repo 'plugins/obs-witherchat/LICENSE') -Destination (Join-Path $data 'OBS-PLUGIN-LICENSE.txt')
[void][IO.Directory]::CreateDirectory((Join-Path $source 'plugins'))
[void][IO.Directory]::CreateDirectory((Join-Path $source 'build'))
Copy-Item -LiteralPath (Join-Path $repo 'plugins/obs-witherchat') -Destination (Join-Path $source 'plugins') -Recurse
foreach ($file in @('Build-ObsDock.ps1', 'Package-ObsDock.ps1', 'Check-ObsDockCompatibility.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $source 'build')
}
[IO.Compression.ZipFile]::CreateFromDirectory($source, (Join-Path $data 'plugin-source.zip'))
$asset = Join-Path $repo 'src/WitherChat.Desktop/Assets/ObsPlugin/windows-x64.zip'
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($asset))
$temp = Join-Path $repo ('artifacts/obs-payload-' + [Guid]::NewGuid().ToString('N') + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $temp, [IO.Compression.CompressionLevel]::Optimal, $false)
[IO.File]::Move($temp, $asset, $true)
Get-FileHash -LiteralPath $asset -Algorithm SHA256
