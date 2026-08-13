[CmdletBinding()]
param(
    [string]$OutputPath,

    [string]$CacheVolume = 'clipedit-win-shared-build-v1',

    [string]$BuilderImage = 'clipedit-windows-shared-media:2026-08-13'
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$recipePath = Join-Path $PSScriptRoot 'native/windows-shared-media'
$engPath = $PSScriptRoot
$exportScript = Join-Path $PSScriptRoot 'compliance/Export-WindowsCorrespondingSource.sh'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot 'artifacts/compliance/native/win-x64'
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "The compliance output path already exists: $fullOutputPath"
}

foreach ($requiredPath in @($recipePath, $exportScript)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required compliance input is missing: $requiredPath"
    }
}

& docker version --format '{{.Server.Version}}' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'A running Docker engine is required to export the Windows corresponding source.'
}

$imageExists = $null -ne (& docker image inspect $BuilderImage 2>$null)
if (-not $imageExists) {
    & docker build --tag $BuilderImage $recipePath
    if ($LASTEXITCODE -ne 0) {
        throw "Docker failed to build the pinned native builder image (exit $LASTEXITCODE)."
    }
}

$stagingPath = "$fullOutputPath.staging-$([Guid]::NewGuid().ToString('N'))"
try {
    [System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null
    & docker run --rm `
        --mount "type=volume,source=$CacheVolume,target=/cache" `
        --mount "type=bind,source=$engPath,target=/clipedit-eng,readonly" `
        --mount "type=bind,source=$stagingPath,target=/output" `
        --entrypoint /bin/bash `
        $BuilderImage `
        /clipedit-eng/compliance/Export-WindowsCorrespondingSource.sh `
        /clipedit-eng/native/windows-shared-media/source-lock.tsv `
        /clipedit-eng/native/windows-shared-media/source-exclusions.tsv `
        /clipedit-eng/native/windows-shared-media `
        /output
    if ($LASTEXITCODE -ne 0) {
        throw "The Windows corresponding-source export failed with exit code $LASTEXITCODE."
    }

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fullOutputPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $fullOutputPath)
    Write-Host "Windows corresponding-source package is ready at $fullOutputPath"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
}
