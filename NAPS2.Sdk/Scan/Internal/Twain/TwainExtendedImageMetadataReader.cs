#if !MACOS
using System.Globalization;
using System.Text;
using NTwain;
using NTwain.Data;

namespace NAPS2.Scan.Internal.Twain;

internal static class TwainExtendedImageMetadataReader
{
    private const int MaxBarcodeEntries = 256;
    private const int MaxBarcodeTextBytes = 1024 * 1024;

    private static readonly ExtendedImageInfo[] RequestedInfoIds =
    [
        ExtendedImageInfo.PageSide,
        ExtendedImageInfo.BarcodeCount,
        ExtendedImageInfo.BarcodeType,
        ExtendedImageInfo.BarcodeTextLength,
        ExtendedImageInfo.BarcodeText,
        ExtendedImageInfo.BarcodeX,
        ExtendedImageInfo.BarcodeY,
        ExtendedImageInfo.BarcodeConfidence,
        ExtendedImageInfo.BarcodeRotation
    ];

    public static TwainExtendedImageMetadata Read(DataTransferredEventArgs? transfer)
    {
        if (transfer == null) return TwainExtendedImageMetadata.Empty;

        TWInfo[] infos;
        try
        {
            infos = transfer.GetExtImageInfo(RequestedInfoIds).ToArray();
        }
        catch
        {
            return TwainExtendedImageMetadata.Empty;
        }

        try
        {
            return Parse(Snapshot(infos));
        }
        catch
        {
            // Extended image information is optional. A malformed driver response must not fail or truncate the
            // image transfer that has already completed.
            return TwainExtendedImageMetadata.Empty;
        }
        finally
        {
            foreach (var returnedInfo in infos)
            {
                var info = returnedInfo;
                info.Dispose();
            }
        }
    }

    internal static TwainExtendedImageMetadata Parse(TwainExtendedImageValues values)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);
        var pageSide = MapPageSide(values.PageSide);
        var reportedCount = values.BarcodeCount.FirstOrDefault();
        var hasReportedCount = values.BarcodeCount.Count > 0;
        var observedCount = new[]
        {
            values.BarcodeTypes.Count,
            values.BarcodeTextLengths.Count,
            values.BarcodeX.Count,
            values.BarcodeY.Count,
            values.BarcodeConfidence.Count,
            values.BarcodeRotation.Count
        }.Max();

        if (hasReportedCount)
        {
            metadata["twain.barcode.count"] = reportedCount == uint.MaxValue
                ? "-1"
                : reportedCount.ToString(CultureInfo.InvariantCulture);
        }
        else if (observedCount > 0)
        {
            metadata["twain.barcode.count"] = observedCount.ToString(CultureInfo.InvariantCulture);
        }

        AddBarcodeTypes(metadata, values.BarcodeTypes);
        AddIndexedValues(metadata, "textLength", values.BarcodeTextLengths);
        AddBarcodeText(metadata, values.BarcodeTextLengths, values.BarcodeText);
        AddIndexedValues(metadata, "x", values.BarcodeX);
        AddIndexedValues(metadata, "y", values.BarcodeY);
        AddIndexedValues(metadata, "confidence", values.BarcodeConfidence);
        AddIndexedValues(metadata, "rotation", values.BarcodeRotation);

        if (values.WasTruncated || observedCount > MaxBarcodeEntries ||
            hasReportedCount && reportedCount != uint.MaxValue && reportedCount > MaxBarcodeEntries)
        {
            metadata["twain.barcode.truncated"] = bool.TrueString;
        }

        return new TwainExtendedImageMetadata(pageSide, metadata);
    }

    private static TwainExtendedImageValues Snapshot(IReadOnlyCollection<TWInfo> infos)
    {
        var pageSide = ReadValues(infos, ExtendedImageInfo.PageSide).FirstOrDefault();
        var counts = ReadUInt32Values(infos, ExtendedImageInfo.BarcodeCount);
        var types = ReadUInt32Values(infos, ExtendedImageInfo.BarcodeType);
        var lengths = ReadUInt32Values(infos, ExtendedImageInfo.BarcodeTextLength);
        var x = ReadUInt32Values(infos, ExtendedImageInfo.BarcodeX);
        var y = ReadUInt32Values(infos, ExtendedImageInfo.BarcodeY);
        var confidence = ReadUInt32Values(infos, ExtendedImageInfo.BarcodeConfidence);
        var rotation = ReadUInt32Values(infos, ExtendedImageInfo.BarcodeRotation);
        var (text, textWasTruncated) = ReadBarcodeText(infos, lengths);

        return new TwainExtendedImageValues
        {
            PageSide = pageSide,
            BarcodeCount = counts,
            BarcodeTypes = types,
            BarcodeTextLengths = lengths,
            BarcodeText = text,
            BarcodeX = x,
            BarcodeY = y,
            BarcodeConfidence = confidence,
            BarcodeRotation = rotation,
            WasTruncated = textWasTruncated
        };
    }

    private static IReadOnlyList<object> ReadValues(IReadOnlyCollection<TWInfo> infos, ExtendedImageInfo infoId)
    {
        var info = infos.FirstOrDefault(candidate => candidate.InfoID == infoId);
        if (info.InfoID == ExtendedImageInfo.Invalid || info.ReturnCode != ReturnCode.Success)
        {
            return Array.Empty<object>();
        }

        try
        {
            return info.ReadValues().ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static IReadOnlyList<uint> ReadUInt32Values(IReadOnlyCollection<TWInfo> infos,
        ExtendedImageInfo infoId)
    {
        return ReadValues(infos, infoId)
            .Select(TryConvertToUInt32)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Take(MaxBarcodeEntries + 1)
            .ToArray();
    }

    private static uint? TryConvertToUInt32(object value)
    {
        try
        {
            return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static (byte[] Bytes, bool WasTruncated) ReadBarcodeText(IReadOnlyCollection<TWInfo> infos,
        IReadOnlyList<uint> lengths)
    {
        var info = infos.FirstOrDefault(candidate => candidate.InfoID == ExtendedImageInfo.BarcodeText);
        if (info.InfoID == ExtendedImageInfo.Invalid || info.ReturnCode != ReturnCode.Success ||
            info.ItemType != ItemType.Handle)
        {
            return (Array.Empty<byte>(), false);
        }

        long requestedByteCount = 0;
        foreach (var length in lengths.Take(MaxBarcodeEntries))
        {
            requestedByteCount += length;
            if (requestedByteCount > MaxBarcodeTextBytes)
            {
                return (Array.Empty<byte>(), true);
            }
        }

        try
        {
            return (info.ReadBytes((int)requestedByteCount), lengths.Count > MaxBarcodeEntries);
        }
        catch
        {
            return (Array.Empty<byte>(), false);
        }
    }

    private static RawScanPageSide MapPageSide(object? value)
    {
        if (value == null) return RawScanPageSide.Unknown;
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) switch
            {
                1 => RawScanPageSide.Front,
                2 => RawScanPageSide.Back,
                _ => RawScanPageSide.Unknown
            };
        }
        catch
        {
            return RawScanPageSide.Unknown;
        }
    }

    private static void AddBarcodeTypes(IDictionary<string, string?> metadata, IReadOnlyList<uint> values)
    {
        for (var index = 0; index < Math.Min(values.Count, MaxBarcodeEntries); index++)
        {
            var value = values[index];
            metadata[$"twain.barcode.{index}.typeCode"] = value.ToString(CultureInfo.InvariantCulture);
            var name = GetBarcodeTypeName(value);
            if (name != null)
            {
                metadata[$"twain.barcode.{index}.type"] = name;
            }
        }
    }

    private static string? GetBarcodeTypeName(uint value)
    {
        if (value == 3) return nameof(BarcodeType.Code93);
        if (value > ushort.MaxValue || !Enum.IsDefined(typeof(BarcodeType), (ushort)value)) return null;
        return ((BarcodeType)(ushort)value).ToString();
    }

    private static void AddIndexedValues(IDictionary<string, string?> metadata, string field,
        IReadOnlyList<uint> values)
    {
        for (var index = 0; index < Math.Min(values.Count, MaxBarcodeEntries); index++)
        {
            metadata[$"twain.barcode.{index}.{field}"] = values[index].ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void AddBarcodeText(IDictionary<string, string?> metadata, IReadOnlyList<uint> lengths,
        IReadOnlyList<byte> text)
    {
        var offset = 0;
        for (var index = 0; index < Math.Min(lengths.Count, MaxBarcodeEntries); index++)
        {
            var length = lengths[index];
            if (length > int.MaxValue || offset > text.Count - (int)length) return;

            var bytes = new byte[(int)length];
            for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
            {
                bytes[byteIndex] = text[offset + byteIndex];
            }
            metadata[$"twain.barcode.{index}.text"] = Encoding.ASCII.GetString(bytes);
            offset += bytes.Length;
        }
    }
}

internal sealed class TwainExtendedImageMetadata
{
    public static TwainExtendedImageMetadata Empty { get; } =
        new(RawScanPageSide.Unknown, new Dictionary<string, string?>(StringComparer.Ordinal));

    public TwainExtendedImageMetadata(RawScanPageSide pageSide,
        IReadOnlyDictionary<string, string?> additionalMetadata)
    {
        PageSide = pageSide;
        AdditionalMetadata = additionalMetadata;
    }

    public RawScanPageSide PageSide { get; }
    public IReadOnlyDictionary<string, string?> AdditionalMetadata { get; }
}

internal sealed class TwainExtendedImageValues
{
    public object? PageSide { get; init; }
    public IReadOnlyList<uint> BarcodeCount { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> BarcodeTypes { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> BarcodeTextLengths { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<byte> BarcodeText { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<uint> BarcodeX { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> BarcodeY { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> BarcodeConfidence { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> BarcodeRotation { get; init; } = Array.Empty<uint>();
    public bool WasTruncated { get; init; }
}
#endif
