using ClipEdit.Application.Media;
using ClipEdit.Domain.Editing;
using ClipEdit.Domain.Geometry;
using ClipEdit.Media.Export;
using System.Collections.Immutable;

namespace ClipEdit.Application.Export;

public sealed class SingleSourceExportPlanner
{
    public ExportPlan Create(
        ImportedMedia media,
        SourceEdit edit,
        CropRegion crop,
        ExportPreset preset,
        string destinationPath,
        bool replaceExistingDestination = false,
        ImmutableArray<ExportAudioTrackPlan> audioTracks = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(preset);

        var video = media.Probe.VideoStreams.FirstOrDefault() ??
                    throw new ExportPlanException("The selected source has no video stream to export.");
        if (edit.IsEmpty)
        {
            throw new ExportPlanException("The edit removes the entire source; keep some video before exporting.");
        }

        if (crop.SourceSize != video.OrientedSize)
        {
            throw new ExportPlanException("The crop no longer matches the selected source dimensions.");
        }

        return new ExportPlan(
            media.Probe.SourcePath,
            destinationPath,
            video.Index,
            media.Probe.AudioStreams.FirstOrDefault()?.Index,
            crop,
            edit.KeptRanges,
            preset,
            replaceExistingDestination,
            audioTracks,
            sourceVideoColorInfo: new ExportVideoColorInfo(
                video.PixelFormat,
                video.ColorRange,
                video.ColorSpace,
                video.ColorTransfer,
                video.ColorPrimaries));
    }
}
