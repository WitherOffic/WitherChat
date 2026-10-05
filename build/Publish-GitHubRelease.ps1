[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+(?:\.\d+)?(?:A)?$')][string]$Version,
    [string]$ObsPluginPackage,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [ValidateSet('WitherOffic/WitherChat')][string]$Repository = 'WitherOffic/WitherChat',
    [string]$OutputDirectory,
    [switch]$PrepareOnly,
    [ValidatePattern('^$|^[0-9a-f]{40}$')][string]$RecoverDraftFromCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$exe = Join-Path $package 'WitherChat.exe'
$required = @('WitherChat.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.md',
    'DOTNET-RUNTIME-LICENSE.txt', 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
    'SKIASHARP-HARFBUZZ-THIRD-PARTY-NOTICES.txt', 'VERSION.txt', 'BUILD_STATUS.txt',
    'Assets/Fonts/OFL-Inter.txt')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $name) -PathType Leaf)) {
        throw "Required release file is missing: $name"
    }
}
if (@(Get-ChildItem -LiteralPath $package -Recurse -File -Force).Count -ne $required.Count) {
    throw 'Unexpected files in the standalone package; refusing to publish.'
}
if ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -ne $Version) {
    throw 'EXE version does not match the release version.'
}
if ((Get-AuthenticodeSignature -LiteralPath $exe).Status -ne 'NotSigned') {
    throw 'This publication path is for explicitly unsigned releases only.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) "WitherChat-release-$SourceCommit"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw 'Release output already exists; refusing to overwrite files.'
}
New-Item -ItemType Directory -Path $output | Out-Null
$releaseExe = Join-Path $output "WitherChat-$Version-win-x64.exe"
$releaseZip = Join-Path $output "WitherChat-$Version-win-x64-portable.zip"
Copy-Item -LiteralPath $exe -Destination $releaseExe
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $releaseZip
$assets = @($releaseExe, $releaseZip)
if (-not [string]::IsNullOrWhiteSpace($ObsPluginPackage)) {
    $obsInput = (Resolve-Path -LiteralPath $ObsPluginPackage).Path
    if ([IO.Path]::GetExtension($obsInput) -ne '.zip') { throw 'OBS plugin package must be a ZIP.' }
    $obsAsset = Join-Path $output "WitherChat-$Version-OBS-Dock-win-x64.zip"
    Copy-Item -LiteralPath $obsInput -Destination $obsAsset
    $assets += $obsAsset
}
$hashes = @($assets | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 })
$checksum = Join-Path $output 'SHA256SUMS.txt'
[IO.File]::WriteAllText($checksum, (($hashes | ForEach-Object {
    "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))"
}) -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
$assets += $checksum
if ($PrepareOnly) {
    $assets | ForEach-Object { Get-Item -LiteralPath $_ } | Select-Object Name, Length
    return
}

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne $Repository -or
    $env:GITHUB_REF -ne 'refs/heads/main' -or $env:GITHUB_SHA -ne $SourceCommit) {
    throw 'Release publication is restricted to the matching GitHub-hosted main build.'
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GitHub workflow token is missing.' }
$headers = @{
    Authorization = "Bearer $env:GH_TOKEN"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}
function Get-GitHubResource([string]$Path) {
    try {
        $response = Invoke-RestMethod "https://api.github.com/repos/$Repository/$Path" -Headers $headers
        return $response
    }
    catch {
        if ($null -ne $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $null }
        throw
    }
}
function Get-GitHubRelease([string]$Tag) {
    $publishedRelease = Get-GitHubResource "releases/tags/$Tag"
    if ($null -ne $publishedRelease) { return $publishedRelease }
    # Authenticated release listing also includes unpublished drafts.
    $matchingReleases = @(foreach ($releaseItem in (Get-GitHubResource 'releases?per_page=100')) {
        if ($null -ne $releaseItem -and $releaseItem.tag_name -eq $Tag) { $releaseItem }
    })
    if ($matchingReleases.Count -gt 1) { throw 'Multiple releases use the requested tag.' }
    if ($matchingReleases.Count -eq 1) { return $matchingReleases[0] }
    return $null
}
$tag = "v$Version"
$existing = Get-GitHubRelease $tag
if ($null -ne $existing -and $existing.draft -and $RecoverDraftFromCommit) {
    # One-time recovery of our failed, never-published 0.5.1A draft.
    # No published release or pre-existing user draft may be deleted.
    $expectedNames = @($assets | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
    $actualNames = @($existing.assets.name | Sort-Object)
    if ($Version -ne '0.5.1A' -or
        $RecoverDraftFromCommit -ne 'df6412d379f902fa75b950430f247dd95b3e722d' -or
        $existing.target_commitish -ne $RecoverDraftFromCommit -or
        $existing.author.login -ne 'github-actions[bot]' -or
        $existing.body -notlike "*$RecoverDraftFromCommit*" -or
        $existing.body -notlike '*actions/runs/37106501637*' -or
        ($expectedNames -join '|') -ne ($actualNames -join '|') -or
        [string]$existing.id -notmatch '^[1-9][0-9]*$') {
        throw 'Draft does not match our known failed publication; nothing was deleted.'
    }
    Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$Repository/releases/$($existing.id)" -Headers $headers | Out-Null
    Write-Output "Removed only our failed unpublished draft: $($existing.id). Original workflow artifacts remain available."
    $existing = $null
}
if ($null -ne $existing) {
    if ($existing.draft) { throw 'An unfinished draft exists; inspect it before retrying.' }
    foreach ($path in $assets) {
        if ([IO.Path]::GetFileName($path) -notin @($existing.assets.name)) {
            throw 'Existing release is incomplete; refusing to overwrite it.'
        }
    }
    Write-Output "Release already published; no files changed: $($existing.html_url)"
    return
}
$existingTag = Get-GitHubResource "git/ref/tags/$tag"
if ($null -ne $existingTag) {
    $tagObject = $existingTag.object
    while ($tagObject.type -eq 'tag') {
        $tagObject = (Get-GitHubResource "git/tags/$($tagObject.sha)").object
    }
    if ($tagObject.type -ne 'commit' -or $tagObject.sha -ne $SourceCommit) {
        throw 'Existing version tag points to a different source commit.'
    }
}
$notesPath = Join-Path $PSScriptRoot "../docs/releases/$Version.md"
$notes = [IO.File]::ReadAllText([IO.Path]::GetFullPath($notesPath))
$runUrl = "https://github.com/$Repository/actions/runs/$env:GITHUB_RUN_ID"
$provenance = "Source: [$SourceCommit](https://github.com/$Repository/commit/$SourceCommit) · [GitHub build]($runUrl)"
$notes = $notes.Replace('<!-- BUILD_PROVENANCE -->', $provenance)
$generatedNotes = Join-Path $output 'release-notes.md'
[IO.File]::WriteAllText($generatedNotes, $notes, [Text.UTF8Encoding]::new($false))
$ghArgs = @('release', 'create', $tag) + $assets + @('--repo', $Repository,
    '--target', $SourceCommit, '--draft', '--latest=false',
    '--title', "WitherChat $Version - Windows x64", '--notes-file', $generatedNotes)
$isPrerelease = $Version.EndsWith('A', [StringComparison]::Ordinal)
if ($isPrerelease) { $ghArgs += '--prerelease' }
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw 'GitHub draft creation or asset upload failed.' }
$draft = Get-GitHubRelease $tag
if ($null -eq $draft -or -not $draft.draft -or $draft.prerelease -ne $isPrerelease -or
    $draft.target_commitish -ne $SourceCommit -or $draft.tag_name -ne $tag) {
    throw 'Unexpected draft metadata; release has not been published.'
}
if (@($draft.assets).Count -ne $assets.Count) { throw 'Release asset count mismatch.' }
foreach ($path in $assets) {
    $asset = @($draft.assets | Where-Object name -eq ([IO.Path]::GetFileName($path)))
    $localFile = Get-Item -LiteralPath $path
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
    if ($asset.Count -ne 1 -or $asset[0].size -ne $localFile.Length -or
        $asset[0].digest -ne $digest -or $asset[0].state -ne 'uploaded') {
        throw 'Uploaded asset verification failed; draft has not been published.'
    }
}
$publishArgs = @('release', 'edit', $tag, '--repo', $Repository, '--draft=false')
$publishArgs += if ($isPrerelease) { @('--prerelease', '--latest=false') } else { @('--prerelease=false', '--latest=true') }
& gh @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publishing the verified draft failed.' }
$published = Get-GitHubResource "releases/tags/$tag"
if ($null -eq $published -or $published.draft -or $published.tag_name -ne $tag -or
    $published.target_commitish -ne $SourceCommit) { throw 'Published release verification failed.' }
Write-Output "Published verified unsigned release: $($published.html_url)"
