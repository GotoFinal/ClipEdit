[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$RuntimeId = 'win-x64',

    [string]$OutputPath,

    [string]$WslDistribution = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'NativeDependencies.ps1')
$nativeDependencies = Get-ClipEditNativeDependencies
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = if ($RuntimeId -eq 'win-x64') {
        Join-Path $workspaceRoot 'packages/native/release/win-x64/shared-media-stack-v2/payload'
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
        $linuxWorkspace = (& wsl.exe -d $WslDistribution --exec wslpath -a $workspaceRoot).Trim()
        $linuxOutput = (& wsl.exe -d $WslDistribution --exec wslpath -a $fullOutputPath).Trim()
        if ($LASTEXITCODE -ne 0 -or
            [string]::IsNullOrWhiteSpace($linuxWorkspace) -or
            [string]::IsNullOrWhiteSpace($linuxOutput)) {
            throw "Could not map the workspace into WSL distribution '$WslDistribution'."
        }

        & wsl.exe -d $WslDistribution --exec bash `
            "$linuxWorkspace/eng/Prepare-LinuxReleasePayload.sh" `
            $linuxOutput
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Linux payload preparation failed with exit code $LASTEXITCODE."
    }

    return
}

$nativeStackRoot = Get-ClipEditWindowsNativeStackPath `
    -WorkspaceRoot $workspaceRoot `
    -Dependencies $nativeDependencies
$nativeBinPath = Join-Path $nativeStackRoot 'bin'
$nativeNames = @($nativeDependencies.windows.requiredBinaries)
$nativePaths = @($nativeNames | ForEach-Object { Join-Path $nativeBinPath $_ })

$missingNativeFiles = @($nativePaths | Where-Object {
    -not (Test-Path -LiteralPath $_ -PathType Leaf)
})
if ($missingNativeFiles.Count -gt 0) {
    & (Join-Path $PSScriptRoot 'Build-WindowsSharedMediaStack.ps1') -OutputPath $nativeStackRoot
    if ($LASTEXITCODE -ne 0) {
        throw "The Windows shared media stack build failed with exit code $LASTEXITCODE."
    }
}
foreach ($requiredPath in $nativePaths) {
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

    Copy-Item -Path (Join-Path $nativeBinPath '*') -Destination $toolPath
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'LICENSE') -Destination (Join-Path $stagingPath 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'THIRD_PARTY_NOTICES.md') -Destination $licensePath
    Copy-Item -Path (Join-Path $nativeStackRoot 'licenses/*') -Destination $licensePath -Recurse
    foreach ($metadataName in @(
        'LICENSE-MANIFEST.tsv',
        'PACKAGES-WITHOUT-INSTALLED-LICENSE.tsv',
        'MSYS2-PACKAGES.tsv',
        'NATIVE-STACK.txt',
        'PE-IMPORTS.tsv')) {
        Copy-Item -LiteralPath (Join-Path $nativeStackRoot $metadataName) -Destination $licensePath
    }
    Copy-Item -LiteralPath (Join-Path $nativeStackRoot 'capabilities') `
        -Destination (Join-Path $licensePath 'capabilities') `
        -Recurse

    $checksums = Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($stagingPath.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relativePath"
        }
    [System.IO.File]::WriteAllText(
        (Join-Path $licensePath 'PAYLOAD-SHA256SUMS'),
        ($checksums -join "`n") + "`n",
        (New-Object System.Text.UTF8Encoding($false)))

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fullOutputPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $fullOutputPath)
    Write-Host "ClipEdit $RuntimeId native payload is ready at $fullOutputPath"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
}
