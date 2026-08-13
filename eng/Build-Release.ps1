[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$RuntimeId = 'win-x64',

    [ValidateSet('SingleFile', 'Directory')]
    [string]$BundleMode = 'SingleFile',

    [ValidateSet('FrameworkDependent', 'SelfContained')]
    [string]$ManagedDeployment = 'SelfContained',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version,

    [string]$NativePayloadPath,

    [string]$OutputPath,

    [switch]$SkipPayloadPreparation,

    [switch]$DisableCompression
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $workspaceRoot 'src/ClipEdit.App/ClipEdit.App.csproj'

if ([string]::IsNullOrWhiteSpace($Version)) {
    $safeWorkspace = $workspaceRoot.Replace('\', '/')
    $shortCommit = (& git -c "safe.directory=$safeWorkspace" rev-parse --short=12 HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($shortCommit)) {
        throw 'Could not determine the Git revision. Pass -Version explicitly.'
    }

    $Version = "0.1.0-dev.$shortCommit"
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a valid semantic version."
}

if ([string]::IsNullOrWhiteSpace($NativePayloadPath)) {
    $NativePayloadPath = if ($RuntimeId -eq 'win-x64') {
        Join-Path $workspaceRoot 'packages/native/release/win-x64/shared-media-stack-v1/payload'
    }
    else {
        Join-Path $workspaceRoot "packages/native/release/$RuntimeId/payload"
    }
}

$fullPayloadPath = [System.IO.Path]::GetFullPath($NativePayloadPath)
if (-not $SkipPayloadPreparation -and -not (Test-Path -LiteralPath $fullPayloadPath)) {
    & (Join-Path $PSScriptRoot 'Prepare-ReleasePayload.ps1') `
        -RuntimeId $RuntimeId `
        -OutputPath $fullPayloadPath
    if ($LASTEXITCODE -ne 0) {
        throw "Native payload preparation failed with exit code $LASTEXITCODE."
    }
}

$executableSuffix = if ($RuntimeId -eq 'win-x64') { '.exe' } else { '' }
$requiredPayload = @(
    "tools/ffmpeg/ffmpeg$executableSuffix",
    "tools/ffmpeg/ffprobe$executableSuffix",
    $(if ($RuntimeId -eq 'win-x64') { 'tools/ffmpeg/libmpv-2.dll' } else { 'libmpv.so.2' }),
    'LICENSE.txt',
    'licenses/THIRD_PARTY_NOTICES.md',
    'licenses/07-native-dependencies.md'
)

if ($RuntimeId -eq 'win-x64') {
    $requiredPayload += @(
        'tools/ffmpeg/avcodec-63.dll',
        'tools/ffmpeg/avdevice-63.dll',
        'tools/ffmpeg/avfilter-12.dll',
        'tools/ffmpeg/avformat-63.dll',
        'tools/ffmpeg/avutil-61.dll',
        'tools/ffmpeg/swresample-7.dll',
        'tools/ffmpeg/swscale-10.dll',
        'tools/ffmpeg/vulkan-1.dll'
    )
}

$missingPayload = @($requiredPayload | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $fullPayloadPath $_) -PathType Leaf)
})
if ($missingPayload.Count -gt 0) {
    $formatted = $missingPayload -join [Environment]::NewLine
    throw "Native release payload '$fullPayloadPath' is incomplete. Missing:$([Environment]::NewLine)$formatted$([Environment]::NewLine)Prepare it with eng/Prepare-ReleasePayload.ps1 -RuntimeId $RuntimeId."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot "artifacts/release/$Version/$RuntimeId"
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "The output path already exists: $fullOutputPath"
}

$stagingRoot = Join-Path $workspaceRoot 'artifacts/.staging'
$buildId = [Guid]::NewGuid().ToString('N')
$stagingPath = Join-Path $stagingRoot $buildId
$buildArtifactsPath = Join-Path $stagingRoot "$buildId-build"
[System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null

try {
    $singleFile = $BundleMode -eq 'SingleFile'
    $selfContained = $ManagedDeployment -eq 'SelfContained'
    $compressionEnabled = $singleFile -and $selfContained -and -not $DisableCompression
    $singleFileValue = $singleFile.ToString().ToLowerInvariant()
    $selfContainedValue = $selfContained.ToString().ToLowerInvariant()
    $publishArguments = @(
        'publish',
        $projectPath,
        '--configuration', $Configuration,
        '--runtime', $RuntimeId,
        "-p:SelfContained=$selfContainedValue",
        '--artifacts-path', $buildArtifactsPath,
        '--output', $stagingPath,
        '--nologo',
        "-p:Version=$Version",
        "-p:ClipEditNativePayloadRoot=$fullPayloadPath",
        "-p:PublishSingleFile=$singleFileValue",
        "-p:IncludeNativeLibrariesForSelfExtract=$singleFileValue",
        "-p:IncludeAllContentForSelfExtract=$singleFileValue",
        "-p:EnableCompressionInSingleFile=$($compressionEnabled.ToString().ToLowerInvariant())",
        '-p:PublishTrimmed=false',
        '-p:DebugSymbols=false',
        '-p:DebugType=None'
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $executableName = "ClipEdit$executableSuffix"
    $executablePath = Join-Path $stagingPath $executableName
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "Publish output is missing $executableName."
    }

    if ($singleFile) {
        Get-ChildItem -LiteralPath $stagingPath -File -Filter '*.pdb' |
            ForEach-Object {
                [System.IO.File]::Delete($_.FullName)
            }

        $unexpectedRuntimeFiles = @(Get-ChildItem -LiteralPath $stagingPath -File |
            Where-Object Name -ne $executableName)
        if ($unexpectedRuntimeFiles.Count -gt 0) {
            throw "Single-file publish emitted unexpected sidecar files: $($unexpectedRuntimeFiles.Name -join ', ')"
        }
    }

    $hash = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        product = 'ClipEdit'
        version = $Version
        runtimeId = $RuntimeId
        bundleMode = $BundleMode
        executable = $executableName
        sha256 = $hash
        managedDeployment = $ManagedDeployment
        includesManagedRuntime = $selfContained
        requiredManagedFramework = if ($selfContained) { $null } else { 'Microsoft.NETCore.App' }
        requiredManagedFrameworkVersion = if ($selfContained) { $null } else { '10.0.0' }
        compressionEnabled = $compressionEnabled
        nativeMediaProfile = if ($RuntimeId -eq 'win-x64') {
            'ffmpeg-9.0.1-shared+libmpv-shared-libav-v1'
        } else {
            'ffmpeg-9.0.1-source-built+libmpv'
        }
        includesFFmpeg = $true
        includesLibMpv = $true
        publiclyRedistributable = $false
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingPath 'release-manifest.json'),
        ($manifest | ConvertTo-Json -Depth 3) + [Environment]::NewLine)
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingPath 'SHA256SUMS'),
        "$hash  $executableName$([Environment]::NewLine)")

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fullOutputPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $fullOutputPath)
    Write-Host "ClipEdit $RuntimeId $BundleMode $ManagedDeployment release candidate is ready at $fullOutputPath"
    Write-Warning 'The build is technically packaged but not cleared for public redistribution; review the embedded notices and source-offer requirements.'
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
    if (Test-Path -LiteralPath $buildArtifactsPath) {
        [System.IO.Directory]::Delete($buildArtifactsPath, $true)
    }
}
