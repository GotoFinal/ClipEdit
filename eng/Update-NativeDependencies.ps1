[CmdletBinding()]
param(
    [switch]$Apply,

    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')

function Get-RemoteTags([string]$Repository) {
    $lines = @(& git ls-remote --tags $Repository)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read tags from $Repository."
    }

    $tags = @{}
    foreach ($line in $lines) {
        if ($line -notmatch '^([0-9a-f]{40})\s+refs/tags/(.+)$') {
            continue
        }
        $revision = $Matches[1]
        $name = $Matches[2]
        if ($name.EndsWith('^{}', [StringComparison]::Ordinal)) {
            $tags[$name.Substring(0, $name.Length - 3)] = $revision
        }
        elseif (-not $tags.ContainsKey($name)) {
            $tags[$name] = $revision
        }
    }
    return $tags
}

function Get-LatestStableTag([string]$Repository, [string]$RequiredPrefix) {
    $tags = Get-RemoteTags $Repository
    $candidates = foreach ($entry in $tags.GetEnumerator()) {
        if (-not [string]::IsNullOrEmpty($RequiredPrefix) -and
            -not $entry.Key.StartsWith($RequiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $normalized = if ([string]::IsNullOrEmpty($RequiredPrefix)) {
            $entry.Key.TrimStart('v', 'V')
        }
        else {
            $entry.Key.Substring($RequiredPrefix.Length)
        }
        if ($normalized -notmatch '^\d+\.\d+(?:\.\d+){0,2}$') {
            continue
        }
        $parsed = $null
        if ([Version]::TryParse($normalized, [ref]$parsed)) {
            [pscustomobject]@{
                Version = $parsed
                VersionText = $normalized
                Tag = $entry.Key
                Revision = $entry.Value
            }
        }
    }
    $latest = $candidates | Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $latest) {
        throw "Could not resolve a stable release tag from $Repository."
    }
    return $latest
}

function Get-RemoteHead([string]$Repository) {
    $lines = @(& git ls-remote $Repository HEAD)
    $gitExitCode = $LASTEXITCODE
    [string]$line = if ($lines.Count -gt 0) { $lines[0] } else { '' }
    $parts = @($line -split '\s+')
    if ($gitExitCode -ne 0 -or $parts.Count -ne 2 -or
        $parts[0] -notmatch '^[0-9a-f]{40}$' -or $parts[1] -ne 'HEAD') {
        throw "Could not resolve the default branch of $Repository."
    }
    return $parts[0]
}

function Set-SourceLockRevision([string]$Name, [string]$Revision) {
    $sourceLockPath = Join-Path $PSScriptRoot 'native/windows-shared-media/source-lock.tsv'
    $lines = @(Get-Content -LiteralPath $sourceLockPath)
    $found = $false
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match "^$([regex]::Escape($Name))`t") {
            $lines[$index] = "$Name`t$Revision"
            $found = $true
            break
        }
    }
    if (-not $found) {
        throw "Windows source lock has no $Name row."
    }
    [IO.File]::WriteAllLines(
        $sourceLockPath,
        $lines,
        (New-Object Text.UTF8Encoding($false)))
}

function Add-Result(
    [Collections.Generic.List[object]]$Results,
    [string]$Name,
    [string]$Current,
    [string]$Latest,
    [string]$CurrentRevision,
    [string]$LatestRevision,
    [bool]$CanApply) {
    $Results.Add([pscustomobject]@{
        Name = $Name
        Current = $Current
        Latest = $Latest
        CurrentRevision = $CurrentRevision
        LatestRevision = $LatestRevision
        UpdateAvailable = $CurrentRevision -ne $LatestRevision
        CanApply = $CanApply
    })
}

$pins = Get-ClipEditNativeDependencies
Assert-ClipEditNativeSourceLock -Dependencies $pins
$results = [Collections.Generic.List[object]]::new()

$tagComponents = @(
    @('ffmpeg', 'FFmpeg', 'n'),
    @('mpv', 'mpv', 'v'),
    @('libass', 'libass', '')
)
foreach ($definition in $tagComponents) {
    $key = $definition[0]
    $displayName = $definition[1]
    $prefix = $definition[2]
    $component = $pins.components.$key
    $latest = Get-LatestStableTag ([string]$component.repository) $prefix
    Add-Result $results $displayName ([string]$component.version) $latest.VersionText `
        ([string]$component.revision) $latest.Revision $true
    if ($Apply -and [string]$component.revision -ne $latest.Revision) {
        $component.version = $latest.VersionText
        $component.tag = $latest.Tag
        $component.revision = $latest.Revision
        if ($key -in @('ffmpeg', 'mpv')) {
            Set-SourceLockRevision $key $latest.Revision
        }
    }
}

foreach ($definition in @(
    @('mpvBuild', 'mpv-build'),
    @('libplacebo', 'libplacebo'))) {
    $key = $definition[0]
    $displayName = $definition[1]
    $component = $pins.components.$key
    $latestRevision = Get-RemoteHead ([string]$component.repository)
    Add-Result $results $displayName ([string]$component.version) "git-$($latestRevision.Substring(0, 7))" `
        ([string]$component.revision) $latestRevision $true
    if ($Apply -and [string]$component.revision -ne $latestRevision) {
        $component.version = "git-$($latestRevision.Substring(0, 7))"
        $component.tag = $null
        $component.revision = $latestRevision
    }
}

$toolchainRevision = Get-RemoteHead ([string]$pins.windows.toolchainRepository)
Add-Result $results 'mpv-winbuild-cmake' "git-$(([string]$pins.windows.toolchainRevision).Substring(0, 7))" `
    "git-$($toolchainRevision.Substring(0, 7))" ([string]$pins.windows.toolchainRevision) `
    $toolchainRevision $false

$latestMesonTag = Get-LatestStableTag ([string]$pins.components.meson.repository) ''
$latestMeson = $latestMesonTag.VersionText
Add-Result $results 'Meson' ([string]$pins.components.meson.version) $latestMeson `
    ([string]$pins.components.meson.version) $latestMeson $true
if ($Apply -and [string]$pins.components.meson.version -ne $latestMeson) {
    $pins.components.meson.version = $latestMeson
}

if ($Apply) {
    $ffmpegVersion = [string]$pins.components.ffmpeg.version
    $mpvShortRevision = ([string]$pins.components.mpv.revision).Substring(0, 7)
    $pins.windows.stackId = "mpv-$mpvShortRevision-ffmpeg-$ffmpegVersion-shared"
    $pins.releaseProfiles.'win-x64' = "ffmpeg-$ffmpegVersion-shared+libmpv-shared-libav-v1"
    $pins.releaseProfiles.'linux-x64' = "ffmpeg-$ffmpegVersion-source-built+libmpv"
    $json = $pins | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText(
        $script:ClipEditNativeDependenciesPath,
        $json + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
    & (Join-Path $PSScriptRoot 'Test-NativeDependencies.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Updated native dependency pins are inconsistent.'
    }
}

$report = [Collections.Generic.List[string]]::new()
$report.Add('# ClipEdit native dependency review')
$report.Add('')
$report.Add('| Dependency | Current | Upstream | Status | Automation |')
$report.Add('|---|---:|---:|---|---|')
foreach ($result in $results) {
    $status = if ($result.UpdateAvailable) { 'Update available' } else { 'Current' }
    $automation = if ($result.CanApply) { 'PR pin update' } else { 'Manual recipe review' }
    $report.Add("| $($result.Name) | ``$($result.Current)`` | ``$($result.Latest)`` | $status | $automation |")
}
$manualResults = @($results | Where-Object { $_.UpdateAvailable -and -not $_.CanApply })
if ($manualResults.Count -gt 0) {
    $report.Add('')
    $report.Add('The Windows toolchain revision is reported but not changed automatically because it can add or remove transitive source-lock entries and invalidate the reviewed patch.')
}
$reportText = $report -join [Environment]::NewLine
Write-Output $reportText
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $fullReportPath = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $fullReportPath)) | Out-Null
    [IO.File]::WriteAllText(
        $fullReportPath,
        $reportText + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
}
