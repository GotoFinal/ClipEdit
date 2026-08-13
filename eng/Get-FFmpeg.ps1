[CmdletBinding()]
param(
    [ValidateSet('Static', 'Shared')]
    [string]$Linkage = 'Static'
)

$ErrorActionPreference = 'Stop'

$version = '9.0.1'
$shared = $Linkage -eq 'Shared'
$archiveVariant = if ($shared) { 'full_build-shared' } else { 'full_build' }
$archiveName = "ffmpeg-$version-$archiveVariant.7z"
$expectedSha256 = if ($shared) {
    'cb4d5e8db6a3353bffdb2100d3eb4b76733457fa443215e236f57c99f9ffdca4'
}
else {
    '4b9c814cb07a1f90d05b768ef4eb2abbf89af94bbb924df5b7dbd6e64e1e2b96'
}
$downloadUri = "https://www.gyan.dev/ffmpeg/builds/packages/$archiveName"
$archiveRootName = "ffmpeg-$version-$archiveVariant"

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = if ($shared) {
    Join-Path $workspaceRoot "packages/native/ffmpeg/win-x64/$version/shared"
}
else {
    Join-Path $workspaceRoot "packages/native/ffmpeg/win-x64/$version"
}
$archivePath = Join-Path $packageRoot $archiveName
$partialPath = "$archivePath.download"
$runtimePath = Join-Path $packageRoot 'runtime'
$binPath = Join-Path $runtimePath "$archiveRootName/bin"

[System.IO.Directory]::CreateDirectory($packageRoot) | Out-Null

if (Test-Path -LiteralPath $archivePath) {
    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "Existing FFmpeg archive has SHA-256 $actualSha256; expected $expectedSha256."
    }
}
else {
    try {
        Invoke-WebRequest -Uri $downloadUri -OutFile $partialPath
        $actualSha256 = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualSha256 -ne $expectedSha256) {
            throw "Downloaded FFmpeg archive has SHA-256 $actualSha256; expected $expectedSha256."
        }

        [System.IO.File]::Move($partialPath, $archivePath)
    }
    finally {
        if (Test-Path -LiteralPath $partialPath) {
            [System.IO.File]::Delete($partialPath)
        }
    }
}

$sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
if ($null -eq $sevenZip) {
    $bundledSevenZip = 'C:\Apps\7-Zip\7z.exe'
    if (Test-Path -LiteralPath $bundledSevenZip) {
        $sevenZip = Get-Item -LiteralPath $bundledSevenZip
    }
}

if ($null -eq $sevenZip) {
    throw '7z.exe is required to extract FFmpeg. Install 7-Zip or add 7z.exe to PATH.'
}

[System.IO.Directory]::CreateDirectory($runtimePath) | Out-Null
$sevenZipPath = if ($sevenZip -is [System.IO.FileInfo]) { $sevenZip.FullName } else { $sevenZip.Source }
& $sevenZipPath x $archivePath "-o$runtimePath" -y | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "7-Zip failed with exit code $LASTEXITCODE."
}

$ffmpegPath = Join-Path $binPath 'ffmpeg.exe'
$ffprobePath = Join-Path $binPath 'ffprobe.exe'
$requiredPaths = @($ffmpegPath, $ffprobePath)
if ($shared) {
    $requiredPaths += @(
        'avcodec-63.dll',
        'avdevice-63.dll',
        'avfilter-12.dll',
        'avformat-63.dll',
        'avutil-61.dll',
        'swresample-7.dll',
        'swscale-10.dll'
    ) | ForEach-Object { Join-Path $binPath $_ }
}

foreach ($requiredPath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "The extracted FFmpeg package is missing $requiredPath."
    }
}

$versionText = (& $ffmpegPath -version | Select-Object -First 1)
if ($versionText -notmatch "ffmpeg version $([regex]::Escape($version))") {
    throw "The extracted FFmpeg executable did not report version $version."
}
$probeVersionText = (& $ffprobePath -version | Select-Object -First 1)
if ($probeVersionText -notmatch "ffprobe version $([regex]::Escape($version))") {
    throw "The extracted ffprobe executable did not report version $version."
}

$encoders = (& $ffmpegPath -hide_banner -encoders 2>&1 | Out-String)
foreach ($encoder in @('libx264', 'libvpx-vp9', 'aac', 'libopus')) {
    if ($encoders -notmatch "\b$([regex]::Escape($encoder))\b") {
        throw "The extracted FFmpeg package does not expose the required $encoder encoder."
    }
}

$filters = (& $ffmpegPath -hide_banner -filters 2>&1 | Out-String)
foreach ($filter in @('crop', 'scale', 'rotate', 'overlay', 'concat', 'atrim', 'asetpts', 'aeval', 'volume', 'amix', 'alimiter', 'apad', 'showwavespic')) {
    if ($filters -notmatch "\b$([regex]::Escape($filter))\b") {
        throw "The extracted FFmpeg package does not expose the required $filter filter."
    }
}

Write-Host "FFmpeg $version GPL full $($Linkage.ToLowerInvariant()) build is ready at $binPath"
