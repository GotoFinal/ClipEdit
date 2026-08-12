using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Probe;

public sealed record AudioStreamInfo : MediaStreamInfo
{
    public AudioStreamInfo(
        int index,
        string codecName,
        string? codecLongName,
        string? profile,
        string? language,
        string? title,
        bool isDefault,
        bool isForced,
        MediaTime? timeBase,
        MediaTime? startTime,
        MediaTime? duration,
        int? sampleRate,
        int? channelCount,
        string? channelLayout,
        string? sampleFormat)
        : base(
            index,
            MediaStreamKind.Audio,
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
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        SampleRate = sampleRate;
        ChannelCount = channelCount;
        ChannelLayout = channelLayout;
        SampleFormat = sampleFormat;
    }

    public int? SampleRate { get; }

    public int? ChannelCount { get; }

    public string? ChannelLayout { get; }

    public string? SampleFormat { get; }
}
