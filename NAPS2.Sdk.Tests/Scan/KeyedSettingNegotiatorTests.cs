using NAPS2.Scan;
using NAPS2.Scan.Internal;
using NAPS2.Scan.Internal.Twain;
using NAPS2.Scan.Internal.Wia;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class KeyedSettingNegotiatorTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<NativeSettingBinding>> Twain =
        KeyedSettingNegotiator.Candidates(TwainSettingBindings.Bindings, [], null);

    private static readonly IReadOnlyDictionary<string, string> TwainGaps = TwainSettingBindings.Gaps;

    [Fact]
    public void EveryKeyIsBoundOrADocumentedGapOnBothProtocols()
    {
        var keys = typeof(DriverSettingKeys).GetFields().Select(x => (string) x.GetValue(null)!).ToList();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.True(TwainSettingBindings.Bindings.ContainsKey(key) ^ TwainGaps.ContainsKey(key), $"TWAIN: {key}");
            Assert.True(WiaSettingBindings.Bindings.ContainsKey(key) ^ WiaSettingBindings.Gaps.ContainsKey(key),
                $"WIA: {key}");
        }
    }

    [Fact]
    public void WritesFollowDependencyOrderAndResultsFollowRequestOrder()
    {
        var access = new FakeAccess();
        var options = Options(
            (DriverSettingKeys.Threshold, DriverSettingValue.FromInteger(140)),
            (DriverSettingKeys.BlackAndWhiteMethod, DriverSettingValue.FromText("fixed")),
            (DriverSettingKeys.PaperHandling, DriverSettingValue.FromText("fragile")));

        var results = Apply(access, options);

        Assert.Equal(["ICAP_BITDEPTHREDUCTION", "ICAP_THRESHOLD", "CAP_PAPERHANDLING"], access.Writes);
        Assert.Equal([DriverSettingKeys.Threshold, DriverSettingKeys.BlackAndWhiteMethod,
            DriverSettingKeys.PaperHandling], results.Select(x => x.Name));
        Assert.All(results, x => Assert.Equal(DriverProcessingStatus.Applied, x.Status));
        Assert.Equal("TWAIN ICAP_THRESHOLD (0x1123)", results[0].Binding);
        Assert.Equal(140L, results[0].EffectiveValue);
        Assert.Equal(DriverExecutionLocation.Unknown, results[0].ExecutionLocation);
    }

    [Fact]
    public void ReadBackHappensAfterAllWritesSoALaterWriteCanAdjustAnEarlierOne()
    {
        var access = new FakeAccess
        {
            // A source that resets the reduction method when the threshold is written.
            AfterWrite = (name, values) =>
            {
                if (name == "ICAP_THRESHOLD") values["ICAP_BITDEPTHREDUCTION"] = 0;
            }
        };
        var options = Options(
            (DriverSettingKeys.BlackAndWhiteMethod, DriverSettingValue.FromText("adaptive")),
            (DriverSettingKeys.Threshold, DriverSettingValue.FromInteger(128)));

        var results = Apply(access, options);

        var method = results.Single(x => x.Name == DriverSettingKeys.BlackAndWhiteMethod);
        Assert.Equal(DriverProcessingStatus.Adjusted, method.Status);
        Assert.Equal("fixed", method.EffectiveValue);
        Assert.Equal(DriverProcessingStatus.Applied,
            results.Single(x => x.Name == DriverSettingKeys.Threshold).Status);
    }

    [Fact]
    public void Fix32RoundingWithinToleranceStillCountsAsApplied()
    {
        var access = new FakeAccess { AfterWrite = (name, values) => values[name] = 2.2000045 };

        var result = Assert.Single(Apply(access, Options((DriverSettingKeys.Gamma, DriverSettingValue.FromReal(2.2)))));

        Assert.Equal(DriverProcessingStatus.Applied, result.Status);
    }

    [Fact]
    public void UnreadableValueAfterAcceptedWriteIsUnknownNotApplied()
    {
        var access = new FakeAccess { Unreadable = { "ICAP_GAMMA" } };

        var result = Assert.Single(Apply(access, Options((DriverSettingKeys.Gamma, DriverSettingValue.FromReal(2.2)))));

        Assert.Equal(DriverProcessingStatus.Unknown, result.Status);
    }

    [Theory]
    [InlineData(DriverProcessingCapabilityState.Unsupported, DriverProcessingStatus.Unsupported)]
    [InlineData(DriverProcessingCapabilityState.ReadOnly, DriverProcessingStatus.Rejected)]
    [InlineData(DriverProcessingCapabilityState.QueryFailed, DriverProcessingStatus.Failed)]
    public void ProbeStateDecidesWithoutWriting(DriverProcessingCapabilityState state, DriverProcessingStatus expected)
    {
        var access = new FakeAccess { States = { ["ICAP_GAMMA"] = state } };

        var result = Assert.Single(Apply(access, Options((DriverSettingKeys.Gamma, DriverSettingValue.FromReal(2.2)))));

        Assert.Equal(expected, result.Status);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void ValuesTheDriverDoesNotOfferAreRejectedWithoutWriting()
    {
        var access = new FakeAccess
        {
            Offered = { ["CAP_PAPERHANDLING"] = [0, 2] },
            Ranges = { ["ICAP_GAMMA"] = (0.5, 4.0) }
        };
        var options = Options(
            (DriverSettingKeys.PaperHandling, DriverSettingValue.FromText("fragile")),
            (DriverSettingKeys.Gamma, DriverSettingValue.FromReal(5.0)));

        var results = Apply(access, options);

        Assert.All(results, x => Assert.Equal(DriverProcessingStatus.Rejected, x.Status));
        Assert.Contains("fragile", results[0].Message);
        Assert.Contains("0.5", results[1].Message);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void ListSettingsAreCheckedAgainstTheirSeparateValuesCapability()
    {
        var access = new FakeAccess { Offered = { ["ICAP_BARCODESEARCHPRIORITIES"] = [20, 4] } };
        var options = Options((DriverSettingKeys.BarcodeTypes, DriverSettingValue.FromText("qr,pdf417")));

        var result = Assert.Single(Apply(access, options));

        Assert.Equal(DriverProcessingStatus.Rejected, result.Status);
        Assert.Contains("pdf417", result.Message);
    }

    [Fact]
    public void ListSettingsRoundTripInPriorityOrder()
    {
        var access = new FakeAccess();

        var result = Assert.Single(Apply(access,
            Options((DriverSettingKeys.BarcodeTypes, DriverSettingValue.FromText("qr, code128")))));

        Assert.Equal(DriverProcessingStatus.Applied, result.Status);
        Assert.Equal("qr,code128", result.EffectiveValue);
        Assert.Equal(new object[] { (ushort) 20, (ushort) 4 },
            (object[]) access.Values["ICAP_BARCODESEARCHPRIORITIES"]);
    }

    [Theory]
    [InlineData((int) NativeWriteStatus.Rejected, DriverProcessingStatus.Rejected)]
    [InlineData((int) NativeWriteStatus.Unsupported, DriverProcessingStatus.Unsupported)]
    [InlineData((int) NativeWriteStatus.Failed, DriverProcessingStatus.Failed)]
    public void WriteFailuresKeepTheirStatus(int write, DriverProcessingStatus expected)
    {
        var access = new FakeAccess { WriteResults = { ["ICAP_GAMMA"] = (NativeWriteStatus) write } };

        var result = Assert.Single(Apply(access, Options((DriverSettingKeys.Gamma, DriverSettingValue.FromReal(2.2)))));

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void GapsUnknownKeysAndBadValuesAreReportedWithoutTouchingTheDriver()
    {
        var access = new FakeAccess();
        var options = Options(
            (DriverSettingKeys.BarcodeMaximumCount, DriverSettingValue.FromInteger(5)),
            ("somethingElse", DriverSettingValue.FromBoolean(true)),
            (DriverSettingKeys.ImageFilter, DriverSettingValue.FromText("sharp")),
            (DriverSettingKeys.Threshold, DriverSettingValue.FromInteger(300)));

        var results = Apply(access, options);

        Assert.Equal(DriverProcessingStatus.Unsupported, results[0].Status);
        Assert.Contains("maximum barcode count", results[0].Message);
        Assert.Equal(DriverProcessingStatus.Unsupported, results[1].Status);
        Assert.Equal(DriverProcessingStatus.Rejected, results[2].Status);
        Assert.Equal(DriverProcessingStatus.Rejected, results[3].Status);
        Assert.Empty(access.Writes);
    }

    [Fact]
    public void CapsTranslateOfferedValuesAndListGaps()
    {
        var access = new FakeAccess
        {
            Offered = { ["CAP_PAPERHANDLING"] = [0, 1, 99] },
            States = { ["ICAP_GAMMA"] = DriverProcessingCapabilityState.Unsupported }
        };

        var caps = KeyedSettingNegotiator.QueryCaps(access, Twain, TwainGaps);

        var paper = caps.Single(x => x.Key == DriverSettingKeys.PaperHandling);
        Assert.Equal(["normal", "fragile"], paper.Values!.Select(x => x.TextValue));
        Assert.Equal(DriverProcessingCapabilityState.Unsupported,
            caps.Single(x => x.Key == DriverSettingKeys.Gamma).State);
        var gap = caps.Single(x => x.Key == DriverSettingKeys.BarcodeMaximumCount);
        Assert.Equal(DriverProcessingCapabilityState.Unsupported, gap.State);
        Assert.Null(gap.Binding);
    }

    [Fact]
    public void WiaBooleansUseTheirDocumentedIntegers()
    {
        var access = new FakeAccess();

        var results = KeyedSettingNegotiator.Apply(access, "WIA",
            KeyedSettingNegotiator.Candidates(WiaSettingBindings.Bindings, [], null), WiaSettingBindings.Gaps,
            Options(
                (DriverSettingKeys.LongDocument, DriverSettingValue.FromBoolean(true)),
                (DriverSettingKeys.MultifeedResponse, DriverSettingValue.FromText("off")),
                (DriverSettingKeys.Gamma, DriverSettingValue.FromReal(2.2))));

        Assert.Equal(1, access.Values["WIA_IPS_LONG_DOCUMENT"]);
        Assert.Equal(0, access.Values["WIA_IPS_MULTI_FEED"]);
        Assert.Equal(DriverProcessingStatus.Applied, results[0].Status);
        Assert.Equal(DriverProcessingStatus.Unsupported, results[2].Status);
    }

    private static List<DriverProcessingSetting> Apply(FakeAccess access, DriverProcessingOptions options) =>
        KeyedSettingNegotiator.Apply(access, "TWAIN", Twain, TwainGaps, options).ToList();

    internal static DriverProcessingOptions Options(params (string Key, DriverSettingValue Value)[] settings) => new()
    {
        Settings = settings.Select(x => new DriverSettingRequest { Key = x.Key, Value = x.Value }).ToList()
    };

    internal sealed class FakeAccess : IDriverSettingAccess
    {
        public Dictionary<string, object> Values { get; } = new();
        public Dictionary<string, DriverProcessingCapabilityState> States { get; } = new();
        public Dictionary<string, IReadOnlyList<object>> Offered { get; } = new();
        public Dictionary<string, (double Minimum, double Maximum)> Ranges { get; } = new();
        public Dictionary<string, NativeWriteStatus> WriteResults { get; } = new();
        public HashSet<string> Unreadable { get; } = new();
        public List<string> Writes { get; } = new();
        public Action<string, Dictionary<string, object>>? AfterWrite { get; init; }

        public NativeProbe Probe(NativeSettingBinding binding)
        {
            var name = binding.NativeName;
            return new NativeProbe
            {
                State = States.TryGetValue(name, out var state) ? state : DriverProcessingCapabilityState.Writable,
                Values = Offered.TryGetValue(name, out var values) ? values : null,
                Minimum = Ranges.TryGetValue(name, out var range) ? range.Minimum : null,
                Maximum = Ranges.TryGetValue(name, out range) ? range.Maximum : null,
                Current = Values.GetValueOrDefault(name)
            };
        }

        public NativeWriteResult Write(NativeSettingBinding binding, object nativeValue)
        {
            var name = binding.NativeName;
            Writes.Add(name);
            if (WriteResults.TryGetValue(name, out var status))
            {
                return new NativeWriteResult(status, "fake failure");
            }
            Values[name] = nativeValue;
            AfterWrite?.Invoke(name, Values);
            return new NativeWriteResult(NativeWriteStatus.Accepted, null);
        }

        public object? Read(NativeSettingBinding binding) =>
            Unreadable.Contains(binding.NativeName) ? null : Values.GetValueOrDefault(binding.NativeName);
    }
}
