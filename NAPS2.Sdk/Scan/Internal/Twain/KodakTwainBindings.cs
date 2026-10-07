using static NAPS2.Scan.Internal.NativeSettingConversions;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// Kodak Alaris custom TWAIN capabilities, from the Kodak-authored kdscust.h integrator header in the NTwain fork
/// (Spec/Kodak/kdscust.h). Ids, item types, ranges and constants are copied from that header.
/// </summary>
/// <remarks>
/// <para>
/// The header lists the scanner families each capability applies to, and the i4000 line after i4200/i4600 is not
/// named there. Family membership is therefore not used as a gate: a binding is offered when the source identifies as
/// Kodak, and only written when the source reports the capability with the documented type. Values outside what the
/// source enumerates are rejected before writing.
/// </para>
/// <para>
/// Most of these capabilities are per camera. Writes apply to the source's current camera; setting
/// CAP_SIDESDIFFERENT to FALSE, which is written last, copies the front settings to the rear.
/// </para>
/// </remarks>
internal static class KodakTwainBindings
{
    private const string Protocol = "TWAIN";
    private const string Source = "Kodak Alaris kdscust.h";

    private const int CapTransportTimeout = 0x8003;
    private const int ICapCroppingMode = 0x8022;
    private const int CapUltrasonicSensitivity = 0x8027;
    private const int CapTransportTimeoutResponse = 0x8028;
    private const int ICapAutoColorAmount = 0x8092;
    private const int ICapAutoColorContent = 0x8093;
    private const int ICapAutoColorThreshold = 0x8094;
    private const int ICapImageEdgeFill = 0x8095;
    private const int ICapImageEdgeTop = 0x8098;
    private const int CapBlankPageMode = 0x809B;
    private const int ICapDocumentType = 0x80AC;
    private const int ICapBackgroundAdjustMode = 0x80AE;
    private const int ICapBackgroundAdjustApplyTo = 0x80AF;
    private const int ICapBackgroundAdjustAggressiveness = 0x80B0;
    private const int ICapColorBalanceBlue = 0x80B1;
    private const int ICapColorBalanceGreen = 0x80B2;
    private const int ICapColorBalanceRed = 0x80B3;
    private const int ICapMediaType = 0x80B6;
    // Named CAP_CAMERALINK before driver 7.56, with different semantics.
    private const int CapSidesDifferent = 0x80B7;
    private const int ICapImageEdgeFillAllSides = 0x80B9;
    private const int CapBlankPageContent = 0x80C4;
    private const int ICapStreakRemovalEnabled = 0x80C6;
    private const int ICapStreakRemovalAggressiveness = 0x80C7;
    private const int ICapColorSharpen = 0x8102;
    private const int ICapColorBalanceMode = 0x8109;

    // TWDT_*
    internal static readonly IReadOnlyDictionary<string, int> DocumentTypes = new Dictionary<string, int>
    {
        ["photo"] = 0,
        ["textWithGraphics"] = 1,
        ["textWithPhoto"] = 2,
        ["text"] = 3
    };

    // TWMT_*
    internal static readonly IReadOnlyDictionary<string, int> MediaTypes = new Dictionary<string, int>
    {
        ["cardstock"] = 0,
        ["glossy"] = 1,
        ["magazine"] = 2,
        ["plain"] = 3,
        ["thin"] = 4
    };

    // TWCL_*. "off" is TWCL_NONE, which turns automatic color detection off.
    internal static readonly IReadOnlyDictionary<string, int> AutoColorContents = new Dictionary<string, int>
    {
        ["off"] = 0,
        ["low"] = 1,
        ["medium"] = 2,
        ["high"] = 3,
        ["custom"] = 4
    };

    private const int AutoColorCustom = 4;

    // TWBS_*
    internal static readonly IReadOnlyDictionary<string, int> BackgroundModes = new Dictionary<string, int>
    {
        ["none"] = 0,
        ["automatic"] = 1,
        ["changeToWhite"] = 2,
        ["automaticBasic"] = 3
    };

    // TWBA_*
    internal static readonly IReadOnlyDictionary<string, int> BackgroundTargets = new Dictionary<string, int>
    {
        ["all"] = 0,
        ["neutral"] = 1,
        ["predominant"] = 2
    };

    // ICAP_COLORSHARPEN levels 0-3.
    internal static readonly IReadOnlyDictionary<string, int> SharpeningLevels = new Dictionary<string, int>
    {
        ["none"] = 0,
        ["normal"] = 1,
        ["more"] = 2,
        ["high"] = 3
    };

    // TWIE_*
    internal static readonly IReadOnlyDictionary<string, int> EdgeFills = new Dictionary<string, int>
    {
        ["none"] = 0,
        ["white"] = 1,
        ["black"] = 2,
        ["automatic"] = 3,
        ["automaticWithTears"] = 4
    };

    // TWCR_*. Continuous, multiple-aggressive and the photo modes can produce long or multiple images per side and
    // are not offered.
    internal static readonly IReadOnlyDictionary<string, int> CroppingModes = new Dictionary<string, int>
    {
        ["automaticBorder"] = 0,
        ["transport"] = 1,
        ["document"] = 2,
        ["aggressive"] = 3
    };

    // TWTR_*
    internal static readonly IReadOnlyDictionary<string, int> TimeoutResponses = new Dictionary<string, int>
    {
        ["stopFeeder"] = 0,
        ["endOfJob"] = 1
    };

    // TWUSS_*. The header requires these values, not the standard TWUS_* ones, for CAP_ULTRASONICSENSITIVITY.
    internal static readonly IReadOnlyDictionary<string, int> UltrasonicSensitivities = new Dictionary<string, int>
    {
        ["off"] = 0,
        ["low"] = 1,
        ["medium"] = 2,
        ["high"] = 3
    };

    // TWBM_CONTENT; CAP_BLANKPAGECONTENT is only valid in this mode.
    private const int BlankPageModeContent = 2;

    // TWCBM_MANUAL; the RGB balance is ignored in any other mode.
    private const int ColorBalanceManual = 1;

    public static VendorBindingSet Set { get; } = new()
    {
        Name = Source,
        Matches = identity => identity.Manufacturer?.IndexOf("Kodak", StringComparison.OrdinalIgnoreCase) >= 0,
        Bindings =
        [
            new(Named(DriverSettingKeys.DocumentType, ICapDocumentType, "ICAP_DOCUMENTTYPE", 90, DocumentTypes)),
            new(Named(DriverSettingKeys.MediaType, ICapMediaType, "ICAP_MEDIATYPE", 95, MediaTypes)),
            new(Named(DriverSettingKeys.CroppingMode, ICapCroppingMode, "ICAP_CROPPINGMODE", 98, CroppingModes)),
            new(Named(DriverSettingKeys.AutomaticColorSensitivity, ICapAutoColorContent, "ICAP_AUTOCOLORCONTENT", 160,
                AutoColorContents)),
            new(Integer(DriverSettingKeys.AutomaticColorAmount, ICapAutoColorAmount, "ICAP_AUTOCOLORAMOUNT", 161, 1,
                200, Requires(ICapAutoColorContent, "ICAP_AUTOCOLORCONTENT", NativeValueType.UInt16,
                    AutoColorCustom))),
            new(Integer(DriverSettingKeys.AutomaticColorThreshold, ICapAutoColorThreshold, "ICAP_AUTOCOLORTHRESHOLD",
                162, 0, 100, Requires(ICapAutoColorContent, "ICAP_AUTOCOLORCONTENT", NativeValueType.UInt16,
                    AutoColorCustom))),
            new(Named(DriverSettingKeys.BackgroundSmoothing, ICapBackgroundAdjustMode, "ICAP_BACKGROUNDADJUSTMODE",
                170, BackgroundModes)),
            new(Named(DriverSettingKeys.BackgroundSmoothingTarget, ICapBackgroundAdjustApplyTo,
                "ICAP_BACKGROUNDADJUSTAPPLYTO", 171, BackgroundTargets)),
            new(Integer(DriverSettingKeys.BackgroundSmoothingStrength, ICapBackgroundAdjustAggressiveness,
                "ICAP_BACKGROUNDADJUSTAGGRESSIVENESS", 172, -10, 10)),
            new(ColorBalance(DriverSettingKeys.ColorBalanceRed, ICapColorBalanceRed, "ICAP_COLORBALANCERED", 180)),
            new(ColorBalance(DriverSettingKeys.ColorBalanceGreen, ICapColorBalanceGreen, "ICAP_COLORBALANCEGREEN",
                181)),
            new(ColorBalance(DriverSettingKeys.ColorBalanceBlue, ICapColorBalanceBlue, "ICAP_COLORBALANCEBLUE", 182)),
            // Documented as TWTY_UINT32; the i4250 with driver 16.4 reports TWTY_INT32 with the same 0-3 range.
            new(Named(DriverSettingKeys.Sharpening, ICapColorSharpen, "ICAP_COLORSHARPEN", 185, SharpeningLevels,
                NativeValueType.UInt32) with { AlternativeTypes = [NativeValueType.Int32] }),
            new(Boolean(DriverSettingKeys.StreakRemoval, ICapStreakRemovalEnabled, "ICAP_STREAKREMOVALENABLED", 190)),
            new(Integer(DriverSettingKeys.StreakRemovalStrength, ICapStreakRemovalAggressiveness,
                "ICAP_STREAKREMOVALAGGRESSIVENESS", 191, -2, 2,
                Requires(ICapStreakRemovalEnabled, "ICAP_STREAKREMOVALENABLED", NativeValueType.Boolean, true))),
            new(Named(DriverSettingKeys.EdgeFill, ICapImageEdgeFill, "ICAP_IMAGEEDGEFILL", 195, EdgeFills)),
            new(EdgeFillWidth()),
            // Documented as TW_UINT32; the i4250 with driver 16.4 reports TWTY_INT32 with the same 0-100 range.
            new(Integer(DriverSettingKeys.BlankPageContent, CapBlankPageContent, "CAP_BLANKPAGECONTENT", 240, 0, 100,
                Requires(CapBlankPageMode, "CAP_BLANKPAGEMODE", NativeValueType.UInt16, BlankPageModeContent),
                NativeValueType.UInt32) with { AlternativeTypes = [NativeValueType.Int32] }),
            new(Named(DriverSettingKeys.MultifeedSensitivity, CapUltrasonicSensitivity, "CAP_ULTRASONICSENSITIVITY",
                320, UltrasonicSensitivities)),
            new(Integer(DriverSettingKeys.FeedTimeout, CapTransportTimeout, "CAP_TRANSPORTTIMEOUT", 350, 0, 300)),
            new(Named(DriverSettingKeys.FeedTimeoutResponse, CapTransportTimeoutResponse,
                "CAP_TRANSPORTTIMEOUTRESPONSE", 351, TimeoutResponses)),
            // FALSE copies the front settings to the rear, so it runs after every other per-camera setting.
            new(new NativeSettingBinding
            {
                Key = DriverSettingKeys.SameSettingsBothSides,
                Protocol = Protocol,
                NativeId = CapSidesDifferent,
                NativeName = "CAP_SIDESDIFFERENT",
                ValueType = NativeValueType.Boolean,
                Order = 900,
                ToNative = value => value is { Kind: DriverSettingValueKind.Boolean, BooleanValue: { } same }
                    ? NativeConversion.Ok(!same)
                    : NativeConversion.Fail("A boolean value is required."),
                FromNative = native => DriverSettingValue.FromBoolean(KeyedSettingNegotiator.ToLong(native) == 0),
                RequireExactType = true,
                Source = Source + " (CAP_SIDESDIFFERENT from driver 7.56; the id was CAP_CAMERALINK before)"
            }, new Version(7, 56))
        ]
    };

    private static NativePrerequisite Requires(int id, string name, NativeValueType type, object value) =>
        new(new NativeSettingBinding
        {
            Key = name,
            Protocol = Protocol,
            NativeId = id,
            NativeName = name,
            ValueType = type,
            ToNative = _ => NativeConversion.Ok(value),
            FromNative = _ => null,
            RequireExactType = true
        }, value);

    private static NativeSettingBinding ColorBalance(string key, int id, string name, int order) =>
        // Programmatic range -1000 to 1000 in steps of 20. TWCBM_MANUAL is required where ICAP_COLORBALANCEMODE
        // exists; the header does not list that capability for every family, so it is an optional prerequisite.
        Integer(key, id, name, order, -1000, 1000) with
        {
            Prerequisites =
            [
                Requires(ICapColorBalanceMode, "ICAP_COLORBALANCEMODE", NativeValueType.UInt16, ColorBalanceManual)
                    with { Optional = true }
            ]
        };

    private static NativeSettingBinding EdgeFillWidth() => new()
    {
        // Applied to all sides through ICAP_IMAGEEDGETOP with ICAP_IMAGEEDGEFILLALLSIDES set. The value is in
        // ICAP_UNITS, which the TWAIN scan runner sets to inches before driver processing is applied.
        Key = DriverSettingKeys.EdgeFillWidth,
        Protocol = Protocol,
        NativeId = ICapImageEdgeTop,
        NativeName = "ICAP_IMAGEEDGETOP",
        ValueType = NativeValueType.Fix32,
        Order = 196,
        ToNative = value => value switch
        {
            { Kind: DriverSettingValueKind.Real, RealValue: >= 0 and < 100 } => NativeConversion.Ok(value.RealValue!),
            { Kind: DriverSettingValueKind.Integer, IntegerValue: >= 0 and < 100 } =>
                NativeConversion.Ok((double) value.IntegerValue!.Value),
            _ => NativeConversion.Fail("A width in inches from 0 to 100 is required.")
        },
        FromNative = RealFromNative,
        Prerequisites =
        [
            Requires(ICapImageEdgeFillAllSides, "ICAP_IMAGEEDGEFILLALLSIDES", NativeValueType.Boolean, true)
        ],
        RequireExactType = true,
        Source = Source
    };

    private static NativeSettingBinding Named(string key, int id, string name, int order,
        IReadOnlyDictionary<string, int> names, NativeValueType type = NativeValueType.UInt16) => new()
    {
        Key = key,
        Protocol = Protocol,
        NativeId = id,
        NativeName = name,
        ValueType = type,
        Order = order,
        ToNative = NamedToNative(names),
        FromNative = NamedFromNative(names),
        RequireExactType = true,
        Source = Source
    };

    private static NativeSettingBinding Integer(string key, int id, string name, int order, int minimum,
        int maximum, NativePrerequisite? prerequisite = null, NativeValueType type = NativeValueType.Int32) => new()
    {
        Key = key,
        Protocol = Protocol,
        NativeId = id,
        NativeName = name,
        ValueType = type,
        Order = order,
        ToNative = IntegerToNative(minimum, maximum),
        FromNative = IntegerFromNative,
        Prerequisites = prerequisite == null ? [] : [prerequisite],
        RequireExactType = true,
        Source = Source
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
        FromNative = BooleanFromNative,
        RequireExactType = true,
        Source = Source
    };
}
