[CmdletBinding()]
param(
    [string]$OutputPath,

    [string]$Msys2Root = $env:MSYS2_ROOT,

    [string]$SourceCachePath
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'The MSYS2 Windows corresponding-source package must be assembled on Windows.'
}

$workspaceRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')
$nativeDependencies = Get-ClipEditNativeDependencies
$nativeStackRoot = Get-ClipEditWindowsNativeStackPath `
    -WorkspaceRoot $workspaceRoot `
    -Dependencies $nativeDependencies
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot 'artifacts/compliance/native/win-x64'
}
if ([string]::IsNullOrWhiteSpace($Msys2Root)) {
    $Msys2Root = 'C:\msys64'
}
if ([string]::IsNullOrWhiteSpace($SourceCachePath)) {
    $SourceCachePath = Join-Path $workspaceRoot 'packages/native/source-cache/win-x64'
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$fullMsys2Root = [System.IO.Path]::GetFullPath($Msys2Root)
$fullSourceCachePath = [System.IO.Path]::GetFullPath($SourceCachePath)
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "The compliance output path already exists: $fullOutputPath"
}

$packageManifestPath = Join-Path $nativeStackRoot 'MSYS2-PACKAGES.tsv'
$licenseManifestPath = Join-Path $nativeStackRoot 'LICENSE-MANIFEST.tsv'
$missingLicenseManifestPath = Join-Path $nativeStackRoot 'PACKAGES-WITHOUT-INSTALLED-LICENSE.tsv'
$licensesPath = Join-Path $nativeStackRoot 'licenses'
$tarPath = Join-Path $fullMsys2Root 'usr/bin/tar.exe'
$cygpathPath = Join-Path $fullMsys2Root 'usr/bin/cygpath.exe'
$curlPath = Join-Path $fullMsys2Root 'usr/bin/curl.exe'
$zstdPath = Join-Path $fullMsys2Root 'usr/bin/zstd.exe'
foreach ($requiredPath in @(
    $packageManifestPath,
    $licenseManifestPath,
    $missingLicenseManifestPath,
    $licensesPath,
    $tarPath,
    $cygpathPath,
    $curlPath,
    $zstdPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required Windows source-package input is missing: $requiredPath"
    }
}

function Get-WebText([string]$Uri) {
    $output = @(& $curlPath --fail --silent --show-error --location --retry 3 $Uri 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not download $Uri.`n$($output -join [Environment]::NewLine)"
    }
    return $output -join "`n"
}

function ConvertTo-MsysPath([string]$Path) {
    $result = (& $cygpathPath -u ([System.IO.Path]::GetFullPath($Path))).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($result)) {
        throw "Could not convert the path for MSYS2 tar: $Path"
    }
    return $result
}

function Get-Msys2SourcePackageUrl([string]$PackageName, [string]$PackageVersion) {
    $packagePage = "https://packages.msys2.org/packages/$([Uri]::EscapeDataString($PackageName))"
    $content = Get-WebText $packagePage
    $pattern = 'https://mirror\.msys2\.org/(?:mingw|msys)/sources/[^"''<>\s]+\.src\.tar\.zst'
    $matches = @([regex]::Matches($content, $pattern) | ForEach-Object Value | Sort-Object -Unique)
    if ($matches.Count -ne 1) {
        throw "Could not resolve one corresponding-source archive for $PackageName $PackageVersion from $packagePage."
    }
    $sourceUrl = [System.Net.WebUtility]::HtmlDecode($matches[0])
    $sourceName = [Uri]::UnescapeDataString(
        [System.IO.Path]::GetFileName(([Uri]$sourceUrl).AbsolutePath))
    if ($sourceName -notmatch "-$([regex]::Escape($PackageVersion))\.src\.tar\.zst$") {
        throw "MSYS2 now publishes $sourceName for $PackageName, but the shipped runtime is $PackageVersion. Run the manual native dependency update instead of mixing package revisions."
    }
    return $sourceUrl
}

$packages = @(Import-Csv -LiteralPath $packageManifestPath -Delimiter "`t")
if ($packages.Count -eq 0) {
    throw "The shipped MSYS2 package manifest is empty: $packageManifestPath"
}

$stagingPath = "$fullOutputPath.staging-$([Guid]::NewGuid().ToString('N'))"
try {
    $sourceArchiveRoot = Join-Path $stagingPath 'ClipEdit-windows-native-corresponding-source'
    $sourcePackagesPath = Join-Path $sourceArchiveRoot 'sources'
    $recipePath = Join-Path $sourceArchiveRoot 'build-recipe'
    $licenseArchiveRoot = Join-Path $stagingPath 'ClipEdit-windows-native-third-party-licenses'
    [System.IO.Directory]::CreateDirectory($sourcePackagesPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($recipePath) | Out-Null
    [System.IO.Directory]::CreateDirectory($licenseArchiveRoot) | Out-Null

    $provenance = [Collections.Generic.List[string]]::new()
    $provenance.Add("component`trevision`torigin")
    $packageSources = [Collections.Generic.List[object]]::new()
    $sourceUrlsByName = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    Write-Host "Resolving corresponding source for $($packages.Count) redistributed MSYS2 packages..."
    foreach ($package in @($packages | Sort-Object package)) {
        $sourceUrl = Get-Msys2SourcePackageUrl $package.package $package.version
        $sourceName = [Uri]::UnescapeDataString(
            [System.IO.Path]::GetFileName(([Uri]$sourceUrl).AbsolutePath))
        if ($sourceUrlsByName.ContainsKey($sourceName) -and
            $sourceUrlsByName[$sourceName] -ne $sourceUrl) {
            throw "Different MSYS2 source URLs resolved to the same archive name: $sourceName"
        }
        $sourceUrlsByName[$sourceName] = $sourceUrl
        $packageSources.Add([pscustomobject]@{
            Package = $package.package
            Version = $package.version
            Url = $sourceUrl
            Name = $sourceName
        })
    }

    [System.IO.Directory]::CreateDirectory($fullSourceCachePath) | Out-Null
    $missingSources = @($sourceUrlsByName.GetEnumerator() | Where-Object {
        $cachedPath = Join-Path $fullSourceCachePath $_.Key
        -not (Test-Path -LiteralPath $cachedPath -PathType Leaf) -or
            (Get-Item -LiteralPath $cachedPath).Length -eq 0
    })
    if ($missingSources.Count -gt 0) {
        Write-Host "Downloading $($missingSources.Count) unique MSYS2 source archives in parallel; $($sourceUrlsByName.Count - $missingSources.Count) restored from cache..."
        $msysCachePath = ConvertTo-MsysPath $fullSourceCachePath
        $curlArguments = @(
            '--parallel',
            '--parallel-immediate',
            '--parallel-max', '8',
            '--fail',
            '--show-error',
            '--location',
            '--retry', '3',
            '--remove-on-error',
            '--output-dir', $msysCachePath
        )
        foreach ($missingSource in $missingSources) {
            $curlArguments += @('--output', $missingSource.Key, '--url', $missingSource.Value)
        }
        & $curlPath @curlArguments
        if ($LASTEXITCODE -ne 0) {
            foreach ($missingSource in $missingSources) {
                Remove-Item -LiteralPath (Join-Path $fullSourceCachePath $missingSource.Key) `
                    -Force -ErrorAction SilentlyContinue
            }
            throw 'One or more MSYS2 corresponding-source archives could not be downloaded.'
        }
    }

    $sourceHashes = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in $packageSources) {
        $cachedPath = Join-Path $fullSourceCachePath $source.Name
        if (-not (Test-Path -LiteralPath $cachedPath -PathType Leaf) -or
            (Get-Item -LiteralPath $cachedPath).Length -eq 0) {
            throw "MSYS2 returned an empty corresponding-source archive: $($source.Url)"
        }
        if (-not $sourceHashes.ContainsKey($source.Name)) {
            $sourceHashes[$source.Name] = (Get-FileHash -LiteralPath $cachedPath -Algorithm SHA256).Hash.ToLowerInvariant()
            Copy-Item -LiteralPath $cachedPath -Destination (Join-Path $sourcePackagesPath $source.Name)
        }
        $provenance.Add("$($source.Package)`t$($source.Version)`t$($source.Url)#sha256=$($sourceHashes[$source.Name])")
    }

    $provenancePath = Join-Path $sourceArchiveRoot 'SOURCE-PROVENANCE.tsv'
    [System.IO.File]::WriteAllLines(
        $provenancePath,
        $provenance,
        (New-Object System.Text.UTF8Encoding($false)))
    Copy-Item -LiteralPath $packageManifestPath -Destination (Join-Path $sourceArchiveRoot 'MSYS2-PACKAGES.tsv')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native/native-dependencies.json') -Destination $recipePath
    foreach ($scriptName in @(
        'Build-WindowsSharedMediaStack.ps1',
        'Export-WindowsCorrespondingSource.ps1',
        'NativeDependencies.ps1',
        'Prepare-ReleasePayload.ps1',
        'Test-NativeDependencies.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination $recipePath
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $sourceArchiveRoot 'README.txt'),
        "This archive contains the exact MSYS2 source packages and ClipEdit assembly recipe corresponding to every package that owns a redistributed Windows media runtime file.`n",
        (New-Object System.Text.UTF8Encoding($false)))

    Copy-Item -Path (Join-Path $licensesPath '*') -Destination $licenseArchiveRoot -Recurse
    Copy-Item -LiteralPath $licenseManifestPath -Destination $licenseArchiveRoot
    Copy-Item -LiteralPath $missingLicenseManifestPath -Destination $licenseArchiveRoot
    Copy-Item -LiteralPath $packageManifestPath -Destination $licenseArchiveRoot

    [System.IO.Directory]::CreateDirectory($fullOutputPath) | Out-Null
    $sourceArchive = Join-Path $fullOutputPath 'windows-native-corresponding-source.tar.zst'
    $licenseArchive = Join-Path $fullOutputPath 'windows-native-third-party-licenses.tar.zst'
    $oldPath = $env:PATH
    try {
        $env:PATH = "$(Join-Path $fullMsys2Root 'usr/bin');$oldPath"
        $stagingMsysPath = ConvertTo-MsysPath $stagingPath
        $sourceArchiveMsysPath = ConvertTo-MsysPath $sourceArchive
        $licenseArchiveMsysPath = ConvertTo-MsysPath $licenseArchive
        & $tarPath --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner `
            --use-compress-program=/usr/bin/zstd -cf $sourceArchiveMsysPath -C $stagingMsysPath `
            'ClipEdit-windows-native-corresponding-source'
        if ($LASTEXITCODE -ne 0) {
            throw "MSYS2 tar could not create $sourceArchive."
        }
        & $tarPath --sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner `
            --use-compress-program=/usr/bin/zstd -cf $licenseArchiveMsysPath -C $stagingMsysPath `
            'ClipEdit-windows-native-third-party-licenses'
        if ($LASTEXITCODE -ne 0) {
            throw "MSYS2 tar could not create $licenseArchive."
        }
    }
    finally {
        $env:PATH = $oldPath
    }

    Copy-Item -LiteralPath $provenancePath `
        -Destination (Join-Path $fullOutputPath 'windows-native-source-provenance.tsv')
    Copy-Item -LiteralPath $licenseManifestPath `
        -Destination (Join-Path $fullOutputPath 'windows-native-license-manifest.tsv')
    $checksums = Get-ChildItem -LiteralPath $fullOutputPath -File |
        Sort-Object Name |
        ForEach-Object {
            "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
        }
    [System.IO.File]::WriteAllText(
        (Join-Path $fullOutputPath 'SHA256SUMS'),
        ($checksums -join "`n") + "`n",
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Windows MSYS2 corresponding-source package is ready at $fullOutputPath"
}
catch {
    if (Test-Path -LiteralPath $fullOutputPath) {
        [System.IO.Directory]::Delete($fullOutputPath, $true)
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
}
