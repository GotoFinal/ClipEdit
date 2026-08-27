using ClipEdit.Domain.Timeline;

namespace ClipEdit.Media.Probe;

public sealed record MediaChapterInfo
{
    public MediaChapterInfo(string title, MediaRange range)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (range.Start < MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(range), "A chapter cannot start before the source.");
        }

        Title = title.Trim();
        Range = range;
    }

    public string Title { get; }

    public MediaRange Range { get; }
}
