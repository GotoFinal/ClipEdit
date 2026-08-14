[CmdletBinding()]
param(
    [string]$OutputPath,

    [ValidateRange(1, 32)]
    [int]$Jobs = 8,

    [string]$CacheVolume = 'clipedit-win-shared-build',

    [string]$CachePath,

    [switch]$RebuildBuilderImage
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$recipePath = Join-Path $PSScriptRoot 'native/windows-shared-media'
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')
$nativeDependencies = Get-ClipEditNativeDependencies
Assert-ClipEditNativeSourceLock -Dependencies $nativeDependencies
$builderImage = [string]$nativeDependencies.windows.builderImage
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Get-ClipEditWindowsNativeStackPath `
        -WorkspaceRoot $workspaceRoot `
        -Dependencies $nativeDependencies
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "The native stack output path already exists: $fullOutputPath"
}

& docker version --format '{{.Server.Version}}' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'A running Docker engine is required to build the Windows shared media stack.'
}

$imageExists = $false
if (-not $RebuildBuilderImage) {
    & docker image inspect $builderImage *> $null
    $imageExists = $LASTEXITCODE -eq 0
}
if (-not $imageExists) {
    & docker build `
        --file (Join-Path $recipePath 'Dockerfile') `
        --tag $builderImage `
        $PSScriptRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Docker failed to build the pinned native builder image (exit $LASTEXITCODE)."
    }
}

$cacheMount = if ([string]::IsNullOrWhiteSpace($CachePath)) {
    "type=volume,source=$CacheVolume,target=/cache"
}
else {
    $fullCachePath = [System.IO.Path]::GetFullPath($CachePath)
    [System.IO.Directory]::CreateDirectory($fullCachePath) | Out-Null
    "type=bind,source=$fullCachePath,target=/cache"
}

$stagingPath = "$fullOutputPath.staging-$([Guid]::NewGuid().ToString('N'))"
try {
    [System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null
    & docker run --rm `
        --mount $cacheMount `
        --mount "type=bind,source=$stagingPath,target=/output" `
        --env "CLIPEDIT_NATIVE_JOBS=$Jobs" `
        $builderImage
    if ($LASTEXITCODE -ne 0) {
        throw "The Windows shared media stack build failed with exit code $LASTEXITCODE."
    }

    $binPath = Join-Path $stagingPath 'bin'
    $requiredNames = @($nativeDependencies.windows.requiredBinaries)
    foreach ($name in $requiredNames) {
        $requiredPath = Join-Path $binPath $name
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "The source build did not produce $requiredPath."
        }
    }

    $ffmpegPath = Join-Path $binPath 'ffmpeg.exe'
    $ffprobePath = Join-Path $binPath 'ffprobe.exe'
    $windowsExecutor = $null
    $hostIsWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
    if (-not $hostIsWindows) {
        $windowsExecutor = Get-Command wine64 -ErrorAction SilentlyContinue
        if ($null -eq $windowsExecutor) {
            $windowsExecutor = Get-Command wine -ErrorAction SilentlyContinue
        }
        if ($null -eq $windowsExecutor) {
            throw 'wine64 or wine is required to validate the Windows native stack on a non-Windows host.'
        }
    }
    function Invoke-NativeWindowsTool([string]$Executable, [string[]]$Arguments) {
        if ($null -eq $windowsExecutor) {
            & $Executable @Arguments
        }
        else {
            & $windowsExecutor.Source $Executable @Arguments
        }
    }

    $ffmpegVersion = [string]$nativeDependencies.components.ffmpeg.version
    $versionText = (Invoke-NativeWindowsTool $ffmpegPath @('-version') | Select-Object -First 1)
    if ($versionText -notmatch "ffmpeg version n?$([regex]::Escape($ffmpegVersion))") {
        throw "The source-built FFmpeg did not report version ${ffmpegVersion}: $versionText"
    }
    $probeVersionText = (Invoke-NativeWindowsTool $ffprobePath @('-version') | Select-Object -First 1)
    if ($probeVersionText -notmatch "ffprobe version n?$([regex]::Escape($ffmpegVersion))") {
        throw "The source-built ffprobe did not report version ${ffmpegVersion}: $probeVersionText"
    }

    $encoders = (Invoke-NativeWindowsTool $ffmpegPath @('-hide_banner', '-encoders') 2>&1 | Out-String)
    foreach ($encoder in @('libx264', 'libvpx-vp9', 'aac', 'libopus')) {
        if ($encoders -notmatch "\b$([regex]::Escape($encoder))\b") {
            throw "The source-built FFmpeg does not expose the required $encoder encoder."
        }
    }
    $filters = (Invoke-NativeWindowsTool $ffmpegPath @('-hide_banner', '-filters') 2>&1 | Out-String)
    foreach ($filter in @('crop', 'scale', 'zscale', 'tonemap', 'format', 'setparams', 'rotate', 'overlay', 'concat', 'atrim', 'asetpts', 'aeval', 'volume', 'amix', 'alimiter', 'apad', 'showwavespic')) {
        if ($filters -notmatch "\b$([regex]::Escape($filter))\b") {
            throw "The source-built FFmpeg does not expose the required $filter filter."
        }
    }

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fullOutputPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $fullOutputPath)
    Write-Host "ClipEdit Windows shared media stack is ready at $fullOutputPath"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
}
