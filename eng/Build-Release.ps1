[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$RuntimeId = 'win-x64',

    [ValidateSet('SingleFile', 'Directory')]
    [string]$BundleMode = 'SingleFile',

    [ValidateSet('FrameworkDependent', 'SelfContained')]
    [string]$ManagedDeployment = 'SelfContained',

    [ValidateSet('Bundled', 'System')]
    [string]$MediaDependencyMode = 'Bundled',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version,

    [string]$NativePayloadPath,

    [string]$OutputPath,

    [switch]$SkipPayloadPreparation,

    [switch]$DisableCompression,

    [switch]$GenerateCompliance,

    [string]$NativeCompliancePath,

    [switch]$AllowDirtyComplianceSource
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $workspaceRoot 'src/ClipEdit.App/ClipEdit.App.csproj'
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')
$nativeDependencies = Get-ClipEditNativeDependencies

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

if ($MediaDependencyMode -eq 'System') {
    if ($RuntimeId -ne 'linux-x64') {
        throw 'System media dependencies are currently supported only for linux-x64 releases.'
    }
    if ($ManagedDeployment -ne 'FrameworkDependent') {
        throw 'The Linux system-dependencies release must be FrameworkDependent so it does not bundle .NET.'
    }
}

$includesNativeMedia = $MediaDependencyMode -eq 'Bundled'
$releaseAssetId = if ($includesNativeMedia) { $RuntimeId } else { "$RuntimeId-system" }

if ([string]::IsNullOrWhiteSpace($NativePayloadPath)) {
    $NativePayloadPath = if ($RuntimeId -eq 'win-x64') {
        Join-Path $workspaceRoot 'packages/native/release/win-x64/shared-media-stack-v2/payload'
    }
    else {
        Join-Path $workspaceRoot "packages/native/release/$RuntimeId/payload"
    }
}

$fullPayloadPath = [System.IO.Path]::GetFullPath($NativePayloadPath)
if ($includesNativeMedia -and
    -not $SkipPayloadPreparation -and
    -not (Test-Path -LiteralPath $fullPayloadPath)) {
    & (Join-Path $PSScriptRoot 'Prepare-ReleasePayload.ps1') `
        -RuntimeId $RuntimeId `
        -OutputPath $fullPayloadPath
    if ($LASTEXITCODE -ne 0) {
        throw "Native payload preparation failed with exit code $LASTEXITCODE."
    }
}

$executableSuffix = if ($RuntimeId -eq 'win-x64') { '.exe' } else { '' }
$requiredPayload = @()
if ($includesNativeMedia) {
    $requiredPayload += @('LICENSE.txt', 'licenses/THIRD_PARTY_NOTICES.md')
    $requiredPayload += if ($RuntimeId -eq 'win-x64') {
        @($nativeDependencies.windows.requiredBinaries | ForEach-Object { "tools/ffmpeg/$_" })
    }
    else {
        @('tools/ffmpeg/ffmpeg', 'tools/ffmpeg/ffprobe', 'libmpv.so.2')
    }
}

$missingPayload = @($requiredPayload | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $fullPayloadPath $_) -PathType Leaf)
})
if ($missingPayload.Count -gt 0) {
    $formatted = $missingPayload -join [Environment]::NewLine
    throw "Native release payload '$fullPayloadPath' is incomplete. Missing:$([Environment]::NewLine)$formatted$([Environment]::NewLine)Prepare it with eng/Prepare-ReleasePayload.ps1 -RuntimeId $RuntimeId."
}

function Get-ClipEditDirectoryContentHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    $fullDirectory = [System.IO.Path]::GetFullPath($Directory)
    $manifestLines = Get-ChildItem -LiteralPath $fullDirectory -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($fullDirectory.Length)
            $relativePath = $relativePath.TrimStart([char[]]@('\', '/')).Replace('\', '/')
            $fileHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$fileHash  $relativePath"
        }
    $manifest = ($manifestLines -join "`n") + "`n"
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest))
        return [BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function New-ClipEditPayloadArchives {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PayloadPath,

        [Parameter(Mandatory = $true)]
        [string]$ArchiveRoot,

        [Parameter(Mandatory = $true)]
        [string]$RuntimeId,

        [Parameter(Mandatory = $true)]
        [bool]$IncludeMedia
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Directory]::CreateDirectory($ArchiveRoot) | Out-Null
    $runtimeStagingPath = Join-Path $ArchiveRoot '.runtime-staging'
    $noticesStagingPath = Join-Path $ArchiveRoot '.notices-staging'
    $runtimeArchivePath = Join-Path $ArchiveRoot 'media-runtime.zip'
    $noticesArchivePath = Join-Path $ArchiveRoot 'notices.zip'
    foreach ($temporaryPath in @($runtimeStagingPath, $noticesStagingPath)) {
        if (Test-Path -LiteralPath $temporaryPath) {
            [System.IO.Directory]::Delete($temporaryPath, $true)
        }
        [System.IO.Directory]::CreateDirectory($temporaryPath) | Out-Null
    }

    $runtimeItemNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    if ($IncludeMedia) {
        foreach ($runtimeItemName in @('tools', 'native', 'libmpv.so.2')) {
            $runtimeItemNames.Add($runtimeItemName) | Out-Null
        }
    }

    try {
        foreach ($payloadItem in Get-ChildItem -LiteralPath $PayloadPath -Force) {
            $destinationRoot = if ($runtimeItemNames.Contains($payloadItem.Name)) {
                $runtimeStagingPath
            }
            else {
                $noticesStagingPath
            }
            Copy-Item -LiteralPath $payloadItem.FullName `
                -Destination $destinationRoot `
                -Recurse `
                -Force
        }

        $runtimeArchiveId = $null
        if ($IncludeMedia) {
            $runtimeFileCount = @(Get-ChildItem -LiteralPath $runtimeStagingPath -Recurse -File).Count
            if ($runtimeFileCount -eq 0) {
                throw "The $RuntimeId payload contains no media runtime files."
            }
            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $runtimeStagingPath,
                $runtimeArchivePath,
                [System.IO.Compression.CompressionLevel]::Optimal,
                $false)
            $runtimeContentHash = Get-ClipEditDirectoryContentHash $runtimeStagingPath
            $runtimeArchiveId = "$RuntimeId-$runtimeContentHash"
        }

        $noticesArchiveId = $null
        $noticeFileCount = @(Get-ChildItem -LiteralPath $noticesStagingPath -Recurse -File).Count
        if ($noticeFileCount -gt 0) {
            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $noticesStagingPath,
                $noticesArchivePath,
                [System.IO.Compression.CompressionLevel]::Optimal,
                $false)
            $noticesArchiveId = Get-ClipEditDirectoryContentHash $noticesStagingPath
        }

        return [pscustomobject]@{
            MediaArchivePath = if ($IncludeMedia) { $runtimeArchivePath } else { $null }
            MediaArchiveId = $runtimeArchiveId
            NoticesArchivePath = if ($noticeFileCount -gt 0) { $noticesArchivePath } else { $null }
            NoticesArchiveId = $noticesArchiveId
        }
    }
    finally {
        foreach ($temporaryPath in @($runtimeStagingPath, $noticesStagingPath)) {
            if (Test-Path -LiteralPath $temporaryPath) {
                [System.IO.Directory]::Delete($temporaryPath, $true)
            }
        }
    }
}

function Add-ClipEditPayloadArchiveArguments {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.List[string]]$Arguments,

        [Parameter(Mandatory = $true)]
        $Archives
    )

    if (-not [string]::IsNullOrWhiteSpace([string]$Archives.MediaArchivePath)) {
        $Arguments.Add("-p:ClipEditBundledMediaArchive=$($Archives.MediaArchivePath)")
        $Arguments.Add("-p:ClipEditBundledMediaRuntimeId=$($Archives.MediaArchiveId)")
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$Archives.NoticesArchivePath)) {
        $Arguments.Add("-p:ClipEditBundledNoticesArchive=$($Archives.NoticesArchivePath)")
        $Arguments.Add("-p:ClipEditBundledNoticesId=$($Archives.NoticesArchiveId)")
    }
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot "artifacts/release/$Version/$releaseAssetId"
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "The output path already exists: $fullOutputPath"
}
$releaseAssetName = if ($RuntimeId -eq 'win-x64') {
    'ClipEdit-win-x64.exe'
}
else {
    "ClipEdit-$releaseAssetId"
}
$releaseAssetPath = Join-Path (Split-Path -Parent $fullOutputPath) $releaseAssetName
$releaseChecksumPath = "$releaseAssetPath.sha256"
if ($BundleMode -eq 'SingleFile' -and
    ((Test-Path -LiteralPath $releaseAssetPath) -or
     (Test-Path -LiteralPath $releaseChecksumPath))) {
    throw "The GitHub release asset already exists: $releaseAssetPath"
}

$stagingRoot = Join-Path $workspaceRoot 'artifacts/.staging'
$buildId = [Guid]::NewGuid().ToString('N')
$stagingPath = Join-Path $stagingRoot $buildId
$buildArtifactsPath = Join-Path $stagingRoot "$buildId-build"
$complianceBuildPath = Join-Path $stagingRoot "$buildId-compliance"
$complianceDepsPath = Join-Path $buildArtifactsPath 'ClipEdit.release.deps.json'
[System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null

try {
    $singleFile = $BundleMode -eq 'SingleFile'
    $selfContained = $ManagedDeployment -eq 'SelfContained'
    $compressionEnabled = $singleFile -and $selfContained -and -not $DisableCompression
    $readyToRunEnabled = $singleFile
    $compositeReadyToRunEnabled = $readyToRunEnabled -and $selfContained
    $singleFileValue = $singleFile.ToString().ToLowerInvariant()
    $selfContainedValue = $selfContained.ToString().ToLowerInvariant()
    $publishArguments = @(
        'publish',
        $projectPath,
        '--configuration', $Configuration,
        '--runtime', $RuntimeId,
        '--configfile', (Join-Path $workspaceRoot 'NuGet.Config'),
        "-p:SelfContained=$selfContainedValue",
        '--artifacts-path', $buildArtifactsPath,
        '--output', $stagingPath,
        '--nologo',
        "-p:Version=$Version",
        "-p:ClipEditReleaseAssetId=$releaseAssetId",
        "-p:PublishSingleFile=$singleFileValue",
        "-p:IncludeNativeLibrariesForSelfExtract=$singleFileValue",
        '-p:IncludeAllContentForSelfExtract=false',
        "-p:EnableCompressionInSingleFile=$($compressionEnabled.ToString().ToLowerInvariant())",
        "-p:PublishReadyToRun=$($readyToRunEnabled.ToString().ToLowerInvariant())",
        "-p:PublishReadyToRunComposite=$($compositeReadyToRunEnabled.ToString().ToLowerInvariant())",
        '-p:PublishTrimmed=false',
        '-p:DebugSymbols=false',
        '-p:DebugType=None'
    )
    if ($includesNativeMedia -and $singleFile) {
        $initialArchives = New-ClipEditPayloadArchives `
            -PayloadPath $fullPayloadPath `
            -ArchiveRoot (Join-Path $buildArtifactsPath 'payload-archives-initial') `
            -RuntimeId $RuntimeId `
            -IncludeMedia $true
        $publishArgumentList = [Collections.Generic.List[string]]::new()
        $publishArgumentList.AddRange([string[]]$publishArguments)
        Add-ClipEditPayloadArchiveArguments -Arguments $publishArgumentList -Archives $initialArchives
        $publishArguments = $publishArgumentList.ToArray()
    }
    elseif ($includesNativeMedia) {
        $publishArguments += "-p:ClipEditNativePayloadRoot=$fullPayloadPath"
    }
    if ($GenerateCompliance) {
        $publishArguments += "-p:ClipEditComplianceDepsOutput=$complianceDepsPath"
    }
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

    $compliancePath = $null
    if ($GenerateCompliance) {
        if (-not (Test-Path -LiteralPath $complianceDepsPath -PathType Leaf) -and -not $singleFile) {
            $publishedDepsPath = Join-Path $stagingPath 'ClipEdit.deps.json'
            if (Test-Path -LiteralPath $publishedDepsPath -PathType Leaf) {
                [System.IO.Directory]::CreateDirectory($buildArtifactsPath) | Out-Null
                Copy-Item -LiteralPath $publishedDepsPath -Destination $complianceDepsPath -Force
            }
        }
        if (-not (Test-Path -LiteralPath $complianceDepsPath -PathType Leaf)) {
            throw "The publish did not capture its exact dependency manifest: $complianceDepsPath"
        }

        $compliancePath = $complianceBuildPath
        $complianceArguments = @{
            RuntimeId = $RuntimeId
            Version = $Version
            ManagedDeployment = $ManagedDeployment
            MediaDependencyMode = $MediaDependencyMode
            ReleaseAssetId = $releaseAssetId
            DepsJsonPath = $complianceDepsPath
            OutputPath = $compliancePath
        }
        if ($includesNativeMedia) {
            $complianceArguments.NativePayloadPath = $fullPayloadPath
        }
        if (-not [string]::IsNullOrWhiteSpace($NativeCompliancePath)) {
            $complianceArguments.NativeCompliancePath = $NativeCompliancePath
        }
        if ($AllowDirtyComplianceSource) {
            $complianceArguments.AllowDirtySource = $true
        }

        & (Join-Path $PSScriptRoot 'Build-ReleaseCompliance.ps1') @complianceArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Release compliance assembly failed with exit code $LASTEXITCODE."
        }

        $augmentedPayloadPath = Join-Path $buildArtifactsPath 'native-payload-with-compliance'
        [System.IO.Directory]::CreateDirectory($augmentedPayloadPath) | Out-Null
        if ($includesNativeMedia) {
            foreach ($payloadItem in Get-ChildItem -LiteralPath $fullPayloadPath -Force) {
                Copy-Item -LiteralPath $payloadItem.FullName `
                    -Destination $augmentedPayloadPath `
                    -Recurse `
                    -Force
            }
        }
        else {
            Copy-Item -LiteralPath (Join-Path $workspaceRoot 'LICENSE') `
                -Destination (Join-Path $augmentedPayloadPath 'LICENSE.txt')
        }
        $augmentedLicensePath = Join-Path $augmentedPayloadPath 'licenses'
        [System.IO.Directory]::CreateDirectory($augmentedLicensePath) | Out-Null
        Copy-Item -LiteralPath (Join-Path $compliancePath 'THIRD_PARTY_NOTICES.md') `
            -Destination (Join-Path $augmentedLicensePath 'THIRD_PARTY_NOTICES.md') `
            -Force
        Copy-Item -LiteralPath (Join-Path $compliancePath "ClipEdit-$Version-$releaseAssetId.spdx.json") `
            -Destination $augmentedLicensePath
        foreach ($licenseItem in Get-ChildItem -LiteralPath (Join-Path $compliancePath 'licenses') -Force) {
            Copy-Item -LiteralPath $licenseItem.FullName `
                -Destination $augmentedLicensePath `
                -Recurse `
                -Force
        }

        [System.IO.Directory]::Delete($stagingPath, $true)
        [System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null
        $finalPublishArguments = @($publishArguments | Where-Object {
            $_ -notlike '-p:ClipEditNativePayloadRoot=*' -and
            $_ -notlike '-p:ClipEditBundledMediaArchive=*' -and
            $_ -notlike '-p:ClipEditBundledMediaRuntimeId=*' -and
            $_ -notlike '-p:ClipEditBundledNoticesArchive=*' -and
            $_ -notlike '-p:ClipEditBundledNoticesId=*'
        })
        if ($singleFile) {
            $finalArchives = New-ClipEditPayloadArchives `
                -PayloadPath $augmentedPayloadPath `
                -ArchiveRoot (Join-Path $buildArtifactsPath 'payload-archives-final') `
                -RuntimeId $RuntimeId `
                -IncludeMedia $includesNativeMedia
            $finalPublishArgumentList = [Collections.Generic.List[string]]::new()
            $finalPublishArgumentList.AddRange([string[]]$finalPublishArguments)
            Add-ClipEditPayloadArchiveArguments `
                -Arguments $finalPublishArgumentList `
                -Archives $finalArchives
            $finalPublishArguments = $finalPublishArgumentList.ToArray()
        }
        else {
            $finalPublishArguments += "-p:ClipEditNativePayloadRoot=$augmentedPayloadPath"
        }
        & dotnet @finalPublishArguments
        if ($LASTEXITCODE -ne 0) {
            throw "The compliance-embedded dotnet publish failed with exit code $LASTEXITCODE."
        }

        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "Compliance-embedded publish output is missing $executableName."
        }
        if ($singleFile) {
            Get-ChildItem -LiteralPath $stagingPath -File -Filter '*.pdb' |
                ForEach-Object { [System.IO.File]::Delete($_.FullName) }
            $unexpectedFinalRuntimeFiles = @(Get-ChildItem -LiteralPath $stagingPath -File |
                Where-Object Name -ne $executableName)
            if ($unexpectedFinalRuntimeFiles.Count -gt 0) {
                throw "Compliance-embedded single-file publish emitted unexpected sidecars: $($unexpectedFinalRuntimeFiles.Name -join ', ')"
            }
        }

        [System.IO.Directory]::Move(
            $compliancePath,
            (Join-Path $stagingPath 'compliance'))
        $compliancePath = Join-Path $stagingPath 'compliance'
    }

    $hash = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $correspondingSourcePaths = if ($GenerateCompliance -and $includesNativeMedia) {
        @(
            "compliance/source/ClipEdit-$Version-source.zip",
            "compliance/source/ClipEdit-$Version-$RuntimeId-native-source.tar.zst")
    } elseif ($GenerateCompliance) {
        @("compliance/source/ClipEdit-$Version-source.zip")
    } else { @() }
    $manifest = [ordered]@{
        product = 'ClipEdit'
        version = $Version
        runtimeId = $RuntimeId
        releaseAssetId = $releaseAssetId
        bundleMode = $BundleMode
        executable = $executableName
        sha256 = $hash
        managedDeployment = $ManagedDeployment
        includesManagedRuntime = $selfContained
        requiredManagedFramework = if ($selfContained) { $null } else { 'Microsoft.NETCore.App' }
        requiredManagedFrameworkVersion = if ($selfContained) { $null } else { '10.0.0' }
        compressionEnabled = $compressionEnabled
        readyToRunEnabled = $readyToRunEnabled
        compositeReadyToRunEnabled = $compositeReadyToRunEnabled
        mediaDependencyMode = $MediaDependencyMode
        nativeMediaProfile = if ($includesNativeMedia) {
            [string]$nativeDependencies.releaseProfiles.$RuntimeId
        } else { $null }
        includesFFmpeg = $includesNativeMedia
        includesLibMpv = $includesNativeMedia
        requiredSystemDependencies = if ($includesNativeMedia) { @() } else {
            @('Microsoft.NETCore.App 10.0', 'ffmpeg', 'ffprobe', 'libmpv.so.2')
        }
        complianceBundleIncluded = $GenerateCompliance.IsPresent
        spdxPath = if ($GenerateCompliance) {
            "compliance/ClipEdit-$Version-$releaseAssetId.spdx.json"
        } else { $null }
        correspondingSourcePaths = @($correspondingSourcePaths)
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingPath 'release-manifest.json'),
        ($manifest | ConvertTo-Json -Depth 3) + [Environment]::NewLine)
    $knownReleaseHashes = @{
        $executableName = $hash
    }
    if ($GenerateCompliance) {
        foreach ($line in Get-Content -LiteralPath (Join-Path $compliancePath 'SHA256SUMS')) {
            if ($line -match '^([0-9a-fA-F]{64})  (.+)$') {
                $knownReleaseHashes["compliance/$($Matches[2])"] = $Matches[1].ToLowerInvariant()
            }
        }
    }
    $rootChecksumsPath = Join-Path $stagingPath 'SHA256SUMS'
    $releaseChecksums = Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
        Where-Object FullName -ne $rootChecksumsPath |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($stagingPath.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
            $releaseHash = if ($knownReleaseHashes.ContainsKey($relativePath)) {
                $knownReleaseHashes[$relativePath]
            }
            else {
                (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            "$releaseHash  $relativePath"
        }
    [System.IO.File]::WriteAllText(
        $rootChecksumsPath,
        ($releaseChecksums -join "`n") + "`n",
        (New-Object System.Text.UTF8Encoding($false)))

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fullOutputPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $fullOutputPath)
    if ($singleFile) {
        Copy-Item `
            -LiteralPath (Join-Path $fullOutputPath $executableName) `
            -Destination $releaseAssetPath
        [System.IO.File]::WriteAllText(
            $releaseChecksumPath,
            "$hash  $releaseAssetName`n",
            (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "GitHub release assets are ready at $releaseAssetPath and $releaseChecksumPath"
    }
    Write-Host "ClipEdit $releaseAssetId $BundleMode $ManagedDeployment release candidate is ready at $fullOutputPath"
    if ($GenerateCompliance) {
        Write-Host 'License notices, SPDX SBOM, and corresponding source are assembled.'
    }
    else {
        Write-Warning 'The build is technically packaged but has no release compliance bundle. Use -GenerateCompliance for a publication candidate.'
    }
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
    if (Test-Path -LiteralPath $buildArtifactsPath) {
        [System.IO.Directory]::Delete($buildArtifactsPath, $true)
    }
    if (Test-Path -LiteralPath $complianceBuildPath) {
        [System.IO.Directory]::Delete($complianceBuildPath, $true)
    }
}
