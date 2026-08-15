[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$RuntimeId = 'win-x64',

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet('FrameworkDependent', 'SelfContained')]
    [string]$ManagedDeployment = 'SelfContained',

    [ValidateSet('Bundled', 'System')]
    [string]$MediaDependencyMode = 'Bundled',

    [string]$ReleaseAssetId,

    [Parameter(Mandatory = $true)]
    [string]$DepsJsonPath,

    [string]$NativePayloadPath,

    [string]$NativeCompliancePath,

    [string]$OutputPath,

    [switch]$AllowDirtySource
)

$ErrorActionPreference = 'Stop'

function Get-SafeSpdxId([string]$Value) {
    return 'SPDXRef-' + ($Value -replace '[^A-Za-z0-9.-]', '-')
}

function Escape-MarkdownCell([string]$Value) {
    return ($Value -replace '\|', '\|').Replace("`r", ' ').Replace("`n", ' ')
}

function Add-ComplianceFile([string]$Source, [string]$Destination) {
    try {
        New-Item -ItemType HardLink -Path $Destination -Target $Source -ErrorAction Stop | Out-Null
    }
    catch {
        Copy-Item -LiteralPath $Source -Destination $Destination
    }
}

function Get-DotNetDistributionRoot {
    $candidateRoots = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
        $candidateRoots.Add($env:DOTNET_ROOT)
    }

    $dotnetCommandPath = (Get-Command dotnet -ErrorAction Stop).Source
    $candidateRoots.Add((Split-Path -Parent $dotnetCommandPath))
    try {
        $resolvedDotnet = [System.IO.File]::ResolveLinkTarget($dotnetCommandPath, $true)
        if ($null -ne $resolvedDotnet) {
            $candidateRoots.Add((Split-Path -Parent $resolvedDotnet.FullName))
        }
    }
    catch {
        Write-Verbose "Could not resolve the dotnet host link '$dotnetCommandPath': $($_.Exception.Message)"
    }

    foreach ($candidateRoot in @($candidateRoots | Select-Object -Unique)) {
        $hasAllNotices = @('LICENSE.txt', 'ThirdPartyNotices.txt') | ForEach-Object {
            Test-Path -LiteralPath (Join-Path $candidateRoot $_) -PathType Leaf
        }
        if ($hasAllNotices -notcontains $false) {
            return $candidateRoot
        }
    }

    $formattedRoots = $candidateRoots -join ', '
    throw "Could not locate the .NET distribution notices. Checked: $formattedRoots"
}

function Get-NuGetGlobalPackagesFolder([string]$WorkingDirectory) {
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = @(& dotnet nuget locals global-packages --list --force-english-output 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0) {
        throw "Could not resolve NuGet's effective global packages folder.`n$($output -join [Environment]::NewLine)"
    }
    $pathLine = [string]($output | Where-Object {
        [string]$_ -match '^global-packages:\s*(.+)\s*$'
    } | Select-Object -Last 1)
    if ($pathLine -notmatch '^global-packages:\s*(.+)\s*$') {
        throw "Could not parse NuGet's effective global packages folder from: $($output -join [Environment]::NewLine)"
    }

    $packageRoot = [System.IO.Path]::GetFullPath($Matches[1].Trim())
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw "NuGet's effective global packages folder does not exist: $packageRoot. Run dotnet restore first."
    }
    return $packageRoot
}

function Get-PackageMetadata([string]$Id, [string]$VersionValue, [string]$PackageRoot) {
    $packageDirectory = Join-Path (Join-Path $PackageRoot $Id.ToLowerInvariant()) $VersionValue
    if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
        throw "NuGet package $Id $VersionValue is missing from the effective global packages folder: $PackageRoot"
    }
    $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File |
        Select-Object -First 1
    if ($null -eq $nuspec) {
        throw "NuGet metadata is missing for $Id $VersionValue at $packageDirectory."
    }

    [xml]$document = Get-Content -LiteralPath $nuspec.FullName
    $metadata = $document.package.metadata
    $licenseNode = $metadata.license
    if ($null -eq $licenseNode) {
        throw "NuGet package $Id $VersionValue does not declare a license expression or file."
    }

    $licenseType = [string]$licenseNode.type
    $licenseValue = [string]$licenseNode.'#text'
    if ([string]::IsNullOrWhiteSpace($licenseType) -or [string]::IsNullOrWhiteSpace($licenseValue)) {
        throw "NuGet package $Id $VersionValue has incomplete license metadata."
    }

    $repositoryUrl = [string]$metadata.repository.url
    if ([string]::IsNullOrWhiteSpace($repositoryUrl)) {
        $repositoryUrl = [string]$metadata.projectUrl
    }
    if ([string]::IsNullOrWhiteSpace($repositoryUrl)) {
        $repositoryUrl = 'NOASSERTION'
    }

    return [pscustomobject]@{
        Id = $Id
        Version = $VersionValue
        PackageDirectory = $packageDirectory
        LicenseType = $licenseType
        LicenseValue = $licenseValue
        RepositoryUrl = $repositoryUrl
        RepositoryCommit = [string]$metadata.repository.commit
        Copyright = if ([string]::IsNullOrWhiteSpace([string]$metadata.copyright)) {
            'NOASSERTION'
        }
        else {
            [string]$metadata.copyright
        }
    }
}

$workspaceRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')
$nativeDependencies = Get-ClipEditNativeDependencies
$depsFullPath = [System.IO.Path]::GetFullPath($DepsJsonPath)
if (-not (Test-Path -LiteralPath $depsFullPath -PathType Leaf)) {
    throw "The exact publish dependency manifest is missing: $depsFullPath"
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a valid semantic version."
}

$includesNativeMedia = $MediaDependencyMode -eq 'Bundled'
if ([string]::IsNullOrWhiteSpace($ReleaseAssetId)) {
    $ReleaseAssetId = if ($includesNativeMedia) { $RuntimeId } else { "$RuntimeId-system" }
}
if ($ReleaseAssetId -notmatch '^[a-z0-9-]+$') {
    throw "Release asset identity '$ReleaseAssetId' is invalid."
}

if ($includesNativeMedia -and [string]::IsNullOrWhiteSpace($NativePayloadPath)) {
    $NativePayloadPath = if ($RuntimeId -eq 'win-x64') {
        Join-Path $workspaceRoot 'packages/native/release/win-x64/shared-media-stack-v2/payload'
    }
    else {
        Join-Path $workspaceRoot "packages/native/release/$RuntimeId/payload"
    }
}
if ($includesNativeMedia -and [string]::IsNullOrWhiteSpace($NativeCompliancePath)) {
    $NativeCompliancePath = Join-Path $workspaceRoot "artifacts/compliance/native/$RuntimeId"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot "artifacts/compliance/$Version/$ReleaseAssetId"
}

$nativePayloadFullPath = if ($includesNativeMedia) {
    [System.IO.Path]::GetFullPath($NativePayloadPath)
} else { $null }
$nativeComplianceFullPath = if ($includesNativeMedia) {
    [System.IO.Path]::GetFullPath($NativeCompliancePath)
} else { $null }
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
if ($includesNativeMedia -and
    -not (Test-Path -LiteralPath $nativePayloadFullPath -PathType Container)) {
    throw "The native payload is missing: $nativePayloadFullPath"
}
if (Test-Path -LiteralPath $outputFullPath) {
    throw "The compliance output path already exists: $outputFullPath"
}

$safeWorkspace = $workspaceRoot.Replace('\', '/')
$commit = (& git -c "safe.directory=$safeWorkspace" rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Could not determine the exact ClipEdit source revision.'
}
if (-not $AllowDirtySource) {
    $dirty = (& git -c "safe.directory=$safeWorkspace" status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not inspect the Git working tree.'
    }
    if ($null -ne $dirty -and @($dirty).Count -gt 0) {
        throw 'Corresponding source must be generated from a clean tracked working tree. Commit the release changes first.'
    }
}

$nativePrefix = if ($RuntimeId -eq 'win-x64') { 'windows' } else { 'linux' }
$nativeSourceArchiveName = "$nativePrefix-native-corresponding-source.tar.zst"
$nativeLicenseArchiveName = "$nativePrefix-native-third-party-licenses.tar.zst"
$nativeProvenanceName = "$nativePrefix-native-source-provenance.tsv"
$nativeLicenseManifestName = "$nativePrefix-native-license-manifest.tsv"
if ($includesNativeMedia) {
    foreach ($name in @(
        $nativeSourceArchiveName,
        $nativeLicenseArchiveName,
        $nativeProvenanceName,
        $nativeLicenseManifestName,
        'SHA256SUMS')) {
        if (-not (Test-Path -LiteralPath (Join-Path $nativeComplianceFullPath $name) -PathType Leaf)) {
            throw "Native compliance input is incomplete. Missing $name under $nativeComplianceFullPath."
        }
    }
}

$requiredNativeFiles = if (-not $includesNativeMedia) {
    @()
}
elseif ($RuntimeId -eq 'win-x64') {
    @($nativeDependencies.windows.requiredBinaries | ForEach-Object { "tools/ffmpeg/$_" })
}
else {
    @('tools/ffmpeg/ffmpeg', 'tools/ffmpeg/ffprobe', 'libmpv.so.2')
}
foreach ($relativePath in $requiredNativeFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $nativePayloadFullPath $relativePath) -PathType Leaf)) {
        throw "The shipped native payload is missing the inventoried file $relativePath."
    }
}

$stagingPath = "$outputFullPath.staging-$([Guid]::NewGuid().ToString('N'))"
try {
    [System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null
    $sourcePath = Join-Path $stagingPath 'source'
    $licensesPath = Join-Path $stagingPath 'licenses'
    [System.IO.Directory]::CreateDirectory($sourcePath) | Out-Null
    [System.IO.Directory]::CreateDirectory($licensesPath) | Out-Null

    $appSourceName = "ClipEdit-$Version-source.zip"
    $appSourcePath = Join-Path $sourcePath $appSourceName
    & git -c "safe.directory=$safeWorkspace" archive --format=zip --output=$appSourcePath $commit
    if ($LASTEXITCODE -ne 0) {
        throw "Could not archive ClipEdit source revision $commit."
    }
    Write-Verbose 'Archived ClipEdit application source.'

    $nativeSourceReleaseName = "ClipEdit-$Version-$RuntimeId-native-source.tar.zst"
    $nativeLicenseReleaseName = "ClipEdit-$Version-$RuntimeId-native-licenses.tar.zst"
    if ($includesNativeMedia) {
        $nativeSourceInputPath = Join-Path $nativeComplianceFullPath $nativeSourceArchiveName
        $nativeLicenseInputPath = Join-Path $nativeComplianceFullPath $nativeLicenseArchiveName
        Add-ComplianceFile $nativeSourceInputPath (Join-Path $sourcePath $nativeSourceReleaseName)
        Add-ComplianceFile $nativeLicenseInputPath (Join-Path $licensesPath $nativeLicenseReleaseName)
        Copy-Item -LiteralPath (Join-Path $nativeComplianceFullPath $nativeProvenanceName) `
            -Destination $licensesPath
        Copy-Item -LiteralPath (Join-Path $nativeComplianceFullPath $nativeLicenseManifestName) `
            -Destination $licensesPath
        Write-Verbose 'Linked native source/license archives and copied native manifests.'
    }

    $dotnetRoot = Get-DotNetDistributionRoot
    foreach ($runtimeNotice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $runtimeNoticePath = Join-Path $dotnetRoot $runtimeNotice
        if (-not (Test-Path -LiteralPath $runtimeNoticePath -PathType Leaf)) {
            throw "The .NET distribution notice is missing: $runtimeNoticePath"
        }
        Copy-Item -LiteralPath $runtimeNoticePath `
            -Destination (Join-Path $licensesPath ".NET-$runtimeNotice")
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'compliance/licenses/MIT.txt') `
        -Destination (Join-Path $licensesPath 'MIT.txt')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'LICENSE') `
        -Destination (Join-Path $licensesPath 'ClipEdit-GPL-3.0.txt')

    $deps = Get-Content -LiteralPath $depsFullPath -Raw | ConvertFrom-Json
    $libraryNames = @($deps.libraries.psobject.Properties.Name)
    if ($libraryNames.Count -eq 0) {
        throw "The dependency manifest has no libraries: $depsFullPath"
    }
    $runtimeLibraryName = @($libraryNames | Where-Object {
        $_ -like "runtimepack.Microsoft.NETCore.App.Runtime.$RuntimeId/*"
    }) | Select-Object -First 1
    if ($ManagedDeployment -eq 'SelfContained' -and [string]::IsNullOrWhiteSpace($runtimeLibraryName)) {
        throw "The exact self-contained .NET runtime pack is missing from $depsFullPath."
    }
    $runtimeVersion = if ([string]::IsNullOrWhiteSpace($runtimeLibraryName)) {
        '10.0.0'
    }
    else {
        $runtimeLibraryName.Split('/')[1]
    }
    $packageRoot = Get-NuGetGlobalPackagesFolder $workspaceRoot
    $managedPackages = @()
    foreach ($libraryName in $libraryNames) {
        $parts = $libraryName.Split('/')
        if ($parts.Count -ne 2 -or
            $parts[0] -like 'ClipEdit*' -or
            $parts[0] -like 'runtimepack.*') {
            continue
        }
        $managedPackages += Get-PackageMetadata $parts[0] $parts[1] $packageRoot
    }
    $managedPackages = @($managedPackages | Sort-Object Id, Version -Unique)
    Write-Verbose "Resolved $($managedPackages.Count) shipped NuGet packages."

    $managedLicenseRoot = Join-Path $licensesPath 'managed-package-files'
    [System.IO.Directory]::CreateDirectory($managedLicenseRoot) | Out-Null
    $extractedLicenses = @()
    foreach ($package in $managedPackages) {
        if ($package.LicenseType -eq 'file') {
            $packageLicensePath = Join-Path $package.PackageDirectory $package.LicenseValue
            if (-not (Test-Path -LiteralPath $packageLicensePath -PathType Leaf)) {
                throw "Declared license file is missing for $($package.Id): $packageLicensePath"
            }
            $destinationName = "$($package.Id)-$($package.Version)-$([System.IO.Path]::GetFileName($package.LicenseValue))"
            Copy-Item -LiteralPath $packageLicensePath `
                -Destination (Join-Path $managedLicenseRoot $destinationName)
            $licenseId = 'LicenseRef-NuGet-' + (($package.Id + '-' + $package.Version) -replace '[^A-Za-z0-9.-]', '-')
            $extractedLicenses += [ordered]@{
                licenseId = $licenseId
                extractedText = Get-Content -LiteralPath $packageLicensePath -Raw
                name = "$($package.Id) $($package.Version) package license"
            }
        }
        elseif ($package.LicenseType -ne 'expression') {
            throw "Unsupported NuGet license metadata type '$($package.LicenseType)' for $($package.Id)."
        }
    }

    $nativeComponents = @()
    if ($includesNativeMedia) {
        $provenanceLines = Get-Content -LiteralPath (Join-Path $nativeComplianceFullPath $nativeProvenanceName)
        foreach ($line in $provenanceLines | Select-Object -Skip 1) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            $parts = $line.Split("`t")
            if ($parts.Count -ne 3) {
                throw "Malformed native provenance row: $line"
            }
            $nativeComponents += [pscustomobject]@{
                Id = $parts[0]
                Version = $parts[1]
                RepositoryUrl = $parts[2]
            }
        }
        if ($nativeComponents.Count -eq 0) {
            throw 'The native provenance manifest contains no components.'
        }
        Write-Verbose "Resolved $($nativeComponents.Count) native source components."
    }

    $creationTimestamp = (& git -c "safe.directory=$safeWorkspace" show -s --format=%cI $commit).Trim()
    $created = ([DateTimeOffset]::Parse($creationTimestamp)).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $documentNamespace = "https://spdx.clipedit.local/$Version/$ReleaseAssetId/$commit"
    $appSpdxId = 'SPDXRef-Package-ClipEdit'
    $packages = @()
    $relationships = @(
        [ordered]@{
            spdxElementId = 'SPDXRef-DOCUMENT'
            relationshipType = 'DESCRIBES'
            relatedSpdxElement = $appSpdxId
        })

    $appSourceHash = (Get-FileHash -LiteralPath $appSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $packages += [ordered]@{
        name = 'ClipEdit'
        SPDXID = $appSpdxId
        versionInfo = $Version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        checksums = @([ordered]@{ algorithm = 'SHA256'; checksumValue = $appSourceHash })
        licenseConcluded = 'GPL-3.0-only'
        licenseDeclared = 'GPL-3.0-only'
        copyrightText = 'NOASSERTION'
        sourceInfo = "Git revision $commit; exact source archive $appSourceName"
    }

    $runtimeSpdxId = 'SPDXRef-Package-dotnet-runtime'
    $packages += [ordered]@{
        name = '.NET Runtime'
        SPDXID = $runtimeSpdxId
        versionInfo = $runtimeVersion
        downloadLocation = 'https://github.com/dotnet/runtime'
        filesAnalyzed = $false
        licenseConcluded = 'MIT'
        licenseDeclared = 'MIT'
        copyrightText = 'Copyright (c) .NET Foundation and Contributors'
        sourceInfo = if ($ManagedDeployment -eq 'SelfContained') {
            'Bundled managed runtime.'
        }
        else {
            'Required host framework; not bundled in this framework-dependent artifact.'
        }
    }
    $relationships += [ordered]@{
        spdxElementId = $appSpdxId
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $runtimeSpdxId
    }

    foreach ($package in $managedPackages) {
        $spdxId = Get-SafeSpdxId "Package-nuget-$($package.Id)-$($package.Version)"
        $license = if ($package.LicenseType -eq 'expression') {
            $package.LicenseValue
        }
        else {
            'LicenseRef-NuGet-' + (($package.Id + '-' + $package.Version) -replace '[^A-Za-z0-9.-]', '-')
        }
        $packages += [ordered]@{
            name = $package.Id
            SPDXID = $spdxId
            versionInfo = $package.Version
            downloadLocation = $package.RepositoryUrl
            filesAnalyzed = $false
            licenseConcluded = $license
            licenseDeclared = $license
            copyrightText = $package.Copyright
            externalRefs = @([ordered]@{
                referenceCategory = 'PACKAGE-MANAGER'
                referenceType = 'purl'
                referenceLocator = "pkg:nuget/$([Uri]::EscapeDataString($package.Id))@$($package.Version)"
            })
        }
        $relationships += [ordered]@{
            spdxElementId = $appSpdxId
            relationshipType = 'DEPENDS_ON'
            relatedSpdxElement = $spdxId
        }
    }

    foreach ($component in $nativeComponents) {
        $spdxId = Get-SafeSpdxId "Package-native-$($component.Id)-$($component.Version)"
        $packages += [ordered]@{
            name = $component.Id
            SPDXID = $spdxId
            versionInfo = $component.Version
            downloadLocation = $component.RepositoryUrl
            filesAnalyzed = $false
            licenseConcluded = 'NOASSERTION'
            licenseDeclared = 'NOASSERTION'
            copyrightText = 'NOASSERTION'
            sourceInfo = "Exact source is included in $nativeSourceReleaseName; raw license and notice files are indexed by $nativeLicenseManifestName."
        }
        $relationships += [ordered]@{
            spdxElementId = $appSpdxId
            relationshipType = 'DEPENDS_ON'
            relatedSpdxElement = $spdxId
        }
    }
    Write-Verbose "Assembled $($packages.Count) SPDX packages and $($relationships.Count) relationships."

    $spdx = [ordered]@{
        spdxVersion = 'SPDX-2.3'
        dataLicense = 'CC0-1.0'
        SPDXID = 'SPDXRef-DOCUMENT'
        name = "ClipEdit-$Version-$ReleaseAssetId"
        documentNamespace = $documentNamespace
        creationInfo = [ordered]@{
            created = $created
            creators = @('Tool: ClipEdit eng/Build-ReleaseCompliance.ps1')
        }
        documentDescribes = @($appSpdxId)
        packages = $packages
        relationships = $relationships
        hasExtractedLicensingInfos = $extractedLicenses
    }
    $spdxPath = Join-Path $stagingPath "ClipEdit-$Version-$ReleaseAssetId.spdx.json"
    [System.IO.File]::WriteAllText(
        $spdxPath,
        ($spdx | ConvertTo-Json -Depth 6) + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Verbose 'Wrote SPDX 2.3 JSON.'

    $noticeLines = New-Object System.Collections.Generic.List[string]
    $noticeLines.Add('# ClipEdit third-party notices')
    $noticeLines.Add('')
    $noticeLines.Add(('Release: `{0}` for `{1}`; source revision `{2}`.' -f $Version, $ReleaseAssetId, $commit))
    $noticeLines.Add('')
    $noticeLines.Add($(if ($includesNativeMedia) {
        'ClipEdit is distributed under GNU GPL version 3. The complete application license is included as `licenses/ClipEdit-GPL-3.0.txt`. The release source directory contains the exact ClipEdit and native corresponding-source archives; the SBOM names every managed and native component known to this build.'
    } else {
        'ClipEdit is distributed under GNU GPL version 3. The complete application license is included as `licenses/ClipEdit-GPL-3.0.txt`. The release source directory contains the exact ClipEdit source archive; the SBOM names the managed components included in this build.'
    }))
    $noticeLines.Add('')
    $noticeLines.Add($(if ($includesNativeMedia -and $RuntimeId -eq 'win-x64') {
        'The Windows release redistributes reviewed MSYS2 UCRT64 FFmpeg and mpv packages. mpv/libmpv dynamically links to the same shared libav DLLs used by ffmpeg and ffprobe. Exact package versions, corresponding MSYS2 source packages, available installed license files, capability reports, and the assembly recipe are preserved in the native source and license archives.'
    } elseif ($includesNativeMedia) {
        'FFmpeg is built with `--enable-gpl --enable-version3` and GPL libraries including x264, so the shipped FFmpeg stack is treated as GPLv3. mpv/libmpv is dynamically linked to that shared stack. Exact configure output, source revisions, license files, and build recipes are preserved in the native source and license archives.'
    } else {
        'This system-dependencies artifact does not distribute FFmpeg, ffprobe, libmpv, or the .NET runtime. They must be installed separately and retain the license terms of the user-selected system packages.'
    }))
    $noticeLines.Add('')
    $noticeLines.Add('## Managed packages')
    $noticeLines.Add('')
    $noticeLines.Add('| Package | Version | License | Copyright/source |')
    $noticeLines.Add('|---|---:|---|---|')
    foreach ($package in $managedPackages) {
        $license = if ($package.LicenseType -eq 'expression') {
            $package.LicenseValue
        }
        else {
            "file: $($package.LicenseValue)"
        }
        $copyrightAndSource = "$($package.Copyright); $($package.RepositoryUrl)"
        $noticeLines.Add("| $(Escape-MarkdownCell $package.Id) | $(Escape-MarkdownCell $package.Version) | $(Escape-MarkdownCell $license) | $(Escape-MarkdownCell $copyrightAndSource) |")
    }
    $noticeLines.Add('')
    $noticeLines.Add('The canonical MIT terms are in `licenses/MIT.txt`; package-specific license files are under `licenses/managed-package-files`. The .NET runtime license and full Microsoft third-party notices are included in this directory.')
    $noticeLines.Add('')
    if ($includesNativeMedia) {
        $noticeLines.Add('## Native components')
        $noticeLines.Add('')
        $noticeLines.Add('| Component | Version or revision | Upstream/source |')
        $noticeLines.Add('|---|---|---|')
        foreach ($component in $nativeComponents) {
            $noticeLines.Add("| $(Escape-MarkdownCell $component.Id) | $(Escape-MarkdownCell $component.Version) | $(Escape-MarkdownCell $component.RepositoryUrl) |")
        }
        $noticeLines.Add('')
        $noticeLines.Add("The byte-for-byte collected native notices are in `licenses/$nativeLicenseReleaseName`; `$nativeLicenseManifestName` maps each component to its preserved files. Exact buildable sources and ClipEdit's build recipe are in `source/$nativeSourceReleaseName`.")
    }
    $noticePath = Join-Path $stagingPath 'THIRD_PARTY_NOTICES.md'
    [System.IO.File]::WriteAllLines($noticePath, $noticeLines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Verbose 'Wrote consolidated third-party notices.'

    $knownOutputHashes = @{}
    if ($includesNativeMedia) {
        $nativeInputHashes = @{}
        foreach ($line in Get-Content -LiteralPath (Join-Path $nativeComplianceFullPath 'SHA256SUMS')) {
            if ($line -match '^([0-9a-fA-F]{64})  (.+)$') {
                $nativeInputHashes[$Matches[2]] = $Matches[1].ToLowerInvariant()
            }
        }
        foreach ($requiredHashName in @($nativeSourceArchiveName, $nativeLicenseArchiveName)) {
            if (-not $nativeInputHashes.ContainsKey($requiredHashName)) {
                throw "Native compliance checksum is missing for $requiredHashName."
            }
        }
        $knownOutputHashes["source/$nativeSourceReleaseName"] = $nativeInputHashes[$nativeSourceArchiveName]
        $knownOutputHashes["licenses/$nativeLicenseReleaseName"] = $nativeInputHashes[$nativeLicenseArchiveName]
    }

    $checksums = Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($stagingPath.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
            $fileHash = if ($knownOutputHashes.ContainsKey($relativePath)) {
                $knownOutputHashes[$relativePath]
            }
            else {
                (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            "$fileHash  $relativePath"
        }
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingPath 'SHA256SUMS'),
        ($checksums -join "`n") + "`n",
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Verbose 'Wrote release compliance checksums.'

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $outputFullPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $outputFullPath)
    Write-Host "ClipEdit $ReleaseAssetId release compliance bundle is ready at $outputFullPath"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
}
