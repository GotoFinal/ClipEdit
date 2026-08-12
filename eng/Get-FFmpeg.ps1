[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$version = '9.0.1'
$archiveName = "ffmpeg-$version-full_build.7z"
$expectedSha256 = '4b9c814cb07a1f90d05b768ef4eb2abbf89af94bbb924df5b7dbd6e64e1e2b96'
$downloadUri = "https://www.gyan.dev/ffmpeg/builds/packages/$archiveName"
$archiveRootName = "ffmpeg-$version-full_build"

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $workspaceRoot "packages/native/ffmpeg/win-x64/$version"
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
foreach ($requiredPath in @($ffmpegPath, $ffprobePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "The extracted FFmpeg package is missing $requiredPath."
    }
}

$versionText = (& $ffmpegPath -version | Select-Object -First 1)
if ($versionText -notmatch "ffmpeg version $([regex]::Escape($version))") {
    throw "The extracted FFmpeg executable did not report version $version."
}

$encoders = (& $ffmpegPath -hide_banner -encoders 2>&1 | Out-String)
foreach ($encoder in @('libx264', 'libvpx-vp9', 'aac', 'libopus')) {
    if ($encoders -notmatch "\b$([regex]::Escape($encoder))\b") {
        throw "The extracted FFmpeg package does not expose the required $encoder encoder."
    }
}

Write-Host "FFmpeg $version GPL full build is ready at $binPath"
