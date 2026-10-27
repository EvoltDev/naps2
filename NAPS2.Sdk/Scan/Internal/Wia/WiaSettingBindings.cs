using static NAPS2.Scan.Internal.NativeSettingConversions;

namespace NAPS2.Scan.Internal.Wia;

/// <summary>
/// Standard WIA property bindings for <see cref="DriverSettingKeys"/>. Ids and constants are from the Windows SDK
/// header WiaDef.h. Every bound property is a VT_I4, except the barcode type vectors (VT_VECTOR | VT_I4).
/// </summary>
internal static class WiaSettingBindings
{
    private const string Protocol = "WIA";

    private const int IpsThreshold = 6159; // WIA_IPS_THRESHOLD
    private const int IpsColorDrop = 4176; // WIA_IPS_COLOR_DROP
    private const int IpsBarcodeReader = 4150; // WIA_IPS_BARCODE_READER
    private const int IpsMaximumBarcodesPerPage = 4151; // WIA_IPS_MAXIMUM_BARCODES_PER_PAGE
    private const int IpsBarcodeSearchDirection = 4152; // WIA_IPS_BARCODE_SEARCH_DIRECTION
    private const int IpsSupportedBarcodeTypes = 4155; // WIA_IPS_SUPPORTED_BARCODE_TYPES
    private const int IpsEnabledBarcodeTypes = 4156; // WIA_IPS_ENABLED_BARCODE_TYPES
    private const int IpsLongDocument = 4166; // WIA_IPS_LONG_DOCUMENT
    private const int IpsMultiFeed = 4168; // WIA_IPS_MULTI_FEED
    private const int IpsMultiFeedSensitivity = 4169; // WIA_IPS_MULTI_FEED_SENSITIVITY
    private const int IpsColorDropRed = 4177; // WIA_IPS_COLOR_DROP_RED
    private const int IpsColorDropGreen = 4178; // WIA_IPS_COLOR_DROP_GREEN
    private const int IpsColorDropBlue = 4179; // WIA_IPS_COLOR_DROP_BLUE
    private const int IpsMultiFeedDetectMethod = 4193; // WIA_IPS_MULTI_FEED_DETECT_METHOD

    // WIA_BARCODE_READER_DISABLED and WIA_BARCODE_READER_AUTO. The flatbed/feeder-only values are not requested.
    private const int BarcodeReaderAuto = 1;
    private const int BarcodeReaderDisabled = 0;

    // WIA_LONG_DOCUMENT_ENABLED and _DISABLED. WIA_LONG_DOCUMENT_SPLIT is never requested.
    private const int LongDocumentEnabled = 1;
    private const int LongDocumentDisabled = 0;

    // WIA_COLOR_DROP_*. WIA_COLOR_DROP_RGB is selected by the RGB component settings instead.
    internal static readonly IReadOnlyDictionary<string, int> DropoutColors = new Dictionary<string, int>
    {
        ["none"] = 0,
        ["red"] = 1,
        ["green"] = 2,
        ["blue"] = 3
    };

    // WIA_COLOR_DROP_RGB: drop the color given by WIA_IPS_COLOR_DROP_RED, _GREEN and _BLUE.
    private const int ColorDropRgb = 4;

    // WIA_BARCODE_*. The identifiers differ from TWAIN's TWBT_* (QR is 41 here, 20 there). POSTNET has four WIA
    // variants (A, B, C, DPBC) and no single equivalent, so it is not offered.
    internal static readonly IReadOnlyDictionary<string, int> BarcodeTypes = new Dictionary<string, int>
    {
        ["upca"] = 0,
        ["upce"] = 1,
        ["codabar"] = 2,
        ["nonInterleaved2of5"] = 3,
        ["interleaved2of5"] = 4,
        ["code39"] = 5,
        ["code39FullAscii"] = 7,
        ["code93"] = 8,
        ["code128"] = 9,
        ["gs1128"] = 13,
        ["ean8"] = 16,
        ["ean13"] = 17,
        ["maxicode"] = 27,
        ["pdf417"] = 28,
        ["qr"] = 41
    };

    // WIA_IPS_MULTI_FEED_SENSITIVITY is a driver-defined scale, so the portable levels are placed on its range.
    private static readonly string[] MultiFeedSensitivityLevels = ["low", "medium", "high"];

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
        ColorDropComponent(DriverSettingKeys.ColorDropoutRed, IpsColorDropRed, "WIA_IPS_COLOR_DROP_RED", 151),
        ColorDropComponent(DriverSettingKeys.ColorDropoutGreen, IpsColorDropGreen, "WIA_IPS_COLOR_DROP_GREEN", 152),
        ColorDropComponent(DriverSettingKeys.ColorDropoutBlue, IpsColorDropBlue, "WIA_IPS_COLOR_DROP_BLUE", 153),
        Boolean(DriverSettingKeys.BarcodeDetection, IpsBarcodeReader, "WIA_IPS_BARCODE_READER", 200,
            BarcodeReaderAuto, BarcodeReaderDisabled),
        new NativeSettingBinding
        {
            Key = DriverSettingKeys.BarcodeTypes,
            Protocol = Protocol,
            NativeId = IpsEnabledBarcodeTypes,
            NativeName = "WIA_IPS_ENABLED_BARCODE_TYPES",
            ValueType = NativeValueType.Int32Array,
            Order = 210,
            ValuesNativeId = IpsSupportedBarcodeTypes,
            ToNative = NamedListToNative(BarcodeTypes, int32: true),
            FromNative = NamedListFromNative(BarcodeTypes)
        },
        Named(DriverSettingKeys.BarcodeSearchDirection, IpsBarcodeSearchDirection,
            "WIA_IPS_BARCODE_SEARCH_DIRECTION", 220, BarcodeSearchDirections),
        Integer(DriverSettingKeys.BarcodeMaximumCount, IpsMaximumBarcodesPerPage,
            "WIA_IPS_MAXIMUM_BARCODES_PER_PAGE", 230, 1, int.MaxValue),
        Boolean(DriverSettingKeys.LongDocument, IpsLongDocument, "WIA_IPS_LONG_DOCUMENT", 300, LongDocumentEnabled,
            LongDocumentDisabled),
        Named(DriverSettingKeys.MultifeedMethod, IpsMultiFeedDetectMethod, "WIA_IPS_MULTI_FEED_DETECT_METHOD", 310,
            MultiFeedMethods),
        new NativeSettingBinding
        {
            Key = DriverSettingKeys.MultifeedSensitivity,
            Protocol = Protocol,
            NativeId = IpsMultiFeedSensitivity,
            NativeName = "WIA_IPS_MULTI_FEED_SENSITIVITY",
            ValueType = NativeValueType.Int32,
            Order = 320,
            ToNative = _ => NativeConversion.Fail("The sensitivity is placed on the driver's own range."),
            ToNativeForProbe = NamedLevelsInRange(MultiFeedSensitivityLevels),
            FromNative = IntegerFromNative
        },
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
        [DriverSettingKeys.Sharpening] = "WIA defines no standard sharpening property.",
        [DriverSettingKeys.PaperHandling] = "WIA defines no standard paper handling property.",
        [DriverSettingKeys.TransferCompression] =
            "WIA transfer compression needs WIA_IPA_FORMAT, a CLSID property, chosen together with " +
            "WIA_IPA_COMPRESSION; that pair is not bound yet.",
        [DriverSettingKeys.TransferJpegQuality] = "WIA defines no standard JPEG quality property."
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

    /// <summary>One component of a dropped RGB color; WIA_IPS_COLOR_DROP must be WIA_COLOR_DROP_RGB first.</summary>
    private static NativeSettingBinding ColorDropComponent(string key, int id, string name, int order) =>
        Integer(key, id, name, order, 0, 255) with
        {
            Prerequisites =
            [
                new NativePrerequisite(new NativeSettingBinding
                {
                    Key = "WIA_IPS_COLOR_DROP",
                    Protocol = Protocol,
                    NativeId = IpsColorDrop,
                    NativeName = "WIA_IPS_COLOR_DROP",
                    ValueType = NativeValueType.Int32,
                    ToNative = _ => NativeConversion.Ok(ColorDropRgb),
                    FromNative = _ => null
                }, ColorDropRgb)
            ]
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
