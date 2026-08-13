[CmdletBinding()]
param(
    [string]$NativePayloadPath,

    [string]$OutputPath,

    [string]$WslDistribution = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($NativePayloadPath)) {
    $NativePayloadPath = Join-Path $workspaceRoot 'packages/native/release/linux-x64/payload'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $workspaceRoot 'artifacts/compliance/native/linux-x64'
}

$payloadFullPath = [System.IO.Path]::GetFullPath($NativePayloadPath)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $payloadFullPath -PathType Container)) {
    throw "The Linux native payload is missing: $payloadFullPath"
}
if (Test-Path -LiteralPath $outputFullPath) {
    throw "The compliance output path already exists: $outputFullPath"
}

$hostIsLinux = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Linux)
if ($hostIsLinux) {
    & bash (Join-Path $PSScriptRoot 'compliance/Export-LinuxCorrespondingSource.sh') `
        $payloadFullPath $PSScriptRoot $outputFullPath
}
else {
    $linuxWorkspace = (& wsl.exe -d $WslDistribution --exec wslpath -a $workspaceRoot).Trim()
    $linuxPayload = (& wsl.exe -d $WslDistribution --exec wslpath -a $payloadFullPath).Trim()
    $linuxOutput = (& wsl.exe -d $WslDistribution --exec wslpath -a $outputFullPath).Trim()
    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($linuxWorkspace) -or
        [string]::IsNullOrWhiteSpace($linuxPayload) -or
        [string]::IsNullOrWhiteSpace($linuxOutput)) {
        throw "Could not map compliance paths into WSL distribution '$WslDistribution'."
    }

    & wsl.exe -d $WslDistribution --exec bash `
        "$linuxWorkspace/eng/compliance/Export-LinuxCorrespondingSource.sh" `
        $linuxPayload `
        "$linuxWorkspace/eng" `
        $linuxOutput
}

if ($LASTEXITCODE -ne 0) {
    throw "Linux corresponding-source export failed with exit code $LASTEXITCODE."
}
Write-Host "Linux corresponding-source package is ready at $outputFullPath"
