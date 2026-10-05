using NAPS2.Scan;
using NAPS2.Scan.Internal;
using NAPS2.Scan.Internal.Twain;
using Xunit;
using static NAPS2.Sdk.Tests.Scan.KeyedSettingNegotiatorTests;

namespace NAPS2.Sdk.Tests.Scan;

public class KodakTwainBindingsTests
{
    private static readonly DriverDeviceIdentity KodakIdentity = new()
    {
        Manufacturer = "Kodak Alaris Inc.",
        ProductName = "KODAK Scanner: i4250",
        DriverVersion = "7.60"
    };

    [Fact]
    public void KodakBindingsAreOfferedOnlyToKodakSources()
    {
        var kodak = Candidates(KodakIdentity);
        var other = Candidates(new DriverDeviceIdentity { Manufacturer = "Fujitsu", DriverVersion = "9.0" });
        var unknown = Candidates(null);

        Assert.Contains(DriverSettingKeys.DocumentType, kodak.Keys);
        Assert.DoesNotContain(DriverSettingKeys.DocumentType, other.Keys);
        Assert.DoesNotContain(DriverSettingKeys.DocumentType, unknown.Keys);
    }

    [Theory]
    [InlineData("7.56", true)]
    [InlineData("7.60", true)]
    [InlineData("7.55", false)]
    [InlineData("4.10", false)]
    [InlineData("", false)]
    [InlineData("unknown", false)]
    public void SidesDifferentIsOnlyUsedFromDriver756(string version, bool offered)
    {
        var candidates = Candidates(KodakIdentity with { DriverVersion = version });

        Assert.Equal(offered, candidates.ContainsKey(DriverSettingKeys.SameSettingsBothSides));
    }

    [Fact]
    public void EveryKodakBindingRequiresTheDocumentedTypeAndCitesItsSource()
    {
        Assert.All(KodakTwainBindings.Set.Bindings, x =>
        {
            Assert.True(x.Binding.RequireExactType, x.Binding.Key);
            Assert.StartsWith("Kodak Alaris kdscust.h", x.Binding.Source);
            Assert.True(x.Binding.NativeId >= 0x8000, x.Binding.Key);
        });
    }

    [Theory]
    [InlineData(DriverSettingKeys.DocumentType, 0x80AC)]
    [InlineData(DriverSettingKeys.MediaType, 0x80B6)]
    [InlineData(DriverSettingKeys.EdgeFill, 0x8095)]
    [InlineData(DriverSettingKeys.MultifeedSensitivity, 0x8027)]
    [InlineData(DriverSettingKeys.SameSettingsBothSides, 0x80B7)]
    [InlineData(DriverSettingKeys.BlankPageContent, 0x80C4)]
    public void KodakIdsMatchTheHeader(string key, int id)
    {
        Assert.Equal(id, KodakTwainBindings.Set.Bindings.Single(x => x.Binding.Key == key).Binding.NativeId);
    }

    [Fact]
    public void CustomAmountSelectsTheCustomPresetFirst()
    {
        var access = new FakeAccess();

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.AutomaticColorAmount, DriverSettingValue.FromInteger(50)))));

        Assert.Equal(["ICAP_AUTOCOLORCONTENT", "ICAP_AUTOCOLORAMOUNT"], access.Writes);
        Assert.Equal(4, access.Values["ICAP_AUTOCOLORCONTENT"]);
        Assert.Equal(DriverProcessingStatus.Applied, result.Status);
        Assert.Equal("Kodak Alaris kdscust.h", result.Evidence);
        Assert.Equal(DriverExecutionLocation.Unknown, result.ExecutionLocation);
    }

    [Fact]
    public void ColorBalanceProceedsWhenTheOptionalModeIsUnsupported()
    {
        var access = new FakeAccess
        {
            States = { ["ICAP_COLORBALANCEMODE"] = DriverProcessingCapabilityState.Unsupported }
        };

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.ColorBalanceRed, DriverSettingValue.FromInteger(-100)))));

        Assert.Equal(["ICAP_COLORBALANCERED"], access.Writes);
        Assert.Equal(DriverProcessingStatus.Applied, result.Status);
    }

    [Fact]
    public void ARequiredPrerequisiteThatFailsStopsTheSetting()
    {
        var access = new FakeAccess { WriteResults = { ["CAP_BLANKPAGEMODE"] = NativeWriteStatus.Rejected } };

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.BlankPageContent, DriverSettingValue.FromInteger(5)))));

        Assert.Equal(DriverProcessingStatus.Failed, result.Status);
        Assert.Contains("CAP_BLANKPAGEMODE", result.Message);
        Assert.DoesNotContain("CAP_BLANKPAGECONTENT", access.Writes);
    }

    [Fact]
    public void TheStandardCapabilityIsPreferredWhenTheSourceSupportsIt()
    {
        var access = new FakeAccess();

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.MultifeedSensitivity, DriverSettingValue.FromText("high")))));

        Assert.Equal(["CAP_DOUBLEFEEDDETECTIONSENSITIVITY"], access.Writes);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public void KodakSensitivityIsUsedWhenTheStandardOneIsUnsupportedOrCannotExpressTheValue()
    {
        var unsupported = new FakeAccess
        {
            States = { ["CAP_DOUBLEFEEDDETECTIONSENSITIVITY"] = DriverProcessingCapabilityState.Unsupported }
        };
        var standardFine = new FakeAccess();

        Apply(unsupported, Options((DriverSettingKeys.MultifeedSensitivity, DriverSettingValue.FromText("high"))));
        // "off" has no standard value, so even a source with the standard capability uses the Kodak one.
        Apply(standardFine, Options((DriverSettingKeys.MultifeedSensitivity, DriverSettingValue.FromText("off"))));

        Assert.Equal(["CAP_ULTRASONICSENSITIVITY"], unsupported.Writes);
        Assert.Equal(3, unsupported.Values["CAP_ULTRASONICSENSITIVITY"]);
        Assert.Equal(["CAP_ULTRASONICSENSITIVITY"], standardFine.Writes);
        Assert.Equal(0, standardFine.Values["CAP_ULTRASONICSENSITIVITY"]);
    }

    [Fact]
    public void SameSettingsOnBothSidesWritesSidesDifferentFalseAfterEverythingElse()
    {
        var access = new FakeAccess();

        var results = Apply(access, Options(
            (DriverSettingKeys.SameSettingsBothSides, DriverSettingValue.FromBoolean(true)),
            (DriverSettingKeys.DocumentType, DriverSettingValue.FromText("text")),
            (DriverSettingKeys.EdgeFill, DriverSettingValue.FromText("white"))));

        Assert.Equal("CAP_SIDESDIFFERENT", access.Writes[^1]);
        Assert.Equal(false, access.Values["CAP_SIDESDIFFERENT"]);
        Assert.All(results, x => Assert.Equal(DriverProcessingStatus.Applied, x.Status));
        Assert.Equal(true, results[0].EffectiveValue);
    }

    [Fact]
    public void EdgeWidthLinksAllSidesBeforeWritingTheTopEdge()
    {
        var access = new FakeAccess();

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.EdgeFillWidth, DriverSettingValue.FromReal(0.125)))));

        Assert.Equal(["ICAP_IMAGEEDGEFILLALLSIDES", "ICAP_IMAGEEDGETOP"], access.Writes);
        Assert.Equal(true, access.Values["ICAP_IMAGEEDGEFILLALLSIDES"]);
        Assert.Equal(DriverProcessingStatus.Applied, result.Status);
    }

    [Fact]
    public void VendorOnlyKeysOnOtherSourcesReportWhyTheyAreUnsupported()
    {
        var access = new FakeAccess();

        var result = Assert.Single(KeyedSettingNegotiator.Apply(access, "TWAIN",
            Candidates(new DriverDeviceIdentity { Manufacturer = "Canon" }), TwainSettingBindings.Gaps,
            Options((DriverSettingKeys.DocumentType, DriverSettingValue.FromText("text")))));

        Assert.Equal(DriverProcessingStatus.Unsupported, result.Status);
        Assert.Contains("verified vendor binding", result.Message);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void SettingsUnlockedByTheirPrerequisiteAreAppliedNotRejectedUpFront()
    {
        // Observed on the i4250: ICAP_AUTOCOLORAMOUNT is not exposed until ICAP_AUTOCOLORCONTENT is custom.
        var access = new FakeAccess
        {
            States = { ["ICAP_AUTOCOLORAMOUNT"] = DriverProcessingCapabilityState.Unsupported }
        };
        access.AfterWrite = (name, values) =>
        {
            if (name == "ICAP_AUTOCOLORCONTENT" && Equals(values[name], 4))
            {
                access.States.Remove("ICAP_AUTOCOLORAMOUNT");
            }
        };

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.AutomaticColorAmount, DriverSettingValue.FromInteger(50)))));

        Assert.Equal(DriverProcessingStatus.Applied, result.Status);
        Assert.Equal(["ICAP_AUTOCOLORCONTENT", "ICAP_AUTOCOLORAMOUNT"], access.Writes);
    }

    [Fact]
    public void SettingsUnlockedByAnEarlierSettingAreAppliedNotRejectedUpFront()
    {
        // Observed on the i4250: edge widths are not exposed while edge fill is automatic.
        var access = new FakeAccess
        {
            States =
            {
                ["ICAP_IMAGEEDGEFILLALLSIDES"] = DriverProcessingCapabilityState.Unsupported,
                ["ICAP_IMAGEEDGETOP"] = DriverProcessingCapabilityState.Unsupported
            }
        };
        access.AfterWrite = (name, values) =>
        {
            if (name == "ICAP_IMAGEEDGEFILL" && Equals(values[name], 1))
            {
                access.States.Clear();
            }
        };

        var results = Apply(access, Options(
            (DriverSettingKeys.EdgeFillWidth, DriverSettingValue.FromReal(0.2)),
            (DriverSettingKeys.EdgeFill, DriverSettingValue.FromText("white"))));

        Assert.All(results, x => Assert.Equal(DriverProcessingStatus.Applied, x.Status));
        Assert.Equal(["ICAP_IMAGEEDGEFILL", "ICAP_IMAGEEDGEFILLALLSIDES", "ICAP_IMAGEEDGETOP"], access.Writes);
    }

    [Fact]
    public void ASettingThatStaysUnexposedIsReportedUnsupportedWithoutWriting()
    {
        var access = new FakeAccess
        {
            States = { ["ICAP_BACKGROUNDADJUSTAPPLYTO"] = DriverProcessingCapabilityState.Unsupported }
        };

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.BackgroundSmoothingTarget, DriverSettingValue.FromText("all")))));

        Assert.Equal(DriverProcessingStatus.Unsupported, result.Status);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void SharpeningAcceptsTheInt32TypeTheI4250Reports()
    {
        var sharpening = KodakTwainBindings.Set.Bindings.Single(x => x.Binding.Key == DriverSettingKeys.Sharpening)
            .Binding;

        Assert.Equal(NativeValueType.UInt32, sharpening.ValueType);
        Assert.Equal([NativeValueType.Int32], sharpening.AlternativeTypes);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<NativeSettingBinding>> Candidates(
        DriverDeviceIdentity? identity) =>
        KeyedSettingNegotiator.Candidates(TwainSettingBindings.Bindings, [KodakTwainBindings.Set], identity);

    private static List<DriverProcessingSetting> Apply(FakeAccess access, DriverProcessingOptions options) =>
        KeyedSettingNegotiator.Apply(access, "TWAIN", Candidates(KodakIdentity), TwainSettingBindings.Gaps, options)
            .ToList();
}
