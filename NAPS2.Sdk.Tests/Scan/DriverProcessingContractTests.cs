using System.Collections.Immutable;
using NAPS2.Scan;
using NAPS2.Serialization;
using Xunit;

namespace NAPS2.Sdk.Tests.Scan;

public class DriverProcessingContractTests
{
    [Fact]
    public void ProcessingRequestsRoundTripAndKeepUnsetDistinctFromFalse()
    {
        var original = new ScanOptions
        {
            TwainOptions = new TwainOptions
            {
                ProcessingOptions = new DriverProcessingOptions
                {
                    Brightness = 125,
                    Contrast = null,
                    RotationDegrees = 90,
                    AutomaticOrientation = false,
                    Deskew = true,
                    AutomaticColorDetection = DriverColorDetectionMode.ColorOrGrayscale,
                    AutomaticBlankPageDetection = null
                }
            },
            WiaOptions = new WiaOptions
            {
                ProcessingOptions = new DriverProcessingOptions
                {
                    AutomaticColorDetection = DriverColorDetectionMode.Automatic,
                    AutomaticBlankPageDetection = false
                }
            }
        };

        var serializer = new XmlSerializer<ScanOptions>();
        var document = serializer.SerializeToXDocument(original);
        var copy = serializer.DeserializeFromXDocument(document);

        Assert.NotNull(copy);
        Assert.Equal(125, copy.TwainOptions.ProcessingOptions.Brightness);
        Assert.Null(copy.TwainOptions.ProcessingOptions.Contrast);
        Assert.Equal(90, copy.TwainOptions.ProcessingOptions.RotationDegrees);
        Assert.False(copy.TwainOptions.ProcessingOptions.AutomaticOrientation);
        Assert.True(copy.TwainOptions.ProcessingOptions.Deskew);
        Assert.Equal(DriverColorDetectionMode.ColorOrGrayscale,
            copy.TwainOptions.ProcessingOptions.AutomaticColorDetection);
        Assert.Null(copy.TwainOptions.ProcessingOptions.AutomaticBlankPageDetection);
        Assert.Equal(DriverColorDetectionMode.Automatic,
            copy.WiaOptions.ProcessingOptions.AutomaticColorDetection);
        Assert.False(copy.WiaOptions.ProcessingOptions.AutomaticBlankPageDetection);
        Assert.True(copy.TwainOptions.ProcessingOptions.HasRequests);
        Assert.True(copy.WiaOptions.ProcessingOptions.HasRequests);
    }

    [Fact]
    public void ProcessingRequestAliasesShareTheCanonicalValuesWithoutAddingXmlMembers()
    {
        var options = new DriverProcessingOptions
        {
            AutoDeskew = true,
            RotateDegrees = 180
        };

        var document = new XmlSerializer<DriverProcessingOptions>().SerializeToXDocument(options);
        var elementNames = document.Root!.Elements().Select(x => x.Name.LocalName).ToArray();

        Assert.True(options.Deskew);
        Assert.Equal(180, options.RotationDegrees);
        Assert.Contains("Deskew", elementNames);
        Assert.Contains("RotationDegrees", elementNames);
        Assert.DoesNotContain("AutoDeskew", elementNames);
        Assert.DoesNotContain("RotateDegrees", elementNames);
    }

    [Fact]
    public void CapabilityStatesExposeReadOnlyWritableAndQueryFailure()
    {
        var readOnly = new DriverProcessingNumericCaps
        {
            State = DriverProcessingCapabilityState.ReadOnly,
            Minimum = -1000,
            Maximum = 1000,
            Step = 1,
            Current = 25,
            Default = 0
        };
        var writable = new DriverProcessingBooleanCaps
        {
            State = DriverProcessingCapabilityState.Writable,
            Current = true,
            Default = false
        };
        var queryFailed = new DriverProcessingColorCaps
        {
            State = DriverProcessingCapabilityState.QueryFailed
        };

        Assert.True(readOnly.IsSupported);
        Assert.False(readOnly.IsWritable);
        Assert.True(writable.IsSupported);
        Assert.True(writable.IsWritable);
        Assert.False(queryFailed.IsSupported);
        Assert.False(queryFailed.IsWritable);
    }

    [Fact]
    public void CapabilitiesRoundTripNumericAndColorDetails()
    {
        var original = new ScanCaps
        {
            DriverProcessingCaps = new DriverProcessingCaps
            {
                Brightness = new DriverProcessingNumericCaps
                {
                    State = DriverProcessingCapabilityState.Writable,
                    Minimum = -1000,
                    Maximum = 1000,
                    Step = 5,
                    Current = 10,
                    Default = 0
                },
                RotationDegrees = new DriverProcessingNumericCaps
                {
                    State = DriverProcessingCapabilityState.ReadOnly,
                    Current = 90
                },
                AutomaticColorDetection = new DriverProcessingColorCaps
                {
                    State = DriverProcessingCapabilityState.Writable,
                    SupportedModes = ImmutableList.Create(
                        DriverColorDetectionMode.Off,
                        DriverColorDetectionMode.Automatic,
                        DriverColorDetectionMode.ColorOrGrayscale),
                    Current = DriverColorDetectionMode.Automatic,
                    Default = DriverColorDetectionMode.Off
                },
                AutomaticBlankPageDetection = new DriverProcessingBooleanCaps
                {
                    State = DriverProcessingCapabilityState.QueryFailed
                }
            }
        };

        var serializer = new XmlSerializer<ScanCaps>();
        var document = serializer.SerializeToXDocument(original);
        var copy = serializer.DeserializeFromXDocument(document);

        Assert.NotNull(copy?.DriverProcessingCaps);
        var caps = copy!.DriverProcessingCaps!;
        Assert.NotNull(caps.Brightness);
        var brightness = caps.Brightness!;
        Assert.Equal(DriverProcessingCapabilityState.Writable, brightness.State);
        Assert.Equal(-1000, brightness.Minimum);
        Assert.Equal(1000, brightness.Maximum);
        Assert.Equal(5, brightness.Step);
        Assert.Equal(10, brightness.Current);
        Assert.Equal(0, brightness.Default);

        Assert.NotNull(caps.AutomaticColorDetection);
        var color = caps.AutomaticColorDetection!;
        Assert.Equal(DriverProcessingCapabilityState.Writable, color.State);
        Assert.Equal(
            [
                DriverColorDetectionMode.Off,
                DriverColorDetectionMode.Automatic,
                DriverColorDetectionMode.ColorOrGrayscale
            ],
            color.SupportedModes);
        Assert.Equal(DriverColorDetectionMode.Automatic, color.Current);
        Assert.Equal(DriverColorDetectionMode.Off, color.Default);

        Assert.NotNull(caps.AutomaticBlankPageDetection);
        var blank = caps.AutomaticBlankPageDetection!;
        Assert.Equal(DriverProcessingCapabilityState.QueryFailed, blank.State);
        Assert.Null(blank.Current);
        Assert.Null(blank.Default);
    }
}
