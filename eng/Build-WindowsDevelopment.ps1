[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$ffmpegVersion = '9.0.1'
$mpvReleaseTag = '2026-08-11-f4d13e1c2c'
$ffmpegRoot = Join-Path $workspaceRoot "packages/native/ffmpeg/win-x64/$ffmpegVersion/runtime/ffmpeg-$ffmpegVersion-full_build"
$mpvRoot = Join-Path $workspaceRoot "packages/native/libmpv/win-x64/$mpvReleaseTag/runtime"
$ffmpegPath = Join-Path $ffmpegRoot 'bin/ffmpeg.exe'
$ffprobePath = Join-Path $ffmpegRoot 'bin/ffprobe.exe'
$libMpvPath = Join-Path $mpvRoot 'libmpv-2.dll'

if (-not (Test-Path -LiteralPath $ffmpegPath)) {
    & (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1')
}

if (-not (Test-Path -LiteralPath $libMpvPath)) {
    & (Join-Path $PSScriptRoot 'Get-LibMpv.ps1')
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $OutputPath = Join-Path $workspaceRoot "artifacts/development/ClipEdit-win-x64-$stamp"
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $fullOutputPath) {
    throw "The output path already exists: $fullOutputPath"
}

$stagingRoot = Join-Path $workspaceRoot 'artifacts/.staging'
$stagingPath = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($stagingPath) | Out-Null

try {
    $projectPath = Join-Path $workspaceRoot 'src/ClipEdit.App/ClipEdit.App.csproj'
    & dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $stagingPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $toolPath = Join-Path $stagingPath 'tools/ffmpeg'
    $licensePath = Join-Path $stagingPath 'licenses'
    [System.IO.Directory]::CreateDirectory($toolPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($licensePath) | Out-Null

    Copy-Item -LiteralPath $ffmpegPath -Destination (Join-Path $toolPath 'ffmpeg.exe')
    Copy-Item -LiteralPath $ffprobePath -Destination (Join-Path $toolPath 'ffprobe.exe')
    Copy-Item -LiteralPath $libMpvPath -Destination (Join-Path $stagingPath 'libmpv-2.dll')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'LICENSE') -Destination (Join-Path $stagingPath 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'THIRD_PARTY_NOTICES.md') -Destination $licensePath
    Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'LICENSE') -Destination (Join-Path $licensePath 'FFmpeg-GPL-3.0.txt')
    Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'README.txt') -Destination (Join-Path $licensePath 'FFmpeg-build-README.txt')

    $checksums = Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($stagingPath.Length).TrimStart([char]'\').Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relativePath"
        }
    [System.IO.File]::WriteAllLines((Join-Path $stagingPath 'SHA256SUMS'), $checksums)

    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $fullOutputPath)) | Out-Null
    [System.IO.Directory]::Move($stagingPath, $fullOutputPath)
    Write-Host "ClipEdit Windows x64 development bundle is ready at $fullOutputPath"
    Write-Warning 'This development bundle is not cleared for public redistribution; see licenses/THIRD_PARTY_NOTICES.md.'
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        [System.IO.Directory]::Delete($stagingPath, $true)
    }
}
