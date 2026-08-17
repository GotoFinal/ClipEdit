[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FfmpegPath,

    [Parameter(Mandatory = $true)]
    [string]$FfprobePath,

    [string]$OutputDirectory,

    [switch]$FailOnMismatch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ffmpeg = [IO.Path]::GetFullPath($FfmpegPath)
$ffprobe = [IO.Path]::GetFullPath($FfprobePath)
$resolvedOutputDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $PSScriptRoot '../artifacts/boundary-gop-codec-lab'
} else {
    $OutputDirectory
}
$outputRoot = [IO.Path]::GetFullPath($resolvedOutputDirectory)
foreach ($tool in @($ffmpeg, $ffprobe)) {
    if (-not [IO.File]::Exists($tool)) {
        throw "Media tool does not exist: $tool"
    }
}

[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$runDirectory = Join-Path $outputRoot ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null

function Invoke-MediaTool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [switch]$CaptureOutput
    )

    if ($CaptureOutput) {
        $result = & $Executable @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "$([IO.Path]::GetFileName($Executable)) failed with exit code $LASTEXITCODE`n$($result -join [Environment]::NewLine)"
        }
        return $result
    }

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$([IO.Path]::GetFileName($Executable)) failed with exit code $LASTEXITCODE"
    }
}

function Get-VideoFacts {
    param([Parameter(Mandatory = $true)][string]$Path)

    $json = Invoke-MediaTool -Executable $ffprobe -CaptureOutput -Arguments @(
        '-v', 'error',
        '-select_streams', 'v:0',
        '-count_frames',
        '-show_entries', 'stream=codec_name,width,height,nb_read_frames:format=duration',
        '-of', 'json',
        $Path)
    $probe = ($json -join [Environment]::NewLine) | ConvertFrom-Json
    return [pscustomobject]@{
        Codec = [string]$probe.streams[0].codec_name
        Width = [int]$probe.streams[0].width
        Height = [int]$probe.streams[0].height
        Frames = [int]$probe.streams[0].nb_read_frames
        Duration = [double]::Parse(
            [string]$probe.format.duration,
            [Globalization.CultureInfo]::InvariantCulture)
    }
}

function Get-Keyframes {
    param([Parameter(Mandatory = $true)][string]$Path)

    $json = Invoke-MediaTool -Executable $ffprobe -CaptureOutput -Arguments @(
        '-v', 'error',
        '-select_streams', 'v:0',
        '-show_packets',
        '-show_entries', 'packet=pts_time,dts_time,flags',
        '-of', 'json',
        $Path)
    $probe = ($json -join [Environment]::NewLine) | ConvertFrom-Json
    return @($probe.packets | Where-Object { [string]$_.flags -like 'K*' } | ForEach-Object {
        [pscustomobject]@{
            Pts = [double]::Parse([string]$_.pts_time, [Globalization.CultureInfo]::InvariantCulture)
            Dts = [double]::Parse([string]$_.dts_time, [Globalization.CultureInfo]::InvariantCulture)
        }
    })
}

function Format-Seconds {
    param([double]$Value)
    return $Value.ToString('0.#########', [Globalization.CultureInfo]::InvariantCulture)
}

$codecs = @(
    [pscustomobject]@{
        Name = 'h264'
        Encoder = 'libx264'
        Extension = '.mp4'
        Muxer = 'mp4'
        Arguments = @('-preset', 'veryfast', '-crf', '18', '-g', '60', '-keyint_min', '60', '-sc_threshold', '0', '-bf', '2')
    },
    [pscustomobject]@{
        Name = 'hevc'
        Encoder = 'libx265'
        Extension = '.mp4'
        Muxer = 'mp4'
        Arguments = @('-preset', 'veryfast', '-crf', '20', '-x265-params', 'keyint=60:min-keyint=60:scenecut=0')
    },
    [pscustomobject]@{
        Name = 'vp9'
        Encoder = 'libvpx-vp9'
        Extension = '.webm'
        Muxer = 'webm'
        Arguments = @('-deadline', 'good', '-cpu-used', '5', '-crf', '28', '-b:v', '0', '-g', '60')
    },
    [pscustomobject]@{
        Name = 'av1'
        Encoder = 'libaom-av1'
        Extension = '.webm'
        Muxer = 'webm'
        Arguments = @('-cpu-used', '8', '-crf', '32', '-b:v', '0', '-g', '60', '-row-mt', '1')
    })

$cutStart = 1.2
$cutEnd = 10.3
$results = [Collections.Generic.List[object]]::new()
foreach ($codec in $codecs) {
    $codecDirectory = Join-Path $runDirectory $codec.Name
    [IO.Directory]::CreateDirectory($codecDirectory) | Out-Null
    $source = Join-Path $codecDirectory ("source$($codec.Extension)")
    $lead = Join-Path $codecDirectory ("lead$($codec.Extension)")
    $middle = Join-Path $codecDirectory ("middle$($codec.Extension)")
    $tail = Join-Path $codecDirectory ("tail$($codec.Extension)")
    $manifest = Join-Path $codecDirectory 'pieces.ffconcat'
    $candidate = Join-Path $codecDirectory ("candidate$($codec.Extension)")
    $reference = Join-Path $codecDirectory ("reference$($codec.Extension)")

    try {
        Invoke-MediaTool -Executable $ffmpeg -Arguments (@(
            '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
            '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=30:duration=12',
            '-an', '-pix_fmt', 'yuv420p', '-c:v', $codec.Encoder) +
            $codec.Arguments + @('-f', $codec.Muxer, $source))

        $keyframes = Get-Keyframes -Path $source
        $firstInterior = $keyframes | Where-Object Pts -GT $cutStart | Select-Object -First 1
        $lastInterior = $keyframes | Where-Object Pts -LT $cutEnd | Select-Object -Last 1
        if ($null -eq $firstInterior -or $null -eq $lastInterior -or $firstInterior.Pts -ge $lastInterior.Pts) {
            throw 'The generated source does not contain a useful copied interior GOP range.'
        }

        $leadFilter = "trim=start=$(Format-Seconds $cutStart):end=$(Format-Seconds $firstInterior.Pts),setpts=PTS-STARTPTS"
        $tailFilter = "trim=start=$(Format-Seconds $lastInterior.Pts):end=$(Format-Seconds $cutEnd),setpts=PTS-STARTPTS"
        foreach ($boundary in @(
            [pscustomobject]@{ Path = $lead; Filter = $leadFilter },
            [pscustomobject]@{ Path = $tail; Filter = $tailFilter })) {
            Invoke-MediaTool -Executable $ffmpeg -Arguments (@(
                '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
                '-i', $source,
                '-map', '0:v:0', '-an', '-vf', $boundary.Filter,
                '-fps_mode', 'passthrough', '-enc_time_base', '1:30',
                '-pix_fmt', 'yuv420p', '-c:v', $codec.Encoder) +
                $codec.Arguments + @('-f', $codec.Muxer, $boundary.Path))
        }

        $middleDuration = $lastInterior.Dts - $firstInterior.Pts
        if ($middleDuration -le 0) {
            throw 'The copied interior GOP range has a non-positive decode duration.'
        }
        Invoke-MediaTool -Executable $ffmpeg -Arguments @(
            '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
            '-ss', (Format-Seconds $firstInterior.Pts),
            '-t', (Format-Seconds $middleDuration),
            '-i', $source,
            '-map', '0:v:0', '-an', '-c:v', 'copy',
            '-map_metadata', '-1', '-map_chapters', '-1',
            '-f', $codec.Muxer, $middle)

        $manifestText = "ffconcat version 1.0`n" +
            (@($lead, $middle, $tail) | ForEach-Object {
                "file '$($_.Replace("'", "'\''"))'`n"
            }) -join ''
        [IO.File]::WriteAllText($manifest, $manifestText, [Text.UTF8Encoding]::new($false))
        Invoke-MediaTool -Executable $ffmpeg -Arguments @(
            '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
            '-f', 'concat', '-safe', '0', '-i', $manifest,
            '-map', '0:v:0', '-an', '-c:v', 'copy',
            '-map_metadata', '-1', '-map_chapters', '-1',
            '-f', $codec.Muxer, $candidate)

        $referenceFilter = "trim=start=$(Format-Seconds $cutStart):end=$(Format-Seconds $cutEnd),setpts=PTS-STARTPTS"
        Invoke-MediaTool -Executable $ffmpeg -Arguments (@(
            '-hide_banner', '-loglevel', 'error', '-nostdin', '-y',
            '-i', $source,
            '-map', '0:v:0', '-an', '-vf', $referenceFilter,
            '-fps_mode', 'passthrough', '-enc_time_base', '1:30',
            '-pix_fmt', 'yuv420p', '-c:v', $codec.Encoder) +
            $codec.Arguments + @('-f', $codec.Muxer, $reference))

        Invoke-MediaTool -Executable $ffmpeg -Arguments @(
            '-hide_banner', '-loglevel', 'error', '-xerror', '-nostdin',
            '-i', $candidate, '-map', '0:v:0', '-f', 'null', '-')
        $candidateFacts = Get-VideoFacts -Path $candidate
        $referenceFacts = Get-VideoFacts -Path $reference
        $matches = $candidateFacts.Codec -eq $referenceFacts.Codec -and
            $candidateFacts.Width -eq $referenceFacts.Width -and
            $candidateFacts.Height -eq $referenceFacts.Height -and
            $candidateFacts.Frames -eq $referenceFacts.Frames -and
            [Math]::Abs($candidateFacts.Duration - $referenceFacts.Duration) -le (1.0 / 30.0)
        $results.Add([pscustomobject]@{
            Codec = $codec.Name
            Passed = $matches
            CandidateFrames = $candidateFacts.Frames
            ReferenceFrames = $referenceFacts.Frames
            CandidateDuration = $candidateFacts.Duration
            ReferenceDuration = $referenceFacts.Duration
            Error = if ($matches) { $null } else { 'Decoded, but frame count or duration differs from the exact reference.' }
        })
    }
    catch {
        $results.Add([pscustomobject]@{
            Codec = $codec.Name
            Passed = $false
            CandidateFrames = $null
            ReferenceFrames = $null
            CandidateDuration = $null
            ReferenceDuration = $null
            Error = $_.Exception.Message
        })
    }
}

$reportPath = Join-Path $runDirectory 'results.json'
[IO.File]::WriteAllText(
    $reportPath,
    ($results | ConvertTo-Json -Depth 4),
    [Text.UTF8Encoding]::new($false))
$results | Format-Table -AutoSize
Write-Host "Boundary-GOP codec lab results: $reportPath"

if ($FailOnMismatch -and $results.Where({ -not $_.Passed }).Count -gt 0) {
    exit 1
}
