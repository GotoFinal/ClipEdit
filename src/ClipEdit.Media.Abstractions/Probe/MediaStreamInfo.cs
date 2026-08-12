using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Probe;

public abstract record MediaStreamInfo
{
    protected MediaStreamInfo(
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
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(codecName);

        Index = index;
        Kind = kind;
        CodecName = codecName;
        CodecLongName = codecLongName;
        Profile = profile;
        Language = language;
        Title = title;
        IsDefault = isDefault;
        IsForced = isForced;
        TimeBase = timeBase;
        StartTime = startTime;
        Duration = duration;
    }

    public int Index { get; }

    public MediaStreamKind Kind { get; }

    public string CodecName { get; }

    public string? CodecLongName { get; }

    public string? Profile { get; }

    public string? Language { get; }

    public string? Title { get; }

    public bool IsDefault { get; }

    public bool IsForced { get; }

    public MediaTime? TimeBase { get; }

    public MediaTime? StartTime { get; }

    public MediaTime? Duration { get; }
}
