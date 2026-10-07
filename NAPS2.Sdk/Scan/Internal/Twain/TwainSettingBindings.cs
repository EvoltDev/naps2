using static NAPS2.Scan.Internal.NativeSettingConversions;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// Standard TWAIN capability bindings for <see cref="DriverSettingKeys"/>. Ids and constants are from the TWAIN 2.3
/// specification header (twain.h).
/// </summary>
/// <remarks>
/// The order groups dependencies: image processing (100s) before detection (200s) before paper handling (300s), and
/// within a group a setting that constrains another is written first, such as the bit depth reduction method before
/// the threshold.
/// </remarks>
internal static class TwainSettingBindings
{
    private const string Protocol = "TWAIN";

    // Capability ids.
    private const int ICapFilter = 0x1106;
    private const int ICapGamma = 0x1108;
    private const int ICapThreshold = 0x1123;
    private const int ICapBitDepthReduction = 0x112c;
    private const int ICapBarcodeDetectionEnabled = 0x1137;
    private const int ICapSupportedBarcodeTypes = 0x1138;
    private const int ICapBarcodeSearchPriorities = 0x113a;
    private const int ICapBarcodeSearchMode = 0x113b;
    private const int ICapImageFilter = 0x1147;
    private const int ICapNoiseFilter = 0x1148;
    private const int ICapAutomaticLengthDetection = 0x1158;
    private const int CapDoubleFeedDetection = 0x103f;
    private const int CapDoubleFeedDetectionSensitivity = 0x1041;
    private const int CapDoubleFeedDetectionResponse = 0x1042;
    private const int CapPaperHandling = 0x1043;

    // TWBR_*
    internal static readonly IReadOnlyDictionary<string, int> BlackAndWhiteMethods = new Dictionary<string, int>
    {
        ["fixed"] = 0, // TWBR_THRESHOLD
        ["adaptive"] = 4 // TWBR_DYNAMICTHRESHOLD
    };

    // TWIF_*
    internal static readonly IReadOnlyDictionary<string, int> ImageFilters = new Dictionary<string, int>
    {
        ["none"] = 0,
        ["auto"] = 1,
        ["lowPass"] = 2,
        ["bandPass"] = 3,
        ["highPass"] = 4
    };

    // TWNF_*
    internal static readonly IReadOnlyDictionary<string, int> NoiseFilters = new Dictionary<string, int>
    {
        ["none"] = 0,
        ["auto"] = 1,
        ["lonePixel"] = 2,
        ["majorityRule"] = 3
    };

    // TWFT_*. ICAP_FILTER names the color filtered out of the image, which is color dropout.
    internal static readonly IReadOnlyDictionary<string, int> DropoutColors = new Dictionary<string, int>
    {
        ["red"] = 0,
        ["green"] = 1,
        ["blue"] = 2,
        ["none"] = 3
    };

    // TWBT_*
    internal static readonly IReadOnlyDictionary<string, int> BarcodeTypes = new Dictionary<string, int>
    {
        ["code39"] = 0,
        ["interleaved2of5"] = 1,
        ["nonInterleaved2of5"] = 2,
        ["code93"] = 3,
        ["code128"] = 4,
        ["gs1128"] = 5,
        ["codabar"] = 6,
        ["upca"] = 7,
        ["upce"] = 8,
        ["ean8"] = 9,
        ["ean13"] = 10,
        ["postnet"] = 11,
        ["pdf417"] = 12,
        ["code39FullAscii"] = 17,
        ["maxicode"] = 19,
        ["qr"] = 20
    };

    // TWBD_*
    internal static readonly IReadOnlyDictionary<string, int> BarcodeSearchModes = new Dictionary<string, int>
    {
        ["horizontal"] = 0,
        ["vertical"] = 1,
        ["horizontalVertical"] = 2,
        ["verticalHorizontal"] = 3
    };

    // TWDF_*
    internal static readonly IReadOnlyDictionary<string, int> DoubleFeedMethods = new Dictionary<string, int>
    {
        ["ultrasonic"] = 0,
        ["length"] = 1,
        ["infrared"] = 2
    };

    // TWUS_*
    internal static readonly IReadOnlyDictionary<string, int> DoubleFeedSensitivities = new Dictionary<string, int>
    {
        ["low"] = 0,
        ["medium"] = 1,
        ["high"] = 2
    };

    // TWDP_*. Sound and do-not-imprint are not stop behaviors and are not offered.
    internal static readonly IReadOnlyDictionary<string, int> DoubleFeedResponses = new Dictionary<string, int>
    {
        ["stop"] = 0,
        ["stopAndWait"] = 1
    };

    // TWPH_*
    internal static readonly IReadOnlyDictionary<string, int> PaperHandlings = new Dictionary<string, int>
    {
        ["normal"] = 0,
        ["fragile"] = 1,
        ["thick"] = 2,
        ["trifold"] = 3,
        ["photograph"] = 4
    };

    public static IReadOnlyDictionary<string, NativeSettingBinding> Bindings { get; } = new[]
    {
        Named(DriverSettingKeys.BlackAndWhiteMethod, ICapBitDepthReduction, "ICAP_BITDEPTHREDUCTION", 100,
            BlackAndWhiteMethods),
        new NativeSettingBinding
        {
            Key = DriverSettingKeys.Threshold,
            Protocol = Protocol,
            NativeId = ICapThreshold,
            NativeName = "ICAP_THRESHOLD",
            ValueType = NativeValueType.Fix32,
            Order = 110,
            ToNative = IntegerToReal(0, 255),
            FromNative = IntegerFromReal
        },
        new NativeSettingBinding
        {
            Key = DriverSettingKeys.Gamma,
            Protocol = Protocol,
            NativeId = ICapGamma,
            NativeName = "ICAP_GAMMA",
            ValueType = NativeValueType.Fix32,
            Order = 120,
            ToNative = PositiveRealToNative(),
            FromNative = RealFromNative
        },
        Named(DriverSettingKeys.ImageFilter, ICapImageFilter, "ICAP_IMAGEFILTER", 130, ImageFilters),
        Named(DriverSettingKeys.NoiseFilter, ICapNoiseFilter, "ICAP_NOISEFILTER", 140, NoiseFilters),
        Named(DriverSettingKeys.ColorDropout, ICapFilter, "ICAP_FILTER", 150, DropoutColors),
        Boolean(DriverSettingKeys.BarcodeDetection, ICapBarcodeDetectionEnabled, "ICAP_BARCODEDETECTIONENABLED", 200),
        NamedList(DriverSettingKeys.BarcodeTypes, ICapBarcodeSearchPriorities, "ICAP_BARCODESEARCHPRIORITIES", 210,
            BarcodeTypes, ICapSupportedBarcodeTypes),
        Named(DriverSettingKeys.BarcodeSearchDirection, ICapBarcodeSearchMode, "ICAP_BARCODESEARCHMODE", 220,
            BarcodeSearchModes),
        Boolean(DriverSettingKeys.LongDocument, ICapAutomaticLengthDetection, "ICAP_AUTOMATICLENGTHDETECTION", 300),
        NamedList(DriverSettingKeys.MultifeedMethod, CapDoubleFeedDetection, "CAP_DOUBLEFEEDDETECTION", 310,
            DoubleFeedMethods),
        Named(DriverSettingKeys.MultifeedSensitivity, CapDoubleFeedDetectionSensitivity,
            "CAP_DOUBLEFEEDDETECTIONSENSITIVITY", 320, DoubleFeedSensitivities),
        NamedList(DriverSettingKeys.MultifeedResponse, CapDoubleFeedDetectionResponse,
            "CAP_DOUBLEFEEDDETECTIONRESPONSE", 330, DoubleFeedResponses),
        Named(DriverSettingKeys.PaperHandling, CapPaperHandling, "CAP_PAPERHANDLING", 340, PaperHandlings)
    }.ToDictionary(x => x.Key, StringComparer.Ordinal);

    /// <summary>
    /// Keys TWAIN has no standard binding for, with the reason reported to the caller.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Gaps { get; } = KeyedDriverSettings.WithVendorOnlyGaps(
        new Dictionary<string, string>
        {
            [DriverSettingKeys.BarcodeMaximumCount] =
                "TWAIN has no standard maximum barcode count; ICAP_BARCODEMAXSEARCHPRIORITIES limits the priority " +
                "list, not the number of results."
        }, Protocol);

    private static NativeSettingBinding Named(string key, int id, string name, int order,
        IReadOnlyDictionary<string, int> names) => new()
    {
        Key = key,
        Protocol = Protocol,
        NativeId = id,
        NativeName = name,
        ValueType = NativeValueType.UInt16,
        Order = order,
        ToNative = NamedToNative(names),
        FromNative = NamedFromNative(names)
    };

    private static NativeSettingBinding NamedList(string key, int id, string name, int order,
        IReadOnlyDictionary<string, int> names, int? valuesId = null) => new()
    {
        Key = key,
        Protocol = Protocol,
        NativeId = id,
        NativeName = name,
        ValueType = NativeValueType.UInt16Array,
        Order = order,
        ValuesNativeId = valuesId,
        ToNative = NamedListToNative(names),
        FromNative = NamedListFromNative(names)
    };

    private static NativeSettingBinding Boolean(string key, int id, string name, int order) => new()
    {
        Key = key,
        Protocol = Protocol,
        NativeId = id,
        NativeName = name,
        ValueType = NativeValueType.Boolean,
        Order = order,
        ToNative = BooleanToNative(),
        FromNative = BooleanFromNative
    };
}
