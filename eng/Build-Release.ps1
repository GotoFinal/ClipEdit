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
Assert-ClipEditNativeSourceLock -Dependencies $nativeDependencies

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
$requiredPayload = @('LICENSE.txt', 'licenses/THIRD_PARTY_NOTICES.md')
$requiredPayload += if ($RuntimeId -eq 'win-x64') {
    @($nativeDependencies.windows.requiredBinaries | ForEach-Object { "tools/ffmpeg/$_" })
}
else {
    @('tools/ffmpeg/ffmpeg', 'tools/ffmpeg/ffprobe', 'libmpv.so.2')
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
$releaseAssetName = if ($RuntimeId -eq 'win-x64') {
    'ClipEdit-win-x64.exe'
}
else {
    'ClipEdit-linux-x64'
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
$complianceDepsPath = Join-Path $buildArtifactsPath 'ClipEdit.release.deps.json'
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
        '--configfile', (Join-Path $workspaceRoot 'NuGet.Config'),
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

        $compliancePath = Join-Path $buildArtifactsPath 'compliance'
        $complianceArguments = @{
            RuntimeId = $RuntimeId
            Version = $Version
            ManagedDeployment = $ManagedDeployment
            DepsJsonPath = $complianceDepsPath
            NativePayloadPath = $fullPayloadPath
            OutputPath = $compliancePath
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
        foreach ($payloadItem in Get-ChildItem -LiteralPath $fullPayloadPath -Force) {
            Copy-Item -LiteralPath $payloadItem.FullName `
                -Destination $augmentedPayloadPath `
                -Recurse `
                -Force
        }
        $augmentedLicensePath = Join-Path $augmentedPayloadPath 'licenses'
        [System.IO.Directory]::CreateDirectory($augmentedLicensePath) | Out-Null
        Copy-Item -LiteralPath (Join-Path $compliancePath 'THIRD_PARTY_NOTICES.md') `
            -Destination (Join-Path $augmentedLicensePath 'THIRD_PARTY_NOTICES.md') `
            -Force
        Copy-Item -LiteralPath (Join-Path $compliancePath "ClipEdit-$Version-$RuntimeId.spdx.json") `
            -Destination $augmentedLicensePath
        foreach ($licenseItem in Get-ChildItem -LiteralPath (Join-Path $compliancePath 'licenses') -Force) {
            Copy-Item -LiteralPath $licenseItem.FullName `
                -Destination $augmentedLicensePath `
                -Recurse `
                -Force
        }

        [System.IO.Directory]::Delete($stagingPath, $true)
        [System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null
        $finalPublishArguments = @($publishArguments | ForEach-Object {
            if ($_ -like '-p:ClipEditNativePayloadRoot=*') {
                "-p:ClipEditNativePayloadRoot=$augmentedPayloadPath"
            }
            else {
                $_
            }
        })
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
        nativeMediaProfile = [string]$nativeDependencies.releaseProfiles.$RuntimeId
        includesFFmpeg = $true
        includesLibMpv = $true
        complianceBundleIncluded = $GenerateCompliance.IsPresent
        spdxPath = if ($GenerateCompliance) {
            "compliance/ClipEdit-$Version-$RuntimeId.spdx.json"
        } else { $null }
        correspondingSourcePaths = if ($GenerateCompliance) {
            @(
                "compliance/source/ClipEdit-$Version-source.zip",
                "compliance/source/ClipEdit-$Version-$RuntimeId-native-source.tar.zst")
        } else { @() }
        publiclyRedistributable = $false
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
    [System.IO.File]::WriteAllLines(
        $rootChecksumsPath,
        $releaseChecksums,
        (New-Object System.Text.UTF8Encoding($false)))

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fullOutputPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $fullOutputPath)
    if ($singleFile) {
        Copy-Item `
            -LiteralPath (Join-Path $fullOutputPath $executableName) `
            -Destination $releaseAssetPath
        [System.IO.File]::WriteAllText(
            $releaseChecksumPath,
            "$hash  $releaseAssetName$([Environment]::NewLine)",
            (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "GitHub release assets are ready at $releaseAssetPath and $releaseChecksumPath"
    }
    Write-Host "ClipEdit $RuntimeId $BundleMode $ManagedDeployment release candidate is ready at $fullOutputPath"
    if ($GenerateCompliance) {
        Write-Warning 'License notices, SPDX SBOM, and corresponding source are assembled; public redistribution still requires the manifest gates (including codec/patent, signing, platform, undo/accessibility, and legal review).'
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
}
