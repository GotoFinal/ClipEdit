[CmdletBinding()]
param(
    [string]$OutputPath,

    [ValidateRange(1, 32)]
    [int]$Jobs = 8,

    [string]$CacheVolume = 'clipedit-win-shared-build',

    [switch]$RebuildBuilderImage
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$recipePath = Join-Path $PSScriptRoot 'native/windows-shared-media'
$builderImage = 'clipedit-windows-shared-media:2026-08-13'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot 'packages/native/media-stack/win-x64/mpv-f4d13-ffmpeg-9.0.1-shared/runtime'
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "The native stack output path already exists: $fullOutputPath"
}

& docker version --format '{{.Server.Version}}' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'A running Docker engine is required to build the Windows shared media stack.'
}

$imageExists = -not $RebuildBuilderImage -and $null -ne (& docker image inspect $builderImage 2>$null)
if (-not $imageExists) {
    & docker build --tag $builderImage $recipePath
    if ($LASTEXITCODE -ne 0) {
        throw "Docker failed to build the pinned native builder image (exit $LASTEXITCODE)."
    }
}

$stagingPath = "$fullOutputPath.staging-$([Guid]::NewGuid().ToString('N'))"
try {
    [System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null
    & docker run --rm `
        --mount "type=volume,source=$CacheVolume,target=/cache" `
        --mount "type=bind,source=$stagingPath,target=/output" `
        --env "CLIPEDIT_NATIVE_JOBS=$Jobs" `
        $builderImage
    if ($LASTEXITCODE -ne 0) {
        throw "The Windows shared media stack build failed with exit code $LASTEXITCODE."
    }

    $binPath = Join-Path $stagingPath 'bin'
    $requiredNames = @(
        'ffmpeg.exe',
        'ffprobe.exe',
        'libmpv-2.dll',
        'avcodec-63.dll',
        'avdevice-63.dll',
        'avfilter-12.dll',
        'avformat-63.dll',
        'avutil-61.dll',
        'swresample-7.dll',
        'swscale-10.dll',
        'vulkan-1.dll'
    )
    foreach ($name in $requiredNames) {
        $requiredPath = Join-Path $binPath $name
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "The source build did not produce $requiredPath."
        }
    }

    $ffmpegPath = Join-Path $binPath 'ffmpeg.exe'
    $ffprobePath = Join-Path $binPath 'ffprobe.exe'
    $versionText = (& $ffmpegPath -version | Select-Object -First 1)
    if ($versionText -notmatch 'ffmpeg version n9\.0\.1') {
        throw "The source-built FFmpeg did not report version n9.0.1: $versionText"
    }
    $probeVersionText = (& $ffprobePath -version | Select-Object -First 1)
    if ($probeVersionText -notmatch 'ffprobe version n9\.0\.1') {
        throw "The source-built ffprobe did not report version n9.0.1: $probeVersionText"
    }

    $encoders = (& $ffmpegPath -hide_banner -encoders 2>&1 | Out-String)
    foreach ($encoder in @('libx264', 'libvpx-vp9', 'aac', 'libopus')) {
        if ($encoders -notmatch "\b$([regex]::Escape($encoder))\b") {
            throw "The source-built FFmpeg does not expose the required $encoder encoder."
        }
    }
    $filters = (& $ffmpegPath -hide_banner -filters 2>&1 | Out-String)
    foreach ($filter in @('crop', 'scale', 'rotate', 'overlay', 'concat', 'atrim', 'asetpts', 'aeval', 'volume', 'amix', 'alimiter', 'apad', 'showwavespic')) {
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
