using System.Globalization;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Mpv.Native;

internal static class MpvAudioGraphBuilder
{
    public static string Build(IReadOnlyList<MpvAudioGraphTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            return string.Empty;
        }

        if (tracks.Select(track => track.MpvTrackId).Distinct().Count() != tracks.Count)
        {
            throw new ArgumentException("mpv audio track IDs must be distinct.", nameof(tracks));
        }

        if (tracks.Count == 1)
        {
            var track = tracks[0];
            return $"[aid{track.MpvTrackId}]" +
                   CreateRangeMask(track.AudioEdit) +
                   CreateDelay(track.TimelineOffset) +
                   $"volume={FormatGain(track.GainDb)}dB[ao]";
        }

        var filters = tracks
            .Select((track, index) =>
                $"[aid{track.MpvTrackId}]" +
                CreateRangeMask(track.AudioEdit) +
                CreateDelay(track.TimelineOffset) +
                "aresample=48000," +
                "aformat=sample_fmts=fltp:channel_layouts=stereo," +
                $"volume={FormatGain(track.GainDb)}dB[mix{index}]")
            .ToList();
        filters.Add(
            string.Concat(Enumerable.Range(0, tracks.Count).Select(index => $"[mix{index}]")) +
            $"amix=inputs={tracks.Count}:duration=longest:normalize=0," +
            "alimiter=limit=0.95[ao]");
        return string.Join(';', filters);
    }

    private static string FormatGain(double gainDb)
    {
        if (!double.IsFinite(gainDb) || gainDb is < -60 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb));
        }

        return gainDb.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    private static string CreateDelay(MediaTime timelineOffset)
    {
        if (timelineOffset < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineOffset));
        }

        return timelineOffset == MediaTime.Zero
            ? string.Empty
            : $"adelay=delays={timelineOffset.TotalSeconds.ToString("0.###############", CultureInfo.InvariantCulture)}s:all=1,";
    }

    private static string CreateRangeMask(SourceEdit? audioEdit)
    {
        if (audioEdit is null || audioEdit.IsUnedited)
        {
            return string.Empty;
        }

        if (audioEdit.IsEmpty)
        {
            return "aeval='0':c=same,";
        }

        var keptExpression = string.Join(
            '+',
            audioEdit.KeptRanges.Select(range =>
                $"gte(t,{FormatTime(range.Start)})*lt(t,{FormatTime(range.End)})"));
        return $"aeval='if(gt({keptExpression},0),val(ch),0)':c=same,";
    }

    private static string FormatTime(MediaTime value) =>
        value.TotalSeconds.ToString("0.###############", CultureInfo.InvariantCulture);
}

internal readonly record struct MpvAudioGraphTrack(
    long MpvTrackId,
    double GainDb,
    MediaTime TimelineOffset = default,
    SourceEdit? AudioEdit = null);
