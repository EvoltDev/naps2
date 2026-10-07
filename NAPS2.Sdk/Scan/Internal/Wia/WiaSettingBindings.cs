using static NAPS2.Scan.Internal.NativeSettingConversions;

namespace NAPS2.Scan.Internal.Wia;

/// <summary>
/// Standard WIA property bindings for <see cref="DriverSettingKeys"/>. Ids and constants are from the Windows SDK
/// header WiaDef.h. Every bound property is a VT_I4.
/// </summary>
internal static class WiaSettingBindings
{
    private const string Protocol = "WIA";

    private const int IpsThreshold = 6159; // WIA_IPS_THRESHOLD
    private const int IpsColorDrop = 4176; // WIA_IPS_COLOR_DROP
    private const int IpsBarcodeReader = 4150; // WIA_IPS_BARCODE_READER
    private const int IpsMaximumBarcodesPerPage = 4151; // WIA_IPS_MAXIMUM_BARCODES_PER_PAGE
    private const int IpsBarcodeSearchDirection = 4152; // WIA_IPS_BARCODE_SEARCH_DIRECTION
    private const int IpsLongDocument = 4166; // WIA_IPS_LONG_DOCUMENT
    private const int IpsMultiFeed = 4168; // WIA_IPS_MULTI_FEED
    private const int IpsMultiFeedDetectMethod = 4193; // WIA_IPS_MULTI_FEED_DETECT_METHOD

    // WIA_BARCODE_READER_DISABLED and WIA_BARCODE_READER_AUTO. The flatbed/feeder-only values are not requested.
    private const int BarcodeReaderAuto = 1;
    private const int BarcodeReaderDisabled = 0;

    // WIA_LONG_DOCUMENT_ENABLED and _DISABLED. WIA_LONG_DOCUMENT_SPLIT is never requested.
    private const int LongDocumentEnabled = 1;
    private const int LongDocumentDisabled = 0;

    // WIA_COLOR_DROP_*. WIA_COLOR_DROP_RGB needs the separate RGB properties and is not offered.
    internal static readonly IReadOnlyDictionary<string, int> DropoutColors = new Dictionary<string, int>
    {
        ["none"] = 0,
        ["red"] = 1,
        ["green"] = 2,
        ["blue"] = 3
    };

    // WIA_BARCODE_*_SEARCH
    internal static readonly IReadOnlyDictionary<string, int> BarcodeSearchDirections = new Dictionary<string, int>
    {
        ["horizontal"] = 0,
        ["vertical"] = 1,
        ["horizontalVertical"] = 2,
        ["verticalHorizontal"] = 3,
        ["auto"] = 4
    };

    // WIA_MULTI_FEED_DETECT_METHOD_*
    internal static readonly IReadOnlyDictionary<string, int> MultiFeedMethods = new Dictionary<string, int>
    {
        ["length"] = 0,
        ["overlap"] = 1
    };

    // WIA_MULTI_FEED_DETECT_*. WIA_IPS_MULTI_FEED combines enabling detection with the response.
    internal static readonly IReadOnlyDictionary<string, int> MultiFeedResponses = new Dictionary<string, int>
    {
        ["off"] = 0, // WIA_MULTI_FEED_DETECT_DISABLED
        ["stop"] = 1, // WIA_MULTI_FEED_DETECT_STOP_ERROR
        ["continue"] = 3 // WIA_MULTI_FEED_DETECT_CONTINUE
    };

    public static IReadOnlyDictionary<string, NativeSettingBinding> Bindings { get; } = new[]
    {
        Integer(DriverSettingKeys.Threshold, IpsThreshold, "WIA_IPS_THRESHOLD", 110, 0, 255),
        Named(DriverSettingKeys.ColorDropout, IpsColorDrop, "WIA_IPS_COLOR_DROP", 150, DropoutColors),
        Boolean(DriverSettingKeys.BarcodeDetection, IpsBarcodeReader, "WIA_IPS_BARCODE_READER", 200,
            BarcodeReaderAuto, BarcodeReaderDisabled),
        Named(DriverSettingKeys.BarcodeSearchDirection, IpsBarcodeSearchDirection,
            "WIA_IPS_BARCODE_SEARCH_DIRECTION", 220, BarcodeSearchDirections),
        Integer(DriverSettingKeys.BarcodeMaximumCount, IpsMaximumBarcodesPerPage,
            "WIA_IPS_MAXIMUM_BARCODES_PER_PAGE", 230, 1, int.MaxValue),
        Boolean(DriverSettingKeys.LongDocument, IpsLongDocument, "WIA_IPS_LONG_DOCUMENT", 300, LongDocumentEnabled,
            LongDocumentDisabled),
        Named(DriverSettingKeys.MultifeedMethod, IpsMultiFeedDetectMethod, "WIA_IPS_MULTI_FEED_DETECT_METHOD", 310,
            MultiFeedMethods),
        Named(DriverSettingKeys.MultifeedResponse, IpsMultiFeed, "WIA_IPS_MULTI_FEED", 330, MultiFeedResponses)
    }.ToDictionary(x => x.Key, StringComparer.Ordinal);

    /// <summary>
    /// Keys WIA has no standard binding for, with the reason reported to the caller.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Gaps { get; } = KeyedDriverSettings.WithVendorOnlyGaps(
        new Dictionary<string, string>
    {
        [DriverSettingKeys.Gamma] = "WIA's reserved gamma properties are not a usable standard binding.",
        [DriverSettingKeys.BlackAndWhiteMethod] =
            "WIA only produces a fixed threshold; an adaptive threshold has no standard property.",
        [DriverSettingKeys.ImageFilter] = "WIA defines no standard image filter property.",
        [DriverSettingKeys.NoiseFilter] = "WIA defines no standard noise filter property.",
        [DriverSettingKeys.BarcodeTypes] =
            "WIA_IPS_ENABLED_BARCODE_TYPES is a vector property the WIA wrapper cannot write.",
        [DriverSettingKeys.MultifeedSensitivity] =
            "WIA_IPS_MULTI_FEED_SENSITIVITY is a numeric scale with no defined low, medium or high values.",
        [DriverSettingKeys.PaperHandling] = "WIA defines no standard paper handling property."
    }, Protocol);

    private static NativeSettingBinding Named(string key, int id, string name, int order,
        IReadOnlyDictionary<string, int> names) => new()
    {
        Key = key,
        Protocol = Protocol,
        NativeId = id,
        NativeName = name,
        ValueType = NativeValueType.Int32,
        Order = order,
        ToNative = NamedToNative(names),
        FromNative = NamedFromNative(names)
    };

    private static NativeSettingBinding Integer(string key, int id, string name, int order, int minimum,
        int maximum) => new()
    {
        Key = key,
        Protocol = Protocol,
        NativeId = id,
        NativeName = name,
        ValueType = NativeValueType.Int32,
        Order = order,
        ToNative = IntegerToNative(minimum, maximum),
        FromNative = IntegerFromNative
    };

    private static NativeSettingBinding Boolean(string key, int id, string name, int order, int trueValue,
        int falseValue) => new()
    {
        Key = key,
        Protocol = Protocol,
        NativeId = id,
        NativeName = name,
        ValueType = NativeValueType.Int32,
        Order = order,
        ToNative = BooleanToInteger(trueValue, falseValue),
        FromNative = BooleanFromInteger(trueValue, falseValue)
    };
}
