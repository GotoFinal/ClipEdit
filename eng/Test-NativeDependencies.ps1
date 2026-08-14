[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')

$pins = Get-ClipEditNativeDependencies
Assert-ClipEditNativeSourceLock -Dependencies $pins

$ffmpegVersion = [string]$pins.components.ffmpeg.version
foreach ($runtimeId in @('win-x64', 'linux-x64')) {
    $profile = [string]$pins.releaseProfiles.$runtimeId
    if ([string]::IsNullOrWhiteSpace($profile) -or $profile -notmatch [regex]::Escape($ffmpegVersion)) {
        throw "Release profile '$runtimeId' does not identify FFmpeg $ffmpegVersion."
    }
}
if ([string]$pins.windows.stackId -notmatch [regex]::Escape($ffmpegVersion)) {
    throw "Windows native stack ID does not identify FFmpeg $ffmpegVersion."
}

$requiredBinaries = @($pins.windows.requiredBinaries)
$duplicateBinaries = @($requiredBinaries | Group-Object | Where-Object Count -gt 1)
if ($duplicateBinaries.Count -gt 0) {
    throw "Windows required binary list contains duplicates: $($duplicateBinaries.Name -join ', ')"
}
foreach ($requiredName in @('ffmpeg.exe', 'ffprobe.exe', 'libmpv-2.dll')) {
    if ($requiredName -notin $requiredBinaries) {
        throw "Windows required binary list is missing $requiredName."
    }
}
foreach ($sharedLibrary in @($pins.windows.sharedLibavImports)) {
    if ($sharedLibrary -notin $requiredBinaries) {
        throw "Shared libav import $sharedLibrary is not in the Windows required binary list."
    }
}

$patchText = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'native/windows-shared-media/mpv-winbuild-cmake.patch') -Raw
foreach ($placeholder in @('@CLIPEDIT_FFMPEG_REVISION@', '@CLIPEDIT_MPV_REVISION@')) {
    if (-not $patchText.Contains($placeholder)) {
        throw "Windows native patch is missing placeholder $placeholder."
    }
}

Write-Host "Native dependency manifest is consistent: FFmpeg $ffmpegVersion; Windows stack $($pins.windows.stackId)."
