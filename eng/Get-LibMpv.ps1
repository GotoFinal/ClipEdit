[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$releaseTag = '2026-08-11-f4d13e1c2c'
$archiveName = 'mpv-dev-lgpl-x86_64-20260811-git-f4d13e1c2c.7z'
$expectedSha256 = '89b308808753cd740a0d25984d6de8e51ac2fd8af65edc09cda3e03e40df8d5c'
$downloadUri = "https://github.com/zhongfly/mpv-winbuild/releases/download/$releaseTag/$archiveName"

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $workspaceRoot "packages/native/libmpv/win-x64/$releaseTag"
$archivePath = Join-Path $packageRoot $archiveName
$partialPath = "$archivePath.download"
$runtimePath = Join-Path $packageRoot 'runtime'

[System.IO.Directory]::CreateDirectory($packageRoot) | Out-Null

if (Test-Path -LiteralPath $archivePath) {
    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "Existing libmpv archive has SHA-256 $actualSha256; expected $expectedSha256."
    }
}
else {
    try {
        Invoke-WebRequest -Uri $downloadUri -OutFile $partialPath
        $actualSha256 = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualSha256 -ne $expectedSha256) {
            throw "Downloaded libmpv archive has SHA-256 $actualSha256; expected $expectedSha256."
        }

        [System.IO.File]::Move($partialPath, $archivePath)
    }
    finally {
        if (Test-Path -LiteralPath $partialPath) {
            [System.IO.File]::Delete($partialPath)
        }
    }
}

$sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue
if ($null -eq $sevenZip) {
    $bundledSevenZip = 'C:\Apps\7-Zip\7z.exe'
    if (Test-Path -LiteralPath $bundledSevenZip) {
        $sevenZip = Get-Item -LiteralPath $bundledSevenZip
    }
}

if ($null -eq $sevenZip) {
    throw '7z.exe is required to extract libmpv. Install 7-Zip or add 7z.exe to PATH.'
}

[System.IO.Directory]::CreateDirectory($runtimePath) | Out-Null
$sevenZipPath = if ($sevenZip -is [System.IO.FileInfo]) { $sevenZip.FullName } else { $sevenZip.Source }
& $sevenZipPath x $archivePath "-o$runtimePath" -y | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "7-Zip failed with exit code $LASTEXITCODE."
}

$requiredFiles = @(
    'libmpv-2.dll',
    'libmpv.dll.a',
    'include/mpv/client.h',
    'include/mpv/render.h',
    'include/mpv/render_gl.h'
)

foreach ($relativePath in $requiredFiles) {
    $requiredPath = Join-Path $runtimePath $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "The extracted package is missing $relativePath."
    }
}

Write-Host "libmpv $releaseTag is ready at $runtimePath"
