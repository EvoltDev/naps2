namespace NAPS2.Images;

public sealed record SubPixelType
{
    /// <summary>
    /// 4 bytes per pixel, 1 byte per sample, red-green-blue-alpha order.
    /// </summary>
    public static readonly SubPixelType Rgba = new()
    {
        BitsPerPixel = 32,
        BytesPerPixel = 4,
        RedOffset = 0,
        GreenOffset = 1,
        BlueOffset = 2,
        AlphaOffset = 3,
        HasAlpha = true
    };

    /// <summary>
    /// 3 bytes per pixel, 1 byte per sample, red-green-blue order.
    /// </summary>
    public static readonly SubPixelType Rgb = new()
    {
        BitsPerPixel = 24,
        BytesPerPixel = 3,
        RedOffset = 0,
        GreenOffset = 1,
        BlueOffset = 2
    };

    /// <summary>
    /// 4 bytes per pixel, 1 byte per sample, red-green-blue-none order.
    /// </summary>
    public static readonly SubPixelType Rgbn = new()
    {
        BitsPerPixel = 32,
        BytesPerPixel = 4,
        RedOffset = 0,
        GreenOffset = 1,
        BlueOffset = 2
    };

    /// <summary>
    /// 4 bytes per pixel, 1 byte per sample, blue-green-red-alpha order.
    /// </summary>
    public static readonly SubPixelType Bgra = new()
    {
        BitsPerPixel = 32,
        BytesPerPixel = 4,
        RedOffset = 2,
        GreenOffset = 1,
        BlueOffset = 0,
        AlphaOffset = 3,
        HasAlpha = true
    };

    /// <summary>
    /// 3 bytes per pixel, 1 byte per sample, blue-green-red order.
    /// </summary>
    public static readonly SubPixelType Bgr = new()
    {
        BitsPerPixel = 24,
        BytesPerPixel = 3,
        RedOffset = 2,
        GreenOffset = 1,
        BlueOffset = 0
    };

    /// <summary>
    /// 1 byte per pixel, 0 = black, 255 = white.
    /// </summary>
    public static readonly SubPixelType Gray = new()
    {
        BitsPerPixel = 8,
        BytesPerPixel = 1
    };

    /// <summary>
    /// 1 bit per pixel, 0 = black, 1 = white.
    /// </summary>
    public static readonly SubPixelType Bit = new()
    {
        BitsPerPixel = 1
    };

    /// <summary>
    /// 1 bit per pixel, 0 = white, 1 = black.
    /// </summary>
    public static readonly SubPixelType InvertedBit = new()
    {
        BitsPerPixel = 1,
        InvertColorSpace = true
    };

    public SubPixelType()
    {
    }

    public int BitsPerPixel { get; init; }
    public int BytesPerPixel { get; init; }
    public int RedOffset { get; init; }
    public int GreenOffset { get; init; }
    public int BlueOffset { get; init; }
    public int AlphaOffset { get; init; }
    public bool HasAlpha { get; init; }
    public bool InvertColorSpace { get; init; }
}
