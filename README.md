# ClipEdit
ClipEdit is simple app for quick and dirty video file edits, main focus is on creation of short clips, 
maybe you recorded your gameplay and want to quickly share small part of it with friend etc.  
Main goal is that video can be super quickly cropped and cut without needing to know beforehand what kind of resolution you want - 
that most other video editors enforce. You just open it, select the area on video, select time range, and you are ready to export.  
You can export to file, clipboard, or both.  

There are extra more advanced features, that you don't even need to look at, or think about, for a bit more advanced edits:
- muting parts of the video
- combining multiple clips
- changing audio gain on each clip
- rotating/scaling of each clip
- removing other audio tracks

Disclaimer: This project was developed with heavy use of AI, 
I was just too annoyed to the point i was recording my clips by using ShareX to record my screen as I play the video,
as it was faster and more convenient than all existing video editing apps.   
So I slopped this app in 2 days and can finally forget about all that pain.

I'm not really interested in supporting this app much, but it's here if you need it. Feel free to report any issues, I might even look at them.

## Build and run on Windows
ClipEdit requires the .NET 10 SDK and 7-Zip. It uses FFmpeg for all video operations and LibMpv for the previews. The UI is handled by Avalonia.

There are slopped scripts to fetch some dependencies: 
```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Get-LibMpv.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Get-FFmpeg.ps1
dotnet build ClipEdit.sln
dotnet run --project .\src\ClipEdit.App -- .\path\to\video.mkv
```

To do self-contained Windows x64 development bundle:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\Build-WindowsDevelopment.ps1
```

## Build release candidates
The default release-candidate build is a self-contained executable containing all dependencies:

```powershell
.\eng\Build-Release.ps1 -RuntimeId win-x64 -Version 0.1.0
.\eng\Build-Release.ps1 -RuntimeId linux-x64 -Version 0.1.0
```

The Linux version currently runs under WSL2 and I have no idea if it even works, good luck. You might need to run `eng/Install-LinuxBuildDependencies.sh` first to get other dependencies.

## Tests
Some of the tests require extra video/audio files that are not included in the repository, maybe I could generate them, but I kinda don't care and used whatever I had on my PC.  
For full testing you will need:  
- FFmpeg and FFprobe (provided via PATH or variable)
- libmpv for preview integration tests
- Local test media:
    - Video with an audio stream
    - Separate audio file
    - Video containing at least two audio tracks

```powershell
$env:CLIPEDIT_FFMPEG_PATH = "C:\Apps\ffmpeg\bin\ffmpeg.exe"
$env:CLIPEDIT_FFPROBE_PATH = "C:\Apps\ffmpeg\bin\ffprobe.exe"
$env:CLIPEDIT_LIBMPV_PATH = "C:\path\to\mpv-2.dll"

$env:CLIPEDIT_LOCAL_MEDIA = "C:\path\to\example.mkv"
$env:CLIPEDIT_LOCAL_EXTERNAL_AUDIO = "C:\path\to\audio.flac"
$env:CLIPEDIT_LOCAL_MULTI_AUDIO = "C:\path\to\video-with-two-audio-tracks.mkv"
```

`CLIPEDIT_LOCAL_MEDIA` should contain video and audio. Tests assume audio stream index `1` by default. Can be overriden:

```powershell
$env:CLIPEDIT_LOCAL_AUDIO_STREAM = "2"
```