namespace ClipEdit.Media.Mpv;

public readonly record struct MpvApiVersion(int Major, int Minor)
{
    public static MpvApiVersion FromPacked(uint value) =>
        new(checked((int)(value >> 16)), checked((int)(value & 0xffff)));

    public override string ToString() => $"{Major}.{Minor}";
}
