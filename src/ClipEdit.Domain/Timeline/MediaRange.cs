namespace ClipEdit.Domain.Timeline;

/// <summary>
/// A half-open media range: the start is included and the end is excluded.
/// </summary>
public readonly record struct MediaRange
{
    public MediaRange(MediaTime start, MediaTime end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                end,
                "A media range cannot end before it starts.");
        }

        Start = start;
        End = end;
    }

    public MediaTime Start { get; }

    public MediaTime End { get; }

    public MediaTime Duration => End - Start;

    public bool IsEmpty => Start == End;

    public bool Contains(MediaTime value) => value >= Start && value < End;
}
