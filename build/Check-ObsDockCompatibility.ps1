[CmdletBinding()]
param([Parameter(Mandatory)][string]$ObsDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ObsDirectory).Path
$exe = Join-Path $root 'bin/64bit/obs64.exe'
$qt = Join-Path $root 'bin/64bit/Qt6Core.dll'
foreach ($file in @($exe, $qt)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Not an OBS x64 installation: $file" }
}
function Get-PeArchitecture([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Invalid PE header.' }
        [void]$stream.Seek(0x3C, [IO.SeekOrigin]::Begin)
        $offset = $reader.ReadInt32()
        if ($offset -lt 0 -or $offset + 6 -gt $stream.Length) { throw 'Invalid PE offset.' }
        [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin)
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid PE signature.' }
        return $reader.ReadUInt16()
    } finally { $stream.Dispose() }
}
$obsVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
$qtVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($qt).ProductVersion
$architecture = Get-PeArchitecture $exe
$compatible = $obsVersion -eq '32.2.2' -and $qtVersion -eq '6.11.1.0' -and $architecture -eq 0x8664
[pscustomobject]@{
    OBS = $obsVersion; Qt = $qtVersion; Architecture = ('0x{0:X4}' -f $architecture)
    VerifiedCombination = $compatible
    DigitalSignature = 'WitherChat EXE/DLL are unsigned'
}
if (-not $compatible) {
    throw 'This OBS/Qt combination is not verified. Do not install this DLL blindly; request a matching build.'
}
Write-Output 'Matches the tested OBS 32.2.2 x64 / Qt 6.11.1 combination. No files or settings changed.'
