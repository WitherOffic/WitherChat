[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativeDll,
    [Parameter(Mandatory)][string]$AppPackage,
    [string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifacts ('obs-dock-package-' + [Guid]::NewGuid().ToString('N'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output + '.zip'))) {
    throw 'Choose a new OutputDirectory within repository/artifacts.'
}
$dll = (Resolve-Path -LiteralPath $NativeDll).Path
$app = (Resolve-Path -LiteralPath $AppPackage).Path
$required = @('WitherChat.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.md',
    'DOTNET-RUNTIME-LICENSE.txt', 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
    'SKIASHARP-HARFBUZZ-THIRD-PARTY-NOTICES.txt', 'Assets/Fonts/OFL-Inter.txt')
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $file) -PathType Leaf)) {
        throw "AppPackage is incomplete: $file"
    }
}
$dllBytes = [IO.File]::ReadAllBytes($dll)
if ([Text.Encoding]::ASCII.GetString($dllBytes).Contains('native probe completed')) {
    throw 'A diagnostic DLL must not be packaged for users.'
}
$bin = Join-Path $output 'obs-plugins/64bit'
$data = Join-Path $output 'data/obs-plugins/witherchat-obs'
$source = Join-Path $output 'plugin-source'
[void][IO.Directory]::CreateDirectory($bin)
[void][IO.Directory]::CreateDirectory($data)
Copy-Item -LiteralPath $dll -Destination (Join-Path $bin 'witherchat-obs.dll')
# Use the exact payload embedded in the chat so status checks also recognize manual installation.
$payload = [IO.Compression.ZipFile]::OpenRead((Join-Path $repo 'src/WitherChat.Desktop/Assets/ObsPlugin/windows-x64.zip'))
try {
    $entry = $payload.GetEntry('obs-plugins/64bit/witherchat-obs.dll')
    $memory = [IO.MemoryStream]::new()
    $input = $entry.Open()
    try { $input.CopyTo($memory) } finally { $input.Dispose() }
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($memory.ToArray())) -ne
        (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash) {
        throw 'Native DLL differs from embedded installer. Run Prepare-ObsPluginPayload.ps1 first.'
    }
    $memory.Dispose()
    foreach ($item in $payload.Entries) {
        if (-not $item.FullName.StartsWith('data/obs-plugins/witherchat-obs/', [StringComparison]::Ordinal)) { continue }
        $destination = Join-Path $output $item.FullName
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($item, $destination, $false)
    }
} finally { $payload.Dispose() }
foreach ($file in $required) {
    $destination = Join-Path $data $file
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    Copy-Item -LiteralPath (Join-Path $app $file) -Destination $destination -Force
}
[void][IO.Directory]::CreateDirectory((Join-Path $source 'plugins'))
[void][IO.Directory]::CreateDirectory((Join-Path $source 'build'))
Copy-Item -LiteralPath (Join-Path $repo 'plugins/obs-witherchat') -Destination (Join-Path $source 'plugins') -Recurse
foreach ($file in @('Build-ObsDock.ps1', 'Package-ObsDock.ps1', 'Check-ObsDockCompatibility.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $source 'build')
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Check-ObsDockCompatibility.ps1') -Destination (Join-Path $output 'Check-ObsDockCompatibility.ps1')
Copy-Item -LiteralPath (Join-Path $repo 'plugins/obs-witherchat/README.md') -Destination (Join-Path $output 'README.md')
Copy-Item -LiteralPath (Join-Path $repo 'plugins/obs-witherchat/LICENSE') -Destination (Join-Path $output 'OBS-PLUGIN-LICENSE.txt')
[IO.Compression.ZipFile]::CreateFromDirectory($output, $output + '.zip', [IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash -LiteralPath ($output + '.zip') -Algorithm SHA256
Write-Output "Created experimental package: $output.zip"
