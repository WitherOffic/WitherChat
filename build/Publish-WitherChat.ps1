[CmdletBinding()]
param(
    [ValidateSet('all', 'windows', 'linux', 'macos')]
    [string]$Platform = 'all',

    [ValidatePattern('^\d+\.\d+\.\d+(?:A)?$')]
    [string]$Version = '0.5.1A',

    [ValidateSet('all', 'x64', 'arm64')]
    [string]$WindowsArchitecture = 'all',

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$runningOnWindows = $env:OS -eq 'Windows_NT'
$numericVersionText = $Version -replace 'A$', ''
$numericVersion = [version]$numericVersionText
$packageVersion = if ($Version.EndsWith('A', [StringComparison]::Ordinal))
{
    "$numericVersionText-alpha"
}
else
{
    $numericVersionText
}
$runtimeFrameworkVersion = '8.0.30'

$directorySeparator = [IO.Path]::DirectorySeparatorChar
$repositoryRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + $directorySeparator
$project = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src/WitherChat.Desktop/WitherChat.Desktop.csproj'))
$artifactRoot = ([IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/WitherChat'))).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + $directorySeparator
$versionFile = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src/WitherChat.Core/AppVersion.cs'))
$iconFile = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src/WitherChat.Desktop/Assets/WitherChat.ico'))
$applicationLicenseFile = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'LICENSE'))
$thirdPartyNoticesFile = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md'))
$projectAssetsFile = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src/WitherChat.Desktop/obj/project.assets.json'))
$preparedPlatforms = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Assert-WithinRepository([string]$Path)
{
    $fullPath = [IO.Path]::GetFullPath($Path)
    $comparison = if ($runningOnWindows)
    {
        [StringComparison]::OrdinalIgnoreCase
    }
    else
    {
        [StringComparison]::Ordinal
    }
    if (-not $fullPath.StartsWith($repositoryRoot, $comparison))
    {
        throw "Path escaped the repository: $fullPath"
    }

    return $fullPath
}

function Get-IcoPngEntries([string]$Path)
{
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or
        [BitConverter]::ToUInt16($bytes, 2) -ne 1)
    {
        throw "Invalid WitherChat ICO file: $Path"
    }

    $count = [BitConverter]::ToUInt16($bytes, 4)
    $entries = New-Object System.Collections.Generic.List[object]
    for ($index = 0; $index -lt $count; $index++)
    {
        $entryOffset = 6 + ($index * 16)
        if ($entryOffset + 16 -gt $bytes.Length)
        {
            throw "Truncated WitherChat ICO directory: $Path"
        }

        $width = if ($bytes[$entryOffset] -eq 0) { 256 } else { [int]$bytes[$entryOffset] }
        $height = if ($bytes[$entryOffset + 1] -eq 0) { 256 } else { [int]$bytes[$entryOffset + 1] }
        $length = [BitConverter]::ToUInt32($bytes, $entryOffset + 8)
        $imageOffset = [BitConverter]::ToUInt32($bytes, $entryOffset + 12)
        if ($length -eq 0 -or $imageOffset + $length -gt $bytes.Length)
        {
            throw "Invalid WitherChat ICO image entry: $Path"
        }

        $data = [byte[]]::new($length)
        [Array]::Copy($bytes, $imageOffset, $data, 0, $length)
        $isPng = $data.Length -ge 8 -and
            $data[0] -eq 0x89 -and $data[1] -eq 0x50 -and $data[2] -eq 0x4E -and $data[3] -eq 0x47
        if ($isPng -and $width -eq $height)
        {
            $entries.Add([pscustomobject]@{ Size = $width; Data = $data })
        }
    }

    return $entries
}

function Write-BigEndianUInt32([IO.BinaryWriter]$Writer, [uint32]$Value)
{
    $Writer.Write([byte[]]@(
        (($Value -shr 24) -band 0xFF),
        (($Value -shr 16) -band 0xFF),
        (($Value -shr 8) -band 0xFF),
        ($Value -band 0xFF)))
}

function Write-MacIcon([string]$Destination)
{
    $types = @{ 16 = 'icp4'; 32 = 'icp5'; 64 = 'icp6'; 128 = 'ic07'; 256 = 'ic08' }
    $entries = @(Get-IcoPngEntries $iconFile | Where-Object { $types.ContainsKey($_.Size) })
    if ($entries.Count -eq 0)
    {
        throw 'WitherChat.ico does not contain PNG images suitable for a macOS icon.'
    }

    [uint32]$totalLength = 8
    foreach ($entry in $entries)
    {
        $totalLength += 8 + $entry.Data.Length
    }

    $stream = [IO.File]::Create($Destination)
    try
    {
        $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::ASCII, $true)
        try
        {
            $writer.Write([Text.Encoding]::ASCII.GetBytes('icns'))
            Write-BigEndianUInt32 $writer $totalLength
            foreach ($entry in $entries)
            {
                $writer.Write([Text.Encoding]::ASCII.GetBytes($types[$entry.Size]))
                Write-BigEndianUInt32 $writer ([uint32](8 + $entry.Data.Length))
                $writer.Write($entry.Data)
            }
        }
        finally
        {
            $writer.Dispose()
        }
    }
    finally
    {
        $stream.Dispose()
    }
}

function Write-LinuxCompanionFiles([string]$Output, [string]$RuntimeIdentifier)
{
    $largest = Get-IcoPngEntries $iconFile | Sort-Object Size -Descending | Select-Object -First 1
    if ($null -eq $largest)
    {
        throw 'WitherChat.ico does not contain a PNG image for Linux.'
    }

    [IO.File]::WriteAllBytes((Join-Path $Output 'WitherChat.png'), $largest.Data)
    $installScript = @'
#!/bin/sh
set -eu

version='__VERSION__'
package_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
if [ -n "${XDG_DATA_HOME:-}" ]; then
    data_home=$XDG_DATA_HOME
else
    : "${HOME:?HOME is not set and XDG_DATA_HOME was not provided.}"
    data_home=$HOME/.local/share
fi
case "$data_home" in
    /*) ;;
    *) printf '%s\n' 'XDG_DATA_HOME must be an absolute path.' >&2; exit 1 ;;
esac
install_root=$data_home/witherchat
version_root=$install_root/versions/$version
staging=$install_root/.install-$version-$$
rollback=$install_root/.rollback-$version
applications_dir=$data_home/applications
icon_dir=$data_home/icons/hicolor/256x256/apps
desktop_file=$applications_dir/witherchat.desktop
desktop_tmp=
had_previous=0
new_version_installed=0
staging_created=0
committed=0

case "$staging" in
    "$install_root"/.install-"$version"-*) ;;
    *) printf '%s\n' 'Refusing to use an unsafe installation path.' >&2; exit 1 ;;
esac

cleanup()
{
    status=$?
    trap - EXIT HUP INT TERM
    if [ "$staging_created" -eq 1 ] && [ -d "$staging" ]; then
        rm -rf -- "$staging"
    fi
    if [ -n "$desktop_tmp" ]; then
        rm -f -- "$desktop_tmp"
    fi

    if [ "$committed" -eq 0 ] && [ "$new_version_installed" -eq 1 ]; then
        rm -rf -- "$version_root"
        if [ "$had_previous" -eq 1 ] && [ -d "$rollback" ]; then
            mv -- "$rollback" "$version_root" || true
        fi
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

if [ -L "$install_root" ]; then
    printf '%s\n' 'Refusing to install through a symbolic-link install root.' >&2
    exit 1
fi
mkdir -p -- "$install_root" "$applications_dir" "$icon_dir"
mkdir -- "$staging"
staging_created=1
cp -a -- "$package_dir"/. "$staging"/
chmod 755 -- "$staging/WitherChat" "$staging/install.sh" "$staging/uninstall.sh"

if [ -d "$version_root" ]; then
    rm -rf -- "$rollback"
    mv -- "$version_root" "$rollback"
    had_previous=1
fi
if ! mv -- "$staging" "$version_root"; then
    if [ "$had_previous" -eq 1 ] && [ -d "$rollback" ]; then
        mv -- "$rollback" "$version_root"
    fi
    printf '%s\n' 'WitherChat installation failed; the previous copy was restored.' >&2
    exit 1
fi
new_version_installed=1

install -m 0644 -- "$version_root/WitherChat.png" "$icon_dir/witherchat.png"

desktop_escape()
{
    # In an Exec value, percent introduces a desktop-entry field code even
    # inside quotes. Doubling it keeps a literal percent in an install path.
    printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g; s/`/\\`/g; s/\$/\\$/g; s/%/%%/g'
}
exec_path=$(desktop_escape "$version_root/WitherChat")
working_path=$(desktop_escape "$version_root")
desktop_tmp=$(mktemp "$applications_dir/.witherchat.desktop.XXXXXX")
{
    printf '%s\n' '[Desktop Entry]'
    printf '%s\n' 'Version=1.0'
    printf '%s\n' 'Type=Application'
    printf '%s\n' 'Name=WitherChat'
    printf '%s\n' 'Comment=Twitch, YouTube and DonationAlerts streaming chat'
    printf 'Exec="%s"\n' "$exec_path"
    printf 'Path="%s"\n' "$working_path"
    printf '%s\n' 'Icon=witherchat'
    printf '%s\n' 'Terminal=false'
    printf '%s\n' 'Categories=Network;Chat;'
    printf '%s\n' 'StartupNotify=true'
    printf '%s\n' 'X-WitherChat-Version=__VERSION__'
} > "$desktop_tmp"
chmod 0644 -- "$desktop_tmp"
mv -- "$desktop_tmp" "$desktop_file"
committed=1
rm -rf -- "$rollback"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$applications_dir" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q "$data_home/icons/hicolor" >/dev/null 2>&1 || true
fi

printf 'WitherChat %s installed. Open it from the application menu.\n' "$version"
'@.Replace('__VERSION__', $Version)
    $uninstallScript = @'
#!/bin/sh
set -eu

version='__VERSION__'
if [ -n "${XDG_DATA_HOME:-}" ]; then
    data_home=$XDG_DATA_HOME
else
    : "${HOME:?HOME is not set and XDG_DATA_HOME was not provided.}"
    data_home=$HOME/.local/share
fi
case "$data_home" in
    /*) ;;
    *) printf '%s\n' 'XDG_DATA_HOME must be an absolute path.' >&2; exit 1 ;;
esac
install_root=$data_home/witherchat
version_root=$install_root/versions/$version
rollback=$install_root/.rollback-$version
desktop_file=$data_home/applications/witherchat.desktop
icon_file=$data_home/icons/hicolor/256x256/apps/witherchat.png

case "$version_root" in
    "$install_root"/versions/"$version") ;;
    *) printf '%s\n' 'Refusing to use an unsafe uninstall path.' >&2; exit 1 ;;
esac
if [ -L "$install_root" ]; then
    printf '%s\n' 'Refusing to uninstall through a symbolic-link install root.' >&2
    exit 1
fi

if [ -f "$desktop_file" ] && grep -Fqx "X-WitherChat-Version=$version" "$desktop_file"; then
    rm -f -- "$desktop_file"
fi
rm -rf -- "$version_root" "$rollback"
rmdir -- "$install_root/versions" "$install_root" 2>/dev/null || true
if [ ! -d "$install_root" ]; then
    rm -f -- "$icon_file"
fi

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$data_home/applications" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q "$data_home/icons/hicolor" >/dev/null 2>&1 || true
fi

printf 'WitherChat %s was removed.\n' "$version"
'@.Replace('__VERSION__', $Version)
    $installPath = Join-Path $Output 'install.sh'
    $uninstallPath = Join-Path $Output 'uninstall.sh'
    [IO.File]::WriteAllText(
        $installPath,
        $installScript,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $uninstallPath,
        $uninstallScript,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $Output 'README-LINUX.txt'),
        "WitherChat $Version for $RuntimeIdentifier`n`nPortable launch:`n  ./WitherChat`n`nInstall for the current user (application-menu shortcut and icon):`n  ./install.sh`n`nRemove this installed version:`n  ./uninstall.sh`n`nThe installer uses XDG_DATA_HOME (or ~/.local/share), keeps versions isolated and does not require root.`n`nПортативный запуск:`n  ./WitherChat`n`nУстановка ярлыка и иконки для текущего пользователя:`n  ./install.sh`n`nУдаление установленной версии:`n  ./uninstall.sh`n",
        [Text.UTF8Encoding]::new($false))
    if (-not $runningOnWindows)
    {
        $executableMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor `
            [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::GroupRead -bor `
            [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor `
            [IO.UnixFileMode]::OtherExecute
        [IO.File]::SetUnixFileMode($installPath, $executableMode)
        [IO.File]::SetUnixFileMode($uninstallPath, $executableMode)
    }

    Assert-LinuxCompanionFiles $Output
}

function Assert-LinuxCompanionFiles([string]$Output)
{
    foreach ($requiredName in @('WitherChat', 'WitherChat.png', 'install.sh', 'uninstall.sh', 'README-LINUX.txt'))
    {
        if (-not (Test-Path -LiteralPath (Join-Path $Output $requiredName) -PathType Leaf))
        {
            throw "Linux package is missing $requiredName."
        }
    }

    if (Test-Path -LiteralPath (Join-Path $Output 'WitherChat.desktop'))
    {
        throw 'Linux package contains a non-portable .desktop launcher. Use install.sh to create it.'
    }

    $installScript = [IO.File]::ReadAllText((Join-Path $Output 'install.sh'))
    $uninstallScript = [IO.File]::ReadAllText((Join-Path $Output 'uninstall.sh'))
    if (-not $installScript.Contains('Exec="%s"', [StringComparison]::Ordinal) -or
        -not $installScript.Contains('Icon=witherchat', [StringComparison]::Ordinal) -or
        -not $installScript.Contains("X-WitherChat-Version=$Version", [StringComparison]::Ordinal) -or
        -not $installScript.Contains('XDG_DATA_HOME must be an absolute path.', [StringComparison]::Ordinal) -or
        -not $installScript.Contains('mkdir -- "$staging"', [StringComparison]::Ordinal) -or
        -not $installScript.Contains('mktemp "$applications_dir/.witherchat.desktop.XXXXXX"', [StringComparison]::Ordinal))
    {
        throw 'Linux installer does not create a versioned desktop launcher with a real executable and icon.'
    }
    if (-not $uninstallScript.Contains("X-WitherChat-Version=`$version", [StringComparison]::Ordinal))
    {
        throw 'Linux uninstaller does not protect a newer desktop launcher.'
    }

    if (-not $runningOnWindows -and [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [Runtime.InteropServices.OSPlatform]::Linux))
    {
        $installPath = Join-Path $Output 'install.sh'
        $uninstallPath = Join-Path $Output 'uninstall.sh'
        foreach ($scriptPath in @($installPath, $uninstallPath))
        {
            & /bin/sh -n $scriptPath
            if ($LASTEXITCODE -ne 0)
            {
                throw "Linux shell syntax validation failed: $scriptPath"
            }
        }

        $testDataHome = Join-Path ([IO.Path]::GetTempPath()) (
            'witherchat-installer-test-' + [Guid]::NewGuid().ToString('N'))
        $hadXdgDataHome = Test-Path Env:XDG_DATA_HOME
        $previousXdgDataHome = $env:XDG_DATA_HOME
        New-Item -ItemType Directory -Path $testDataHome | Out-Null
        try
        {
            $env:XDG_DATA_HOME = $testDataHome
            & /bin/sh $installPath
            if ($LASTEXITCODE -ne 0)
            {
                throw "Linux installer smoke test failed: $installPath"
            }

            foreach ($expectedPath in @(
                (Join-Path $testDataHome "witherchat/versions/$Version/WitherChat"),
                (Join-Path $testDataHome 'applications/witherchat.desktop'),
                (Join-Path $testDataHome 'icons/hicolor/256x256/apps/witherchat.png')))
            {
                if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf))
                {
                    throw "Linux installer smoke test is missing: $expectedPath"
                }
            }

            & /bin/sh $uninstallPath
            if ($LASTEXITCODE -ne 0)
            {
                throw "Linux uninstaller smoke test failed: $uninstallPath"
            }
            if (Test-Path -LiteralPath (Join-Path $testDataHome "witherchat/versions/$Version"))
            {
                throw 'Linux uninstaller smoke test left the installed application behind.'
            }
            if (Test-Path -LiteralPath (Join-Path $testDataHome 'applications/witherchat.desktop'))
            {
                throw 'Linux uninstaller smoke test left the desktop launcher behind.'
            }
        }
        finally
        {
            if ($hadXdgDataHome)
            {
                $env:XDG_DATA_HOME = $previousXdgDataHome
            }
            else
            {
                Remove-Item Env:XDG_DATA_HOME -ErrorAction SilentlyContinue
            }
            Remove-Item -LiteralPath $testDataHome -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function New-WindowsZipPackage([string]$VersionRoot, [string]$RuntimeIdentifier)
{
    $source = Assert-WithinRepository (Join-Path $VersionRoot $RuntimeIdentifier)
    $archive = Assert-WithinRepository (Join-Path $VersionRoot "WitherChat-$Version-$RuntimeIdentifier.zip")
    if (Test-Path -LiteralPath $archive)
    {
        Remove-Item -LiteralPath $archive -Force
    }

    Compress-Archive -Path (Join-Path $source '*') -DestinationPath $archive -CompressionLevel Optimal
    Assert-WindowsZipArchive $archive
}

function New-WindowsSingleFileZipPackage([string]$VersionRoot, [string]$RuntimeIdentifier)
{
    $source = Assert-WithinRepository (Join-Path $VersionRoot "$RuntimeIdentifier-single-file")
    $archive = Assert-WithinRepository (
        Join-Path $VersionRoot "WitherChat-$Version-$RuntimeIdentifier-single-file.zip")
    if (Test-Path -LiteralPath $archive)
    {
        Remove-Item -LiteralPath $archive -Force
    }

    # The ZIP is the distributable single-file package: the EXE remains one
    # binary, while mandatory font and third-party notices travel beside it.
    Compress-Archive -Path (Join-Path $source '*') -DestinationPath $archive -CompressionLevel Optimal
    Assert-WindowsZipArchive $archive
}

function Assert-WindowsZipArchive([string]$Archive)
{
    $requiredNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($requiredName in @(
        'WitherChat.exe',
        'LICENSE',
        'VERSION.txt',
        'BUILD_STATUS.txt',
        'THIRD-PARTY-NOTICES.md',
        'DOTNET-RUNTIME-LICENSE.txt',
        'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
        'SKIASHARP-HARFBUZZ-THIRD-PARTY-NOTICES.txt'))
    {
        [void]$requiredNames.Add($requiredName)
    }

    $seenNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $archiveHandle = [IO.Compression.ZipFile]::OpenRead($Archive)
    try
    {
        foreach ($entry in $archiveHandle.Entries)
        {
            $entryName = $entry.FullName
            if ($entryName.Contains('\', [StringComparison]::Ordinal))
            {
                throw "Windows ZIP contains a non-portable path separator: $entryName"
            }
            if ([string]::IsNullOrWhiteSpace($entryName) -or
                [IO.Path]::IsPathRooted($entryName) -or
                $entryName -match '^[A-Za-z]:' -or
                $entryName.Split('/') -contains '..')
            {
                throw "Windows ZIP contains an unsafe path: $entryName"
            }
            if (-not $seenNames.Add($entryName))
            {
                throw "Windows ZIP contains a duplicate entry: $entryName"
            }

            [void]$requiredNames.Remove($entryName)
            if ($entryName.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase))
            {
                throw "Windows ZIP contains debug symbols: $entryName"
            }
        }
    }
    finally
    {
        $archiveHandle.Dispose()
    }

    if ($seenNames.Count -eq 0)
    {
        throw "Windows ZIP is empty: $Archive"
    }
    if ($requiredNames.Count -gt 0)
    {
        throw "Windows ZIP is missing required files: $($requiredNames -join ', ')"
    }
}

function New-UnixTarPackage(
    [string]$VersionRoot,
    [string]$RuntimeIdentifier,
    [string[]]$ExecutableRelativePaths)
{
    $runtimeRoot = Assert-WithinRepository (Join-Path $VersionRoot $RuntimeIdentifier)
    $archive = Assert-WithinRepository (
        Join-Path $VersionRoot "WitherChat-$Version-$RuntimeIdentifier.tar.gz")
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue

    $executablePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($executableRelativePath in $ExecutableRelativePaths)
    {
        [void]$executablePaths.Add($executableRelativePath.Replace('\', '/').TrimStart('/'))
    }
    if (Test-Path -LiteralPath (Join-Path $runtimeRoot 'createdump') -PathType Leaf)
    {
        [void]$executablePaths.Add('createdump')
    }

    $archiveStream = [IO.File]::Create($archive)
    try
    {
        $gzipStream = [IO.Compression.GZipStream]::new(
            $archiveStream,
            [IO.Compression.CompressionLevel]::Optimal,
            $true)
        try
        {
            $writer = [System.Formats.Tar.TarWriter]::new(
                $gzipStream,
                [System.Formats.Tar.TarEntryFormat]::Pax,
                $true)
            try
            {
                foreach ($item in (Get-ChildItem -LiteralPath $runtimeRoot -Force -Recurse |
                    Sort-Object { [IO.Path]::GetRelativePath($runtimeRoot, $_.FullName) }))
                {
                    $relativePath = [IO.Path]::GetRelativePath($runtimeRoot, $item.FullName).Replace('\', '/')
                    if ($relativePath.StartsWith('../', [StringComparison]::Ordinal) -or
                        [IO.Path]::IsPathRooted($relativePath))
                    {
                        throw "Archive item escaped its runtime root: $($item.FullName)"
                    }

                    $isLink = ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
                    $entryType = if ($isLink)
                    {
                        [System.Formats.Tar.TarEntryType]::SymbolicLink
                    }
                    elseif ($item.PSIsContainer)
                    {
                        [System.Formats.Tar.TarEntryType]::Directory
                    }
                    else
                    {
                        [System.Formats.Tar.TarEntryType]::RegularFile
                    }
                    $entry = [System.Formats.Tar.PaxTarEntry]::new($entryType, $relativePath)
                    $entry.Uid = 0
                    $entry.Gid = 0
                    $entry.UserName = 'root'
                    $entry.GroupName = 'root'
                    $entry.ModificationTime = [DateTimeOffset]::FromUnixTimeSeconds(946684800)
                    $entry.Mode = if ($item.PSIsContainer -or $executablePaths.Contains($relativePath))
                    {
                        [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor `
                            [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::GroupRead -bor `
                            [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor `
                            [IO.UnixFileMode]::OtherExecute
                    }
                    else
                    {
                        [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor `
                            [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::OtherRead
                    }

                    $dataStream = $null
                    try
                    {
                        if ($isLink)
                        {
                            $linkTarget = [string]$item.LinkTarget
                            if ([string]::IsNullOrWhiteSpace($linkTarget))
                            {
                                throw "Published package contains an unreadable symbolic link: $relativePath"
                            }
                            $linkTarget = $linkTarget.Replace('\', '/')
                            if ([IO.Path]::IsPathRooted($linkTarget) -or
                                $linkTarget.Split('/') -contains '..')
                            {
                                throw "Published package contains a link outside its bundle: $relativePath -> $linkTarget"
                            }
                            $entry.LinkName = $linkTarget
                        }
                        elseif (-not $item.PSIsContainer)
                        {
                            $dataStream = [IO.File]::OpenRead($item.FullName)
                            $entry.DataStream = $dataStream
                        }
                        $writer.WriteEntry($entry)
                    }
                    finally
                    {
                        if ($null -ne $dataStream)
                        {
                            $dataStream.Dispose()
                        }
                    }
                }
            }
            finally
            {
                $writer.Dispose()
            }
        }
        finally
        {
            $gzipStream.Dispose()
        }
    }
    finally
    {
        $archiveStream.Dispose()
    }

    if (-not (Test-Path -LiteralPath $archive))
    {
        throw "Expected release archive was not created: $archive"
    }

    Assert-UnixArchive $archive $executablePaths
}

function Assert-UnixArchive(
    [string]$Archive,
    [Collections.Generic.HashSet[string]]$ExecutablePaths)
{
    $seenNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenExecutables = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $archiveStream = [IO.File]::OpenRead($Archive)
    try
    {
        $gzipStream = [IO.Compression.GZipStream]::new(
            $archiveStream,
            [IO.Compression.CompressionMode]::Decompress,
            $true)
        try
        {
            $reader = [System.Formats.Tar.TarReader]::new($gzipStream, $true)
            try
            {
                while ($null -ne ($entry = $reader.GetNextEntry()))
                {
                    if ($entry.Name.Contains('\', [StringComparison]::Ordinal))
                    {
                        throw "Unix archive contains a Windows path separator: $($entry.Name)"
                    }
                    $entryName = $entry.Name
                    if ([IO.Path]::IsPathRooted($entryName) -or
                        $entryName -match '^[A-Za-z]:' -or
                        $entryName.Split('/') -contains '..')
                    {
                        throw "Unix archive contains an unsafe path: $($entry.Name)"
                    }
                    if (-not $seenNames.Add($entryName))
                    {
                        throw "Unix archive contains a duplicate entry: $entryName"
                    }
                    if ($ExecutablePaths.Contains($entryName))
                    {
                        $requiredMode = [IO.UnixFileMode]::UserExecute -bor `
                            [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherExecute
                        if (($entry.Mode -band $requiredMode) -ne $requiredMode)
                        {
                            throw "Unix archive executable has an invalid mode: $entryName ($($entry.Mode))"
                        }
                        [void]$seenExecutables.Add($entryName)
                    }
                }
            }
            finally
            {
                $reader.Dispose()
            }
        }
        finally
        {
            $gzipStream.Dispose()
        }
    }
    finally
    {
        $archiveStream.Dispose()
    }

    foreach ($executablePath in $ExecutablePaths)
    {
        if (-not $seenExecutables.Contains($executablePath))
        {
            throw "Unix archive is missing executable entry: $executablePath"
        }
    }
}

function Get-KnownVersions([string]$PlatformRoot)
{
    $versions = New-Object System.Collections.Generic.List[version]
    foreach ($containerName in @('current', 'history'))
    {
        $container = Join-Path $PlatformRoot $containerName
        if (-not (Test-Path -LiteralPath $container))
        {
            continue
        }

        foreach ($directory in (Get-ChildItem -LiteralPath $container -Directory))
        {
            $parsed = $null
            $versionName = $directory.Name -replace 'A$', ''
            if ([version]::TryParse($versionName, [ref]$parsed))
            {
                $versions.Add($parsed)
            }
        }
    }

    return $versions
}

function Prepare-VersionDirectory([string]$PlatformName)
{
    $platformRoot = Assert-WithinRepository (Join-Path $artifactRoot $PlatformName)
    $currentRoot = Assert-WithinRepository (Join-Path $platformRoot 'current')
    $historyRoot = Assert-WithinRepository (Join-Path $platformRoot 'history')
    New-Item -ItemType Directory -Path $currentRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $historyRoot -Force | Out-Null

    $requestedVersion = $numericVersion
    $knownVersions = @(Get-KnownVersions $platformRoot)
    if ($knownVersions.Count -gt 0)
    {
        $latest = $knownVersions | Sort-Object -Descending | Select-Object -First 1
        if ($requestedVersion -lt $latest)
        {
            throw "The $PlatformName version cannot move backwards from $latest to $requestedVersion."
        }
    }

    foreach ($oldCurrent in (Get-ChildItem -LiteralPath $currentRoot -Directory))
    {
        if ($oldCurrent.Name -eq $Version)
        {
            continue
        }

        Write-Warning "Existing package $($oldCurrent.FullName) was preserved."
    }

    $versionRoot = Assert-WithinRepository (Join-Path $currentRoot $Version)
    if ($preparedPlatforms.Add($PlatformName) -and (Test-Path -LiteralPath $versionRoot))
    {
        # A publish of the same version is a replacement, not an incremental
        # merge. Removing only this exact generated version directory prevents
        # stale runtimes and archives from leaking into a release artifact.
        Remove-Item -LiteralPath $versionRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $versionRoot 'VERSION.txt'), $Version + [Environment]::NewLine)
    [IO.File]::WriteAllText(
        (Join-Path $versionRoot 'BUILD_STATUS.txt'),
        "WitherChat $Version development build.$([Environment]::NewLine)Not a signed public release.$([Environment]::NewLine)")
    return $versionRoot
}

function Copy-ReleaseMetadata([string]$Output, [string]$RuntimeIdentifier)
{
    if (-not (Test-Path -LiteralPath $thirdPartyNoticesFile -PathType Leaf))
    {
        throw "Third-party notices are missing: $thirdPartyNoticesFile"
    }

    if (-not (Test-Path -LiteralPath $applicationLicenseFile -PathType Leaf))
    {
        throw "Application license is missing: $applicationLicenseFile"
    }
    Copy-Item -LiteralPath $applicationLicenseFile -Destination (Join-Path $Output 'LICENSE') -Force
    Copy-Item -LiteralPath $thirdPartyNoticesFile -Destination (Join-Path $Output 'THIRD-PARTY-NOTICES.md') -Force
    Copy-DotNetRuntimeNotices $Output $RuntimeIdentifier
    [IO.File]::WriteAllText(
        (Join-Path $Output 'VERSION.txt'),
        $Version + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $Output 'BUILD_STATUS.txt'),
        "WitherChat $Version development build.$([Environment]::NewLine)Not a signed public release.$([Environment]::NewLine)",
        [Text.UTF8Encoding]::new($false))
}

function Copy-DotNetRuntimeNotices([string]$Output, [string]$RuntimeIdentifier)
{
    if (-not (Test-Path -LiteralPath $projectAssetsFile -PathType Leaf))
    {
        throw "NuGet assets are missing; restore before packaging: $projectAssetsFile"
    }

    $assets = Get-Content -LiteralPath $projectAssetsFile -Raw | ConvertFrom-Json -AsHashtable
    $packageRelativePath = "microsoft.netcore.app.runtime.$RuntimeIdentifier/$runtimeFrameworkVersion"
    $runtimePackageRoot = $null
    foreach ($packageFolder in $assets.packageFolders.Keys)
    {
        $candidate = Join-Path $packageFolder $packageRelativePath
        if (Test-Path -LiteralPath $candidate -PathType Container)
        {
            $runtimePackageRoot = $candidate
            break
        }
    }
    if ($null -eq $runtimePackageRoot)
    {
        throw "The .NET runtime pack $packageRelativePath is missing. Restore with RuntimeFrameworkVersion=$runtimeFrameworkVersion."
    }

    $runtimeLicense = Join-Path $runtimePackageRoot 'LICENSE.TXT'
    $runtimeNotices = Join-Path $runtimePackageRoot 'THIRD-PARTY-NOTICES.TXT'
    foreach ($requiredFile in @($runtimeLicense, $runtimeNotices))
    {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf))
        {
            throw "The .NET runtime package is missing a required notice: $requiredFile"
        }
    }

    Copy-Item -LiteralPath $runtimeLicense `
        -Destination (Join-Path $Output 'DOTNET-RUNTIME-LICENSE.txt') -Force
    Copy-Item -LiteralPath $runtimeNotices `
        -Destination (Join-Path $Output 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt') -Force

    $nativeAssetsPlatform = switch -Wildcard ($RuntimeIdentifier)
    {
        'win-*' { 'win32' }
        'linux-*' { 'linux' }
        'osx-*' { 'macos' }
        default { throw "Unsupported SkiaSharp notice platform: $RuntimeIdentifier" }
    }
    $skiaLibraryPrefix = "SkiaSharp.NativeAssets.$nativeAssetsPlatform/"
    $skiaLibrary = $assets.libraries.Keys | Where-Object {
        $_.StartsWith($skiaLibraryPrefix, [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($skiaLibrary))
    {
        throw "The restored assets do not contain $skiaLibraryPrefix."
    }
    $skiaPackageRelativePath = $skiaLibrary.ToLowerInvariant()
    $skiaPackageRoot = $null
    foreach ($packageFolder in $assets.packageFolders.Keys)
    {
        $candidate = Join-Path $packageFolder $skiaPackageRelativePath
        if (Test-Path -LiteralPath $candidate -PathType Container)
        {
            $skiaPackageRoot = $candidate
            break
        }
    }
    $skiaNotices = if ($null -ne $skiaPackageRoot)
    {
        Join-Path $skiaPackageRoot 'THIRD-PARTY-NOTICES.txt'
    }
    else
    {
        $null
    }
    if ($null -eq $skiaNotices -or -not (Test-Path -LiteralPath $skiaNotices -PathType Leaf))
    {
        throw "SkiaSharp native third-party notices are missing for $RuntimeIdentifier."
    }
    Copy-Item -LiteralPath $skiaNotices `
        -Destination (Join-Path $Output 'SKIASHARP-HARFBUZZ-THIRD-PARTY-NOTICES.txt') -Force
}

function Assert-CleanPublishOutput(
    [string]$Output,
    [string]$ExecutableName,
    [string]$ScanRoot = $Output)
{
    foreach ($requiredName in @(
        $ExecutableName,
        'LICENSE',
        'VERSION.txt',
        'BUILD_STATUS.txt',
        'THIRD-PARTY-NOTICES.md',
        'DOTNET-RUNTIME-LICENSE.txt',
        'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
        'SKIASHARP-HARFBUZZ-THIRD-PARTY-NOTICES.txt'))
    {
        if (-not (Test-Path -LiteralPath (Join-Path $Output $requiredName) -PathType Leaf))
        {
            throw "Published package is missing $requiredName in $Output."
        }
    }

    $forbiddenNames = @(
        'settings.json',
        'settings.json.bak',
        'token.dat',
        'twitch-session.dat',
        'youtube-session.dat',
        'donationalerts-session.dat',
        'donationalerts-widget.dat',
        'moderation-cache.json',
        'stream-moments.json',
        'token.logout',
        'secret.key',
        'diagnostics.log',
        '.env')
    $forbiddenFiles = @(Get-ChildItem -LiteralPath $ScanRoot -File -Recurse | Where-Object {
        $_.Extension -in @('.pdb', '.log') -or
        $_.Name -in $forbiddenNames -or
        $_.Name -like '*.corrupt-*' -or
        $_.Name -like '.env.*' -or
        $_.Name.EndsWith('.logged-out', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($forbiddenFiles.Count -gt 0)
    {
        $relativeNames = $forbiddenFiles | ForEach-Object {
            [IO.Path]::GetRelativePath($ScanRoot, $_.FullName)
        }
        throw "Published package contains forbidden development or user-data files: $($relativeNames -join ', ')"
    }

    $forbiddenDirectories = @(Get-ChildItem -LiteralPath $ScanRoot -Directory -Recurse | Where-Object {
        $_.Name -in @('chat_logs', 'logs')
    })
    if ($forbiddenDirectories.Count -gt 0)
    {
        $relativeNames = $forbiddenDirectories | ForEach-Object {
            [IO.Path]::GetRelativePath($ScanRoot, $_.FullName)
        }
        throw "Published package contains user-data directories: $($relativeNames -join ', ')"
    }
}

function Assert-FontLicenseIncluded([string]$Output)
{
    if (-not (Get-ChildItem -LiteralPath $Output -Filter 'OFL-Inter.txt' -File -Recurse | Select-Object -First 1))
    {
        throw "Published package does not contain the Inter font license in $Output."
    }
}

function Publish-Runtime([string]$PlatformName, [string]$RuntimeIdentifier)
{
    $versionRoot = Prepare-VersionDirectory $PlatformName
    if ($PlatformName -eq 'macos')
    {
        $runtimeRoot = Assert-WithinRepository (Join-Path $versionRoot $RuntimeIdentifier)
        if (Test-Path -LiteralPath $runtimeRoot)
        {
            Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
        }
        $appContents = Assert-WithinRepository (Join-Path $runtimeRoot 'WitherChat.app/Contents')
        $output = Assert-WithinRepository (Join-Path $appContents 'MacOS')
        $resources = Assert-WithinRepository (Join-Path $appContents 'Resources')
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        New-Item -ItemType Directory -Path $resources -Force | Out-Null
    }
    else
    {
        $output = Assert-WithinRepository (Join-Path $versionRoot $RuntimeIdentifier)
        if (Test-Path -LiteralPath $output)
        {
            Remove-Item -LiteralPath $output -Recurse -Force
        }
        New-Item -ItemType Directory -Path $output -Force | Out-Null
    }

    $arguments = @(
        'publish',
        $project,
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $output,
        '-p:PublishTrimmed=false',
        '-p:PublishSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:RuntimeFrameworkVersion=$runtimeFrameworkVersion",
        "-p:Version=$packageVersion",
        "-p:FileVersion=$numericVersionText.0",
        "-p:AssemblyVersion=$numericVersionText.0",
        "-p:InformationalVersion=$Version"
    )
    if ($NoRestore)
    {
        $arguments += '--no-restore'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet publish failed for $RuntimeIdentifier with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File -Recurse | Remove-Item -Force

    if ($PlatformName -eq 'macos')
    {
        Copy-ReleaseMetadata $resources $RuntimeIdentifier
        Write-MacIcon (Join-Path $resources 'WitherChat.icns')
        $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDisplayName</key><string>WitherChat</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>WitherChat</string>
  <key>CFBundleIdentifier</key><string>chat.wither.desktop</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>WitherChat</string>
  <key>CFBundleIconFile</key><string>WitherChat.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$numericVersionText</string>
  <key>CFBundleVersion</key><string>$numericVersionText</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.social-networking</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@
        [IO.File]::WriteAllText((Join-Path $appContents 'Info.plist'), $plist, [Text.UTF8Encoding]::new($false))
        if (-not $runningOnWindows -and [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
                [Runtime.InteropServices.OSPlatform]::OSX))
        {
            $appBundle = Assert-WithinRepository (Join-Path $runtimeRoot 'WitherChat.app')
            & /usr/bin/plutil -lint (Join-Path $appContents 'Info.plist')
            if ($LASTEXITCODE -ne 0)
            {
                throw "Info.plist validation failed for $RuntimeIdentifier with exit code $LASTEXITCODE."
            }
            & /usr/bin/codesign --force --deep --sign - --timestamp=none $appBundle
            if ($LASTEXITCODE -ne 0)
            {
                throw "Ad-hoc codesign failed for $RuntimeIdentifier with exit code $LASTEXITCODE."
            }
            & /usr/bin/codesign --verify --deep --strict --verbose=2 $appBundle
            if ($LASTEXITCODE -ne 0)
            {
                throw "Code signature verification failed for $RuntimeIdentifier with exit code $LASTEXITCODE."
            }
        }

        Assert-CleanPublishOutput $resources 'WitherChat.icns' $appContents
        Assert-FontLicenseIncluded $appContents
    }
    elseif ($PlatformName -eq 'linux')
    {
        Copy-ReleaseMetadata $output $RuntimeIdentifier
        Write-LinuxCompanionFiles $output $RuntimeIdentifier
        Assert-CleanPublishOutput $output 'WitherChat'
        Assert-FontLicenseIncluded $output
    }
    else
    {
        Copy-ReleaseMetadata $output $RuntimeIdentifier
        Assert-CleanPublishOutput $output 'WitherChat.exe'
        Assert-FontLicenseIncluded $output
    }
}

function Publish-WindowsSingleFile([string]$RuntimeIdentifier)
{
    $versionRoot = Prepare-VersionDirectory 'windows'
    $output = Assert-WithinRepository (Join-Path $versionRoot "$RuntimeIdentifier-single-file")
    if (Test-Path -LiteralPath $output)
    {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null

    $arguments = @(
        'publish',
        $project,
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $output,
        '-p:PublishTrimmed=false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:RuntimeFrameworkVersion=$runtimeFrameworkVersion",
        "-p:Version=$packageVersion",
        "-p:FileVersion=$numericVersionText.0",
        "-p:AssemblyVersion=$numericVersionText.0",
        "-p:InformationalVersion=$Version"
    )
    if ($NoRestore)
    {
        $arguments += '--no-restore'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Single-file dotnet publish failed for $RuntimeIdentifier with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File -Recurse | Remove-Item -Force
    Copy-ReleaseMetadata $output $RuntimeIdentifier
    $executable = Join-Path $output 'WitherChat.exe'
    if (-not (Test-Path -LiteralPath $executable))
    {
        throw "The Windows single-file executable was not created: $executable"
    }
    Assert-CleanPublishOutput $output 'WitherChat.exe'
    Assert-FontLicenseIncluded $output
}

$declaredVersion = Select-String -LiteralPath $versionFile -Pattern 'public const string Current = "([^"]+)";' |
    Select-Object -First 1
if ($null -eq $declaredVersion -or $declaredVersion.Matches[0].Groups[1].Value -ne $Version)
{
    throw "AppVersion.Current must match requested version $Version."
}

$windowsRuntimes = switch ($WindowsArchitecture)
{
    'x64' { @('win-x64') }
    'arm64' { @('win-arm64') }
    default { @('win-x64', 'win-arm64') }
}

$targets = @(
    foreach ($runtime in $windowsRuntimes)
    {
        [pscustomobject]@{ Platform = 'windows'; Runtime = $runtime }
    }
    [pscustomobject]@{ Platform = 'linux'; Runtime = 'linux-x64' },
    [pscustomobject]@{ Platform = 'linux'; Runtime = 'linux-arm64' },
    [pscustomobject]@{ Platform = 'macos'; Runtime = 'osx-x64' },
    [pscustomobject]@{ Platform = 'macos'; Runtime = 'osx-arm64' }
)

foreach ($target in $targets)
{
    if ($Platform -eq 'all' -or $Platform -eq $target.Platform)
    {
        Publish-Runtime $target.Platform $target.Runtime
    }
}

if ($Platform -in @('all', 'windows'))
{
    foreach ($runtime in $windowsRuntimes)
    {
        Publish-WindowsSingleFile $runtime
    }
}

foreach ($platformName in @('windows', 'linux', 'macos'))
{
    if ($Platform -ne 'all' -and $Platform -ne $platformName)
    {
        continue
    }

    $versionRoot = Assert-WithinRepository (Join-Path $artifactRoot "$platformName/current/$Version")
    switch ($platformName)
    {
        'windows'
        {
            $packageLinks = @(
                foreach ($runtime in $windowsRuntimes)
                {
                    New-WindowsZipPackage $versionRoot $runtime
                    New-WindowsSingleFileZipPackage $versionRoot $runtime
                    $architectureLabel = if ($runtime -eq 'win-x64') { 'x64' } else { 'ARM64' }
                    [pscustomobject]@{
                        Label = "Windows $architectureLabel - ZIP"
                        Path = "WitherChat-$Version-$runtime.zip"
                    }
                    [pscustomobject]@{
                        Label = "Windows $architectureLabel - single-file ZIP"
                        Path = "WitherChat-$Version-$runtime-single-file.zip"
                    }
                    [pscustomobject]@{
                        Label = "Windows $architectureLabel - standalone EXE (notices are in the adjacent folder)"
                        Path = "$runtime-single-file/WitherChat.exe"
                    }
                }
            )
        }
        'linux'
        {
            New-UnixTarPackage -VersionRoot $versionRoot -RuntimeIdentifier 'linux-x64' `
                -ExecutableRelativePaths @('WitherChat', 'install.sh', 'uninstall.sh')
            New-UnixTarPackage -VersionRoot $versionRoot -RuntimeIdentifier 'linux-arm64' `
                -ExecutableRelativePaths @('WitherChat', 'install.sh', 'uninstall.sh')
            $packageLinks = @(
                [pscustomobject]@{
                    Label = 'Linux x64 - tar.gz'
                    Path = "WitherChat-$Version-linux-x64.tar.gz"
                },
                [pscustomobject]@{
                    Label = 'Linux ARM64 - tar.gz'
                    Path = "WitherChat-$Version-linux-arm64.tar.gz"
                }
            )
        }
        'macos'
        {
            New-UnixTarPackage $versionRoot 'osx-x64' 'WitherChat.app/Contents/MacOS/WitherChat'
            New-UnixTarPackage $versionRoot 'osx-arm64' 'WitherChat.app/Contents/MacOS/WitherChat'
            $packageLinks = @(
                [pscustomobject]@{
                    Label = 'macOS Intel x64 - app tar.gz'
                    Path = "WitherChat-$Version-osx-x64.tar.gz"
                },
                [pscustomobject]@{
                    Label = 'macOS Apple Silicon - app tar.gz'
                    Path = "WitherChat-$Version-osx-arm64.tar.gz"
                }
            )
        }
    }

    $platformDisplayName = switch ($platformName)
    {
        'windows' { 'Windows' }
        'linux' { 'Linux' }
        'macos' { 'macOS' }
    }
    $packageLinksFile = Join-Path $versionRoot 'PACKAGE_LINKS.md'
    $packageLinkLines = [System.Collections.Generic.List[string]]::new()
    $packageLinkLines.Add("# WitherChat $Version - $platformDisplayName")
    $packageLinkLines.Add('')
    foreach ($packageLink in $packageLinks)
    {
        $packagePath = Join-Path $versionRoot $packageLink.Path
        if (-not (Test-Path -LiteralPath $packagePath))
        {
            throw "Expected package was not created: $packagePath"
        }

        $markdownPath = $packageLink.Path.Replace('\', '/')
        $packageLinkLines.Add("- [$($packageLink.Label)](./$markdownPath)")
    }
    $packageLinkLines.Add("- [SHA256 checksums](./SHA256SUMS.txt)")
    [IO.File]::WriteAllLines(
        $packageLinksFile,
        $packageLinkLines,
        [Text.UTF8Encoding]::new($false))

    $checksumFile = Join-Path $versionRoot 'SHA256SUMS.txt'
    $checksumTargets = [System.Collections.Generic.List[string]]::new()
    foreach ($metadataName in @('VERSION.txt', 'BUILD_STATUS.txt', 'PACKAGE_LINKS.md'))
    {
        $checksumTargets.Add((Join-Path $versionRoot $metadataName))
    }
    foreach ($packageLink in $packageLinks)
    {
        $checksumTargets.Add((Join-Path $versionRoot $packageLink.Path))
    }
    $lines = $checksumTargets |
        Sort-Object -Unique |
        ForEach-Object {
            $fullPath = [IO.Path]::GetFullPath($_)
            $relativePath = ($fullPath.Substring($versionRoot.Length)).TrimStart(
                [char[]]@('\', '/')).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relativePath"
        }
    [IO.File]::WriteAllLines($checksumFile, $lines, [Text.UTF8Encoding]::new($false))

    Write-Output ''
    Write-Output ('=' * 72)
    Write-Output "WitherChat $Version | $platformDisplayName"
    Write-Output ('=' * 72)
    foreach ($packageLink in $packageLinks)
    {
        Write-Output ("{0}: {1}" -f $packageLink.Label, (Join-Path $versionRoot $packageLink.Path))
    }
    Write-Output "Checksums: $checksumFile"
    Write-Output "Clickable index: $packageLinksFile"
}

Write-Output ''
Write-Output ('#' * 72)
Write-Output "WitherChat $Version packaging completed"
Write-Output "Artifact root: $artifactRoot"
Write-Output ('#' * 72)
