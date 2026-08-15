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

    if ($pins.schemaVersion -ne 2) {
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
        [string]::IsNullOrWhiteSpace([string]$pins.windows.ffmpegVersion) -or
        @($pins.windows.packages).Count -eq 0 -or
        @($pins.windows.requiredBinaries).Count -eq 0) {
        throw 'Native dependency manifest has incomplete release metadata.'
    }

    foreach ($package in @($pins.windows.packages)) {
        if ([string]::IsNullOrWhiteSpace([string]$package.name) -or
            [string]::IsNullOrWhiteSpace([string]$package.version)) {
            throw 'The Windows MSYS2 package lock has an incomplete entry.'
        }
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
