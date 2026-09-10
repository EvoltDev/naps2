using NAPS2.Images;

namespace NAPS2.Scan.Internal.Wia;

internal static class WiaFeedOrientationConfiguration
{
    internal const int OrientationPropertyId = 6156; // WIA_IPS_ORIENTATION

    public static (int Width, int Height) GetPageDimensions(PageSize pageSize, WiaFeedOrientation? orientation)
    {
        var width = pageSize.WidthInThousandthsOfAnInch;
        var height = pageSize.HeightInThousandthsOfAnInch;
        return orientation switch
        {
            null => (width, height),
            WiaFeedOrientation.Portrait => (Math.Min(width, height), Math.Max(width, height)),
            WiaFeedOrientation.Landscape => (Math.Max(width, height), Math.Min(width, height)),
            _ => throw new ArgumentOutOfRangeException(nameof(orientation))
        };
    }

    public static void Apply(WiaFeedOrientation? orientation, Action<int> write, Func<int?> read)
    {
        if (orientation == null) return;
        if (orientation is not (WiaFeedOrientation.Portrait or WiaFeedOrientation.Landscape))
            throw new ArgumentOutOfRangeException(nameof(orientation));
        if (read() == null)
            throw new InvalidOperationException("The WIA source does not support feed orientation. Choose Driver default in the WIA profile settings.");
        write((int) orientation.Value);
        Verify(orientation, read);
    }

    public static void Verify(WiaFeedOrientation? orientation, Func<int?> read)
    {
        if (orientation != null && read() != (int) orientation.Value)
            throw new InvalidOperationException($"The WIA source did not retain the requested {orientation} feed orientation. Check the WIA profile settings.");
    }
}
