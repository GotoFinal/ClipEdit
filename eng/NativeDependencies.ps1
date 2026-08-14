$script:ClipEditNativeDependenciesPath = Join-Path $PSScriptRoot 'native/native-dependencies.json'

function Get-ClipEditNativeDependencies {
    [CmdletBinding()]
    param()

    if (-not (Test-Path -LiteralPath $script:ClipEditNativeDependenciesPath -PathType Leaf)) {
        throw "Native dependency manifest is missing: $script:ClipEditNativeDependenciesPath"
    }

    try {
        $pins = Get-Content -LiteralPath $script:ClipEditNativeDependenciesPath -Raw |
            ConvertFrom-Json
    }
    catch {
        throw "Native dependency manifest is invalid: $($_.Exception.Message)"
    }

    if ($pins.schemaVersion -ne 1) {
        throw "Unsupported native dependency manifest schema: $($pins.schemaVersion)"
    }
    foreach ($componentName in @('ffmpeg', 'mpv', 'mpvBuild', 'libplacebo', 'libass', 'meson')) {
        if ($null -eq $pins.components.$componentName) {
            throw "Native dependency manifest has no '$componentName' component."
        }
    }
    foreach ($componentName in @('ffmpeg', 'mpv', 'mpvBuild', 'libplacebo', 'libass')) {
        $revision = [string]$pins.components.$componentName.revision
        if ($revision -notmatch '^[0-9a-f]{40}$') {
            throw "Native dependency '$componentName' has an invalid Git revision: $revision"
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$pins.components.ffmpeg.version) -or
        [string]::IsNullOrWhiteSpace([string]$pins.windows.stackId) -or
        @($pins.windows.requiredBinaries).Count -eq 0) {
        throw 'Native dependency manifest has incomplete release metadata.'
    }

    return $pins
}

function Get-ClipEditWindowsNativeStackPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkspaceRoot,

        [Parameter(Mandatory = $true)]
        $Dependencies
    )

    return Join-Path $WorkspaceRoot "packages/native/media-stack/win-x64/$($Dependencies.windows.stackId)/runtime"
}

function Assert-ClipEditNativeSourceLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        $Dependencies,

        [string]$SourceLockPath = (Join-Path $PSScriptRoot 'native/windows-shared-media/source-lock.tsv')
    )

    $locked = @{}
    foreach ($line in Get-Content -LiteralPath $SourceLockPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
            continue
        }
        $parts = $line -split "`t"
        if ($parts.Count -ne 2 -or $locked.ContainsKey($parts[0])) {
            throw "Invalid or duplicate native source-lock row: $line"
        }
        $locked[$parts[0]] = $parts[1]
    }

    foreach ($mapping in @(
        @('ffmpeg', 'ffmpeg'),
        @('mpv', 'mpv'))) {
        $lockName = $mapping[0]
        $componentName = $mapping[1]
        $expected = [string]$Dependencies.components.$componentName.revision
        if ($locked[$lockName] -ne $expected) {
            throw "Native source lock '$lockName' is $($locked[$lockName]); expected $expected from native-dependencies.json."
        }
    }
}
