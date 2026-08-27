using ClipEdit.Domain.Timeline;

namespace ClipEdit.App.ViewModels;

public sealed record SequenceChapterViewModel(
    VideoClipViewModel Clip,
    string Title,
    MediaRange TimelineRange,
    string DisplayName);
