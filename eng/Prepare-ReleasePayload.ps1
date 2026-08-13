[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$RuntimeId = 'win-x64',

    [string]$OutputPath,

    [string]$WslDistribution = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = if ($RuntimeId -eq 'win-x64') {
        Join-Path $workspaceRoot 'packages/native/release/win-x64/ffmpeg-9.0.1-full-shared/payload'
    }
    else {
        Join-Path $workspaceRoot "packages/native/release/$RuntimeId/payload"
    }
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "The payload output path already exists: $fullOutputPath"
}

if ($RuntimeId -eq 'linux-x64') {
    $linuxScript = Join-Path $PSScriptRoot 'Prepare-LinuxReleasePayload.sh'
    if (-not (Test-Path -LiteralPath $linuxScript -PathType Leaf)) {
        throw "Linux payload script is missing: $linuxScript"
    }

    $hostIsLinux = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux)
    if ($hostIsLinux) {
        & bash $linuxScript $fullOutputPath
    }
    else {
        $linuxWorkspace = (& wsl.exe -d $WslDistribution -- wslpath -a $workspaceRoot).Trim()
        $linuxOutput = (& wsl.exe -d $WslDistribution -- wslpath -a $fullOutputPath).Trim()
        if ($LASTEXITCODE -ne 0 -or
            [string]::IsNullOrWhiteSpace($linuxWorkspace) -or
            [string]::IsNullOrWhiteSpace($linuxOutput)) {
            throw "Could not map the workspace into WSL distribution '$WslDistribution'."
        }

        & wsl.exe -d $WslDistribution -- bash `
            "$linuxWorkspace/eng/Prepare-LinuxReleasePayload.sh" `
            $linuxOutput
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Linux payload preparation failed with exit code $LASTEXITCODE."
    }

    return
}

$ffmpegVersion = '9.0.1'
$mpvReleaseTag = '2026-08-11-f4d13e1c2c'
$ffmpegRoot = Join-Path $workspaceRoot "packages/native/ffmpeg/win-x64/$ffmpegVersion/shared/runtime/ffmpeg-$ffmpegVersion-full_build-shared"
$mpvRoot = Join-Path $workspaceRoot "packages/native/libmpv/win-x64/$mpvReleaseTag/runtime"
$ffmpegPath = Join-Path $ffmpegRoot 'bin/ffmpeg.exe'
$ffprobePath = Join-Path $ffmpegRoot 'bin/ffprobe.exe'
$libMpvPath = Join-Path $mpvRoot 'libmpv-2.dll'
$sharedLibraryNames = @(
    'avcodec-63.dll',
    'avdevice-63.dll',
    'avfilter-12.dll',
    'avformat-63.dll',
    'avutil-61.dll',
    'swresample-7.dll',
    'swscale-10.dll'
)
$sharedLibraryPaths = @($sharedLibraryNames | ForEach-Object { Join-Path $ffmpegRoot "bin/$_" })

$missingFfmpegFiles = @(@($ffmpegPath, $ffprobePath) + $sharedLibraryPaths | Where-Object {
    -not (Test-Path -LiteralPath $_ -PathType Leaf)
})
if ($missingFfmpegFiles.Count -gt 0) {
    & (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1') -Linkage Shared
}

if (-not (Test-Path -LiteralPath $libMpvPath -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'Get-LibMpv.ps1')
}
foreach ($requiredPath in @(@($ffmpegPath, $ffprobePath, $libMpvPath) + $sharedLibraryPaths)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "The Windows release payload source is missing $requiredPath."
    }
}

$stagingPath = "$fullOutputPath.staging-$([Guid]::NewGuid().ToString('N'))"
try {
    $toolPath = Join-Path $stagingPath 'tools/ffmpeg'
    $licensePath = Join-Path $stagingPath 'licenses'
    [System.IO.Directory]::CreateDirectory($toolPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($licensePath) | Out-Null

    Copy-Item -LiteralPath $ffmpegPath -Destination (Join-Path $toolPath 'ffmpeg.exe')
    Copy-Item -LiteralPath $ffprobePath -Destination (Join-Path $toolPath 'ffprobe.exe')
    foreach ($libraryPath in $sharedLibraryPaths) {
        Copy-Item -LiteralPath $libraryPath -Destination (Join-Path $toolPath (Split-Path -Leaf $libraryPath))
    }
    Copy-Item -LiteralPath $libMpvPath -Destination (Join-Path $stagingPath 'libmpv-2.dll')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'LICENSE') -Destination (Join-Path $stagingPath 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'THIRD_PARTY_NOTICES.md') -Destination $licensePath
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'docs/07-native-dependencies.md') -Destination $licensePath
    Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'LICENSE') -Destination (Join-Path $licensePath 'FFmpeg-GPL-3.0.txt')
    Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'README.txt') -Destination (Join-Path $licensePath 'FFmpeg-build-README.txt')

    $checksums = Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($stagingPath.Length).TrimStart([char]'\').Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relativePath"
        }
    [System.IO.File]::WriteAllLines((Join-Path $licensePath 'PAYLOAD-SHA256SUMS'), $checksums)

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fullOutputPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $fullOutputPath)
    Write-Host "ClipEdit $RuntimeId native payload is ready at $fullOutputPath"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
}
