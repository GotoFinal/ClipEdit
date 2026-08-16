namespace ClipEdit.Media.FFmpeg.Probe;

internal static class FfprobeArguments
{
    private const string Entries =
        "format=format_name,format_long_name,start_time,duration,size,bit_rate:" +
        "format_tags=title,encoder,creation_time:" +
        "stream=index,codec_type,codec_name,codec_long_name,codec_tag_string,profile,level,extradata_hash,width,height," +
        "sample_aspect_ratio,display_aspect_ratio,pix_fmt,color_range,color_space," +
        "color_transfer,color_primaries,field_order,r_frame_rate,avg_frame_rate," +
        "time_base,start_pts,start_time,duration_ts,duration,bit_rate,sample_fmt," +
        "sample_rate,channels,channel_layout:" +
        "stream_tags=language,title,DURATION,rotate:" +
        "stream_disposition=default,forced,attached_pic:" +
        "stream_side_data=rotation";

    public static IReadOnlyList<string> Create(string sourcePath)
    {
        return
        [
            "-hide_banner",
            "-v",
            "error",
            "-print_format",
            "json",
            "-show_data_hash",
            "sha256",
            "-show_format",
            "-show_streams",
            "-show_entries",
            Entries,
            "--",
            sourcePath,
        ];
    }
}
