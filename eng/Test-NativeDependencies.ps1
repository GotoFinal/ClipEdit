[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')

$pins = Get-ClipEditNativeDependencies
$linuxFfmpegVersion = [string]$pins.components.ffmpeg.version

$windowsPackages = @($pins.windows.packages)
$packageNames = @($windowsPackages | ForEach-Object { [string]$_.name })
$duplicatePackages = @($packageNames | Group-Object | Where-Object Count -gt 1)
if ($duplicatePackages.Count -gt 0) {
    throw "Windows MSYS2 package lock contains duplicates: $($duplicatePackages.Name -join ', ')"
}
foreach ($requiredPackage in @(
    'mingw-w64-ucrt-x86_64-ffmpeg',
    'mingw-w64-ucrt-x86_64-mpv')) {
    if ($requiredPackage -notin $packageNames) {
        throw "Windows MSYS2 package lock is missing $requiredPackage."
    }
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
foreach ($sharedLibrary in @($pins.windows.sharedByFfmpegAndMpv)) {
    if ($sharedLibrary -notin @($pins.windows.sharedLibavImports)) {
        throw "Shared FFmpeg/mpv import $sharedLibrary is not in the shared libav list."
    }
}

$capabilityGroups = @(
    'decoders',
    'encoders',
    'demuxers',
    'muxers',
    'filters',
    'protocols',
    'bitstreamFilters',
    'hardwareAccelerators')
foreach ($group in $capabilityGroups) {
    $values = @($pins.windows.requiredCapabilities.$group)
    if ($values.Count -eq 0) {
        throw "Windows capability baseline '$group' is empty."
    }
    $duplicates = @($values | Group-Object | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "Windows capability baseline '$group' contains duplicates: $($duplicates.Name -join ', ')"
    }
}

Write-Host "Native dependency manifest is consistent: Linux FFmpeg $linuxFfmpegVersion; Windows stack $($pins.windows.stackId)."
