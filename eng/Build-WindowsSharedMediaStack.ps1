[CmdletBinding()]
param(
    [string]$OutputPath,

    [string]$Msys2Root = $env:MSYS2_ROOT
)

$ErrorActionPreference = 'Stop'

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'The MSYS2 Windows media stack must be assembled on Windows.'
}

$workspaceRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')
$nativeDependencies = Get-ClipEditNativeDependencies
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Get-ClipEditWindowsNativeStackPath `
        -WorkspaceRoot $workspaceRoot `
        -Dependencies $nativeDependencies
}
if ([string]::IsNullOrWhiteSpace($Msys2Root)) {
    $Msys2Root = 'C:\msys64'
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$fullMsys2Root = [System.IO.Path]::GetFullPath($Msys2Root)
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "The native stack output path already exists: $fullOutputPath"
}

$pacmanPath = Join-Path $fullMsys2Root 'usr/bin/pacman.exe'
$ucrtBinPath = Join-Path $fullMsys2Root 'ucrt64/bin'
$objdumpPath = Join-Path $ucrtBinPath 'objdump.exe'
foreach ($requiredTool in @($pacmanPath, $objdumpPath)) {
    if (-not (Test-Path -LiteralPath $requiredTool -PathType Leaf)) {
        throw "MSYS2 UCRT64 is incomplete. Missing $requiredTool. Use msys2/setup-msys2 with the FFmpeg, mpv, and binutils packages."
    }
}

function Invoke-Pacman([string[]]$Arguments) {
    $output = @(& $pacmanPath @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "pacman $($Arguments -join ' ') failed:`n$($output -join [Environment]::NewLine)"
    }
    return $output
}

function Get-InstalledPackageVersion([string]$PackageName) {
    $line = [string](Invoke-Pacman @('-Q', $PackageName) | Select-Object -First 1)
    if ($line -notmatch "^$([regex]::Escape($PackageName))\s+(\S+)$") {
        throw "Could not parse the installed MSYS2 package version from: $line"
    }
    return $Matches[1]
}

function ConvertTo-MsysPath([string]$WindowsPath) {
    $fullPath = [System.IO.Path]::GetFullPath($WindowsPath)
    if (-not $fullPath.StartsWith($fullMsys2Root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The path is outside the configured MSYS2 root: $fullPath"
    }
    return '/' + $fullPath.Substring($fullMsys2Root.Length).TrimStart('\', '/').Replace('\', '/')
}

function Get-PeImports([string]$FilePath) {
    $output = @(& $objdumpPath -p $FilePath 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "objdump could not inspect $FilePath.`n$($output -join [Environment]::NewLine)"
    }
    return @($output | ForEach-Object {
        if ([string]$_ -match '^\s*DLL Name:\s*(\S+)\s*$') {
            $Matches[1]
        }
    } | Sort-Object -Unique)
}

function Invoke-StackTool([string]$Executable, [string[]]$Arguments) {
    $oldPath = $env:PATH
    try {
        $env:PATH = "$script:stagingBinPath;$oldPath"
        $output = @(& $Executable @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "$([System.IO.Path]::GetFileName($Executable)) $($Arguments -join ' ') failed:`n$($output -join [Environment]::NewLine)"
        }
        return $output
    }
    finally {
        $env:PATH = $oldPath
    }
}

function Get-CapabilityNames([string[]]$Lines) {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $Lines) {
        $trimmed = ([string]$line).Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.EndsWith(':')) {
            continue
        }
        $tokens = @($trimmed -split '\s+')
        if ($tokens.Count -eq 0) {
            continue
        }
        $candidate = if ($tokens.Count -gt 1 -and $tokens[0] -match '^[A-Z\.]{1,8}$') {
            $tokens[1]
        }
        else {
            $tokens[0]
        }
        if ($candidate -in @('=', '--', 'Decoders:', 'Encoders:', 'Filters:') -or $candidate.Contains('=')) {
            continue
        }
        foreach ($name in $candidate.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
            $names.Add($name.Trim()) | Out-Null
        }
    }
    return $names
}

foreach ($package in @($nativeDependencies.windows.packages)) {
    $actualVersion = Get-InstalledPackageVersion ([string]$package.name)
    if ($actualVersion -ne [string]$package.version) {
        throw "MSYS2 package $($package.name) is $actualVersion; the reviewed release manifest requires $($package.version). Run the manual native dependency update before releasing."
    }
}

$stagingPath = "$fullOutputPath.staging-$([Guid]::NewGuid().ToString('N'))"
try {
    $script:stagingBinPath = Join-Path $stagingPath 'bin'
    $licensesPath = Join-Path $stagingPath 'licenses'
    $capabilitiesPath = Join-Path $stagingPath 'capabilities'
    [System.IO.Directory]::CreateDirectory($script:stagingBinPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($licensesPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($capabilitiesPath) | Out-Null

    $queuedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($name in @($nativeDependencies.windows.requiredBinaries)) {
        if ($queuedNames.Add([string]$name)) {
            $queue.Enqueue([string]$name)
        }
    }

    $importsByFile = @{}
    $packageVersions = @{}
    while ($queue.Count -gt 0) {
        $name = $queue.Dequeue()
        $sourcePath = Join-Path $ucrtBinPath $name
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "The reviewed MSYS2 runtime file is missing: $sourcePath"
        }

        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $script:stagingBinPath $name)
        $imports = @(Get-PeImports $sourcePath)
        $importSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($import in $imports) {
            $importSet.Add($import) | Out-Null
        }
        $importsByFile[$name] = $importSet

        foreach ($import in $imports) {
            $importPath = Join-Path $ucrtBinPath $import
            if ((Test-Path -LiteralPath $importPath -PathType Leaf) -and $queuedNames.Add($import)) {
                $queue.Enqueue($import)
            }
        }
    }

    $ownershipArguments = [Collections.Generic.List[string]]::new()
    $ownershipArguments.Add('-Qo')
    foreach ($binaryName in @($queuedNames | Sort-Object)) {
        $ownershipArguments.Add((ConvertTo-MsysPath (Join-Path $ucrtBinPath $binaryName)))
    }
    foreach ($line in @(Invoke-Pacman $ownershipArguments.ToArray())) {
        if ([string]$line -notmatch '\sis owned by\s+(\S+)\s+(\S+)$') {
            throw "Could not parse an MSYS2 package owner from: $line"
        }
        $packageVersions[$Matches[1]] = $Matches[2]
    }

    foreach ($sharedImport in @($nativeDependencies.windows.sharedByFfmpegAndMpv)) {
        foreach ($consumer in @('ffmpeg.exe', 'libmpv-2.dll')) {
            if (-not $importsByFile[$consumer].Contains([string]$sharedImport)) {
                throw "$consumer does not import the reviewed shared FFmpeg library $sharedImport. The bundle would duplicate or bypass the shared libav stack."
            }
        }
    }

    $importsManifest = [Collections.Generic.List[string]]::new()
    $importsManifest.Add("binary`timport")
    foreach ($binary in @($importsByFile.Keys | Sort-Object)) {
        foreach ($import in @($importsByFile[$binary] | Sort-Object)) {
            $importsManifest.Add("$binary`t$import")
        }
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $stagingPath 'PE-IMPORTS.tsv'),
        $importsManifest,
        (New-Object System.Text.UTF8Encoding($false)))

    $licenseManifest = [Collections.Generic.List[string]]::new()
    $licenseManifest.Add("package`tversion`tpath")
    $packagesWithoutInstalledLicense = [Collections.Generic.List[string]]::new()
    $packagesWithoutInstalledLicense.Add("package`tversion")
    $packageNames = @($packageVersions.Keys | Sort-Object)
    $licenseCounts = @{}
    $licenseArguments = [Collections.Generic.List[string]]::new()
    $licenseArguments.Add('-Ql')
    foreach ($packageName in $packageNames) {
        $licenseCounts[$packageName] = 0
        $licenseArguments.Add($packageName)
    }
    foreach ($line in @(Invoke-Pacman $licenseArguments.ToArray())) {
        if ([string]$line -notmatch '^(\S+)\s+(/ucrt64/share/licenses/\S+)$') {
            continue
        }
        $packageName = $Matches[1]
        $msysLicensePath = $Matches[2]
        $sourceLicensePath = Join-Path $fullMsys2Root $msysLicensePath.TrimStart('/').Replace('/', '\')
        if (-not (Test-Path -LiteralPath $sourceLicensePath -PathType Leaf)) {
            continue
        }
        $relativeLicensePath = $msysLicensePath.Substring('/ucrt64/share/licenses/'.Length)
        $destinationRelativePath = "$packageName/$relativeLicensePath"
        $destinationLicensePath = Join-Path $licensesPath $destinationRelativePath.Replace('/', '\')
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destinationLicensePath)) | Out-Null
        Copy-Item -LiteralPath $sourceLicensePath -Destination $destinationLicensePath
        $licenseManifest.Add("$packageName`t$($packageVersions[$packageName])`t$($destinationRelativePath.Replace('\', '/'))")
        $licenseCounts[$packageName]++
    }
    foreach ($packageName in $packageNames) {
        if ($licenseCounts[$packageName] -eq 0) {
            $packagesWithoutInstalledLicense.Add("$packageName`t$($packageVersions[$packageName])")
        }
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $stagingPath 'LICENSE-MANIFEST.tsv'),
        $licenseManifest,
        (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllLines(
        (Join-Path $stagingPath 'PACKAGES-WITHOUT-INSTALLED-LICENSE.tsv'),
        $packagesWithoutInstalledLicense,
        (New-Object System.Text.UTF8Encoding($false)))

    $packageManifest = [Collections.Generic.List[string]]::new()
    $packageManifest.Add("package`tversion")
    foreach ($packageName in @($packageVersions.Keys | Sort-Object)) {
        $packageManifest.Add("$packageName`t$($packageVersions[$packageName])")
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $stagingPath 'MSYS2-PACKAGES.tsv'),
        $packageManifest,
        (New-Object System.Text.UTF8Encoding($false)))

    $ffmpegPath = Join-Path $script:stagingBinPath 'ffmpeg.exe'
    $ffprobePath = Join-Path $script:stagingBinPath 'ffprobe.exe'
    $ffmpegVersionOutput = @(Invoke-StackTool $ffmpegPath @('-version'))
    $ffprobeVersionOutput = @(Invoke-StackTool $ffprobePath @('-version'))
    $expectedFfmpegVersion = [string]$nativeDependencies.windows.ffmpegVersion
    if ([string]$ffmpegVersionOutput[0] -notmatch "^ffmpeg version n?$([regex]::Escape($expectedFfmpegVersion))(?:\s|$)") {
        throw "The MSYS2 FFmpeg version is not $expectedFfmpegVersion`: $($ffmpegVersionOutput[0])"
    }
    if ([string]$ffprobeVersionOutput[0] -notmatch "^ffprobe version n?$([regex]::Escape($expectedFfmpegVersion))(?:\s|$)") {
        throw "The MSYS2 ffprobe version is not $expectedFfmpegVersion`: $($ffprobeVersionOutput[0])"
    }

    $capabilityCommands = [ordered]@{
        decoders = @('-hide_banner', '-decoders')
        encoders = @('-hide_banner', '-encoders')
        demuxers = @('-hide_banner', '-demuxers')
        muxers = @('-hide_banner', '-muxers')
        filters = @('-hide_banner', '-filters')
        protocols = @('-hide_banner', '-protocols')
        bitstreamFilters = @('-hide_banner', '-bsfs')
        hardwareAccelerators = @('-hide_banner', '-hwaccels')
    }
    foreach ($entry in $capabilityCommands.GetEnumerator()) {
        $output = @(Invoke-StackTool $ffmpegPath $entry.Value)
        [System.IO.File]::WriteAllLines(
            (Join-Path $capabilitiesPath "$($entry.Key).txt"),
            $output,
            (New-Object System.Text.UTF8Encoding($false)))
        $available = Get-CapabilityNames $output
        foreach ($required in @($nativeDependencies.windows.requiredCapabilities.($entry.Key))) {
            if (-not $available.Contains([string]$required)) {
                throw "The MSYS2 FFmpeg package is missing reviewed $($entry.Key) capability '$required'."
            }
        }
    }

    $oldPath = $env:PATH
    $mpvHandle = [IntPtr]::Zero
    try {
        $env:PATH = "$script:stagingBinPath;$oldPath"
        if (-not ('ClipEditNativeLoader' -as [type])) {
            Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ClipEditNativeLoader
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr module);
}
'@
        }
        $mpvHandle = [ClipEditNativeLoader]::LoadLibrary(
            (Join-Path $script:stagingBinPath 'libmpv-2.dll'))
        if ($mpvHandle -eq [IntPtr]::Zero) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "Windows could not load the assembled libmpv dependency closure (error $errorCode)."
        }
        $apiVersionExport = [ClipEditNativeLoader]::GetProcAddress(
            $mpvHandle,
            'mpv_client_api_version')
        if ($apiVersionExport -eq [IntPtr]::Zero) {
            throw 'The MSYS2 libmpv package does not export mpv_client_api_version.'
        }
    }
    finally {
        if ($mpvHandle -ne [IntPtr]::Zero) {
            [ClipEditNativeLoader]::FreeLibrary($mpvHandle) | Out-Null
        }
        $env:PATH = $oldPath
    }

    $smokeRoot = Join-Path $stagingPath '.smoke'
    [System.IO.Directory]::CreateDirectory($smokeRoot) | Out-Null
    $smokeMp4 = Join-Path $smokeRoot 'h264-aac.mp4'
    $smokeGif = Join-Path $smokeRoot 'palette.gif'
    Invoke-StackTool $ffmpegPath @(
        '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=24:duration=0.25',
        '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:duration=0.25',
        '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac',
        '-y', $smokeMp4) | Out-Null
    Invoke-StackTool $ffprobePath @('-v', 'error', '-show_streams', $smokeMp4) | Out-Null
    Invoke-StackTool $ffmpegPath @(
        '-hide_banner', '-loglevel', 'error',
        '-f', 'lavfi', '-i', 'testsrc2=size=160x90:rate=10:duration=0.2',
        '-vf', 'split[a][b];[a]palettegen[p];[b][p]paletteuse', '-y', $smokeGif) | Out-Null
    [System.IO.Directory]::Delete($smokeRoot, $true)

    $stackDescription = [Collections.Generic.List[string]]::new()
    $stackDescription.Add('ClipEdit Windows native media stack')
    $stackDescription.Add("Distribution: $($nativeDependencies.windows.distribution)")
    $stackDescription.Add("Stack ID: $($nativeDependencies.windows.stackId)")
    $stackDescription.Add("FFmpeg: $($ffmpegVersionOutput[0])")
    $stackDescription.Add("ffprobe: $($ffprobeVersionOutput[0])")
    $stackDescription.Add("Runtime files: $((Get-ChildItem -LiteralPath $script:stagingBinPath -File).Count)")
    $stackDescription.Add("Owning packages: $($packageVersions.Count)")
    [System.IO.File]::WriteAllLines(
        (Join-Path $stagingPath 'NATIVE-STACK.txt'),
        $stackDescription,
        (New-Object System.Text.UTF8Encoding($false)))

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fullOutputPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $fullOutputPath)
    Write-Host "ClipEdit Windows MSYS2 media stack is ready at $fullOutputPath"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
}
