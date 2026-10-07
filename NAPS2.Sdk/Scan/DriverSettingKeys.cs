namespace NAPS2.Scan;

/// <summary>
/// Backend-neutral keys for <see cref="DriverProcessingOptions.Settings"/>.
/// </summary>
/// <remarks>
/// Each key names a desired result. TWAIN and WIA translate it through their standard capabilities or properties;
/// where a protocol has no standard equivalent the request is reported as unsupported with the reason, never guessed.
/// Text values are the lower camel case names listed on each key.
/// </remarks>
public static class DriverSettingKeys
{
    /// <summary>Real, greater than zero. TWAIN ICAP_GAMMA. WIA has no usable standard property.</summary>
    public const string Gamma = "gamma";

    /// <summary>
    /// Text: "fixed" or "adaptive". TWAIN ICAP_BITDEPTHREDUCTION (TWBR_THRESHOLD, TWBR_DYNAMICTHRESHOLD). WIA only
    /// produces a fixed threshold.
    /// </summary>
    public const string BlackAndWhiteMethod = "blackAndWhiteMethod";

    /// <summary>Integer 0 to 255. TWAIN ICAP_THRESHOLD, WIA_IPS_THRESHOLD.</summary>
    public const string Threshold = "threshold";

    /// <summary>Text: "none", "auto", "lowPass", "bandPass", "highPass". TWAIN ICAP_IMAGEFILTER.</summary>
    public const string ImageFilter = "imageFilter";

    /// <summary>Text: "none", "auto", "lonePixel", "majorityRule". TWAIN ICAP_NOISEFILTER.</summary>
    public const string NoiseFilter = "noiseFilter";

    /// <summary>
    /// Text: "none", "red", "green", "blue". TWAIN ICAP_FILTER, WIA_IPS_COLOR_DROP. Other colors are not portable.
    /// </summary>
    public const string ColorDropout = "colorDropout";

    /// <summary>Boolean. TWAIN ICAP_BARCODEDETECTIONENABLED, WIA_IPS_BARCODE_READER.</summary>
    public const string BarcodeDetection = "barcodeDetection";

    /// <summary>
    /// Text: a comma-separated list in priority order of "qr", "code128", "gs1128", "pdf417", "code39",
    /// "code39FullAscii", "code93", "interleaved2of5", "nonInterleaved2of5", "codabar", "ean8", "ean13", "upca",
    /// "upce", "postnet", "maxicode". TWAIN ICAP_BARCODESEARCHPRIORITIES.
    /// </summary>
    public const string BarcodeTypes = "barcodeTypes";

    /// <summary>
    /// Text: "horizontal", "vertical", "horizontalVertical", "verticalHorizontal", or "auto" (WIA only). TWAIN
    /// ICAP_BARCODESEARCHMODE, WIA_IPS_BARCODE_SEARCH_DIRECTION.
    /// </summary>
    public const string BarcodeSearchDirection = "barcodeSearchDirection";

    /// <summary>Integer. WIA_IPS_MAXIMUM_BARCODES_PER_PAGE. TWAIN has no standard equivalent.</summary>
    public const string BarcodeMaximumCount = "barcodeMaximumCount";

    /// <summary>
    /// Boolean. TWAIN ICAP_AUTOMATICLENGTHDETECTION, WIA_IPS_LONG_DOCUMENT. Split output is never requested.
    /// </summary>
    public const string LongDocument = "longDocument";

    /// <summary>
    /// Text: "ultrasonic", "length", "infrared" (TWAIN CAP_DOUBLEFEEDDETECTION) or "length", "overlap" (WIA
    /// WIA_IPS_MULTI_FEED_DETECT_METHOD). The protocols' methods are not assumed to be equivalent.
    /// </summary>
    public const string MultifeedMethod = "multifeedMethod";

    /// <summary>
    /// Text: "low", "medium", "high", or "off" (vendor only). TWAIN CAP_DOUBLEFEEDDETECTIONSENSITIVITY, or a verified
    /// vendor ultrasonic sensitivity when the standard capability is unsupported.
    /// </summary>
    public const string MultifeedSensitivity = "multifeedSensitivity";

    /// <summary>
    /// Text: "stop", "stopAndWait" (TWAIN only), "continue" (WIA only) or "off" (WIA only). TWAIN
    /// CAP_DOUBLEFEEDDETECTIONRESPONSE, WIA_IPS_MULTI_FEED.
    /// </summary>
    public const string MultifeedResponse = "multifeedResponse";

    /// <summary>Text: "normal", "fragile", "thick", "trifold", "photograph". TWAIN CAP_PAPERHANDLING.</summary>
    public const string PaperHandling = "paperHandling";

    // The keys below have no standard TWAIN or WIA binding. They are available only through verified vendor
    // bindings for a matching device.

    /// <summary>Text: "photo", "textWithGraphics", "textWithPhoto", "text".</summary>
    public const string DocumentType = "documentType";

    /// <summary>Text: "cardstock", "glossy", "magazine", "plain", "thin".</summary>
    public const string MediaType = "mediaType";

    /// <summary>
    /// Text: "automaticBorder", "transport", "document", "aggressive". Modes producing several images per side are
    /// not offered.
    /// </summary>
    public const string CroppingMode = "croppingMode";

    /// <summary>Text: "off", "low", "medium", "high", "custom". Custom enables the amount and threshold.</summary>
    public const string AutomaticColorSensitivity = "automaticColorSensitivity";

    /// <summary>Integer 1 to 200; the amount of color needed to keep a page in color. Not a percentage.</summary>
    public const string AutomaticColorAmount = "automaticColorAmount";

    /// <summary>Integer 0 to 100; how intense a color must be to count towards the amount.</summary>
    public const string AutomaticColorThreshold = "automaticColorThreshold";

    /// <summary>Text: "none", "automatic", "changeToWhite", "automaticBasic".</summary>
    public const string BackgroundSmoothing = "backgroundSmoothing";

    /// <summary>Text: "all", "neutral", "predominant".</summary>
    public const string BackgroundSmoothingTarget = "backgroundSmoothingTarget";

    /// <summary>Integer -10 to 10.</summary>
    public const string BackgroundSmoothingStrength = "backgroundSmoothingStrength";

    /// <summary>Integer -1000 to 1000, manual color balance.</summary>
    public const string ColorBalanceRed = "colorBalanceRed";

    /// <summary>Integer -1000 to 1000, manual color balance.</summary>
    public const string ColorBalanceGreen = "colorBalanceGreen";

    /// <summary>Integer -1000 to 1000, manual color balance.</summary>
    public const string ColorBalanceBlue = "colorBalanceBlue";

    /// <summary>Text: "none", "normal", "more", "high".</summary>
    public const string Sharpening = "sharpening";

    /// <summary>Boolean.</summary>
    public const string StreakRemoval = "streakRemoval";

    /// <summary>Integer -2 to 2; enables streak removal.</summary>
    public const string StreakRemovalStrength = "streakRemovalStrength";

    /// <summary>Text: "none", "white", "black", "automatic", "automaticWithTears".</summary>
    public const string EdgeFill = "edgeFill";

    /// <summary>Real, inches, applied to all four edges.</summary>
    public const string EdgeFillWidth = "edgeFillWidth";

    /// <summary>
    /// Integer 0 to 100: a page with at most this percentage of content is discarded by the driver. Not
    /// interchangeable with EVOSCAN's blank page coverage.
    /// </summary>
    public const string BlankPageContent = "blankPageContent";

    /// <summary>Integer, seconds to wait for paper before the feed times out; 0 disables the timeout.</summary>
    public const string FeedTimeout = "feedTimeout";

    /// <summary>Text: "stopFeeder", "endOfJob".</summary>
    public const string FeedTimeoutResponse = "feedTimeoutResponse";

    /// <summary>Boolean; true applies the front settings to the rear.</summary>
    public const string SameSettingsBothSides = "sameSettingsBothSides";
}
