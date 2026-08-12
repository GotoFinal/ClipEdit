using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Probe;

public sealed record OtherStreamInfo : MediaStreamInfo
{
    public OtherStreamInfo(
        int index,
        MediaStreamKind kind,
        string codecName,
        string? codecLongName,
        string? profile,
        string? language,
        string? title,
        bool isDefault,
        bool isForced,
        MediaTime? timeBase,
        MediaTime? startTime,
        MediaTime? duration)
        : base(
            index,
            kind,
            codecName,
            codecLongName,
            profile,
            language,
            title,
            isDefault,
            isForced,
            timeBase,
            startTime,
            duration)
    {
        if (kind is MediaStreamKind.Video or MediaStreamKind.Audio)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Video and audio streams require their specialized metadata types.");
        }
    }
}
