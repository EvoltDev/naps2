#if !MACOS
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NAPS2.Wia;

namespace NAPS2.Scan.Internal.Wia;

/// <summary>
/// Reads and applies the optional processing properties exposed by a WIA item.
/// </summary>
/// <remarks>
/// WIA properties are deliberately handled here instead of in <see cref="WiaScanDriver"/> so capability probing
/// and acquisition use the same property ids, value conversion, and readback rules. A property can be present in a
/// driver's property collection but still be unusable: the access flags, property attributes, current value, and
/// write/readback result are all considered before a setting is reported as applied.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WiaSourceConfiguration
{
    // WIA_IPS_* values which are not included in all versions of the standalone NAPS2.Wia wrapper.
    private const int RotationPropertyId = 6157; // WIA_IPS_ROTATION
    private const int AutoDeskewPropertyId = 3107; // WIA_IPS_AUTO_DESKEW
    private const int PageSizePropertyId = 3097; // WIA_IPS_PAGE_SIZE
    private const int BlankPagesPropertyId = 4167; // WIA_IPS_BLANK_PAGES
    private const int AutoCropPropertyId = 4170; // WIA_IPS_AUTO_CROP

    // WIA_IPS_* and WIA_IPA_* values.
    private const int WiaPageCustom = 2;
    private const int WiaPageAuto = 100;
    private const int WiaDataThreshold = 0;
    private const int WiaDataGrayscale = 2;
    private const int WiaDataColor = 3;
    private const int WiaDataAuto = 100;

    // WIA_AUTO_DESKEW_* values. WIA uses zero for ON for this property.
    private const int WiaAutoDeskewOn = 0;
    private const int WiaAutoDeskewOff = 1;

    private readonly WiaDevice _device;
    private readonly WiaItemBase _item;
    private readonly DriverProcessingOptions _options;
    private readonly BitDepth _bitDepth;
    private readonly ILogger _logger;
    private bool _propertyCollectionQueryFailed;

    public WiaSourceConfiguration(WiaDevice device, WiaItemBase item, DriverProcessingOptions options,
        BitDepth bitDepth, ILogger logger)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _options = options ?? new DriverProcessingOptions();
        _bitDepth = bitDepth;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the processing capabilities for the selected WIA source.
    /// </summary>
    public DriverProcessingCaps GetCapabilities()
    {
        return new DriverProcessingCaps
        {
            Brightness = GetNumericCaps(WiaPropertyId.IPS_BRIGHTNESS, RotationValueIdentity),
            Contrast = GetNumericCaps(WiaPropertyId.IPS_CONTRAST, RotationValueIdentity),
            RotationDegrees = GetNumericCaps(RotationPropertyId, ToRotationDegrees),
            AutomaticOrientation = GetBooleanCaps(
                FindNamedProperty("AutomaticOrientation", "AutoOrientation", "OrientationDetection", "AutoRotate",
                    "AutoRotation"),
                SupportsAnyBooleanValue),
            Deskew = GetBooleanCaps(AutoDeskewPropertyId, SupportsAutoDeskewValue,
                value => value == WiaAutoDeskewOn),
            AutomaticBrightness = GetBooleanCaps(
                FindNamedProperty("AutomaticBrightness", "AutoBrightness", "AutomaticExposure", "AutoExposure",
                    "AutomaticLevel", "AutoLevel", "AutomaticTone", "AutoTone"),
                SupportsAnyBooleanValue),
            AutomaticPageSize = GetBooleanCaps(PageSizePropertyId, SupportsAutoPageSizeValue,
                value => value == WiaPageAuto),
            AutomaticBorderDetection = GetBooleanCaps(
                FindNamedProperty("AutomaticBorderDetection", "AutoBorderDetection", "BorderDetection",
                    "AutomaticBorder", "AutoBorder", "DocumentBoundaryDetection", "DocumentBoundary",
                    "BoundaryDetection", "AutoDetectBounds"), SupportsAnyBooleanValue),
            AutomaticCrop = GetBooleanCaps(AutoCropPropertyId, SupportsAutoCropValue),
            AutomaticColorDetection = GetColorCaps(),
            AutomaticBlankPageDetection = GetBooleanCaps(BlankPagesPropertyId, SupportsBlankPageValue)
        };
    }

    /// <summary>
    /// Applies requested settings and returns the values verified from the WIA property collection.
    /// </summary>
    public DriverProcessingResult Apply()
    {
        var result = new ResultBuilder();

        if (_options.Brightness is { } brightness)
        {
            ApplyNumeric(result, nameof(DriverProcessingOptions.Brightness), brightness,
                WiaPropertyId.IPS_BRIGHTNESS, RotationValueIdentity, value => (int) Math.Round(value), -1000, 1000);
        }

        if (_options.Contrast is { } contrast)
        {
            ApplyNumeric(result, nameof(DriverProcessingOptions.Contrast), contrast,
                WiaPropertyId.IPS_CONTRAST, RotationValueIdentity, value => (int) Math.Round(value), -1000, 1000);
        }

        if (_options.RotationDegrees is { } rotation)
        {
            ApplyNumeric(result, nameof(DriverProcessingOptions.RotationDegrees), rotation,
                RotationPropertyId, ToRotationDegrees, ToRotationRaw, 0, 360);
        }

        if (_options.AutomaticOrientation is { } automaticOrientation)
        {
            ApplyBoolean(result, nameof(DriverProcessingOptions.AutomaticOrientation), automaticOrientation,
                FindNamedProperty("AutomaticOrientation", "AutoOrientation", "OrientationDetection", "AutoRotate",
                    "AutoRotation"),
                SupportsAnyBooleanValue, 1, 0);
        }

        if (_options.Deskew is { } deskew)
        {
            ApplyBoolean(result, nameof(DriverProcessingOptions.Deskew), deskew, AutoDeskewPropertyId,
                SupportsAutoDeskewValue, WiaAutoDeskewOn, WiaAutoDeskewOff, false,
                value => value == WiaAutoDeskewOn);
        }

        if (_options.AutomaticBrightness is { } automaticBrightness)
        {
            ApplyBoolean(result, nameof(DriverProcessingOptions.AutomaticBrightness), automaticBrightness,
                FindNamedProperty("AutomaticBrightness", "AutoBrightness", "AutomaticExposure", "AutoExposure",
                    "AutomaticLevel", "AutoLevel", "AutomaticTone", "AutoTone"),
                SupportsAnyBooleanValue, 1, 0);
        }

        if (_options.AutomaticPageSize is { } automaticPageSize)
        {
            ApplyBoolean(result, nameof(DriverProcessingOptions.AutomaticPageSize), automaticPageSize,
                PageSizePropertyId, SupportsAutoPageSizeValue, WiaPageAuto, WiaPageCustom, false,
                value => value == WiaPageAuto);
        }

        if (_options.AutomaticBorderDetection is { } automaticBorderDetection)
        {
            ApplyBoolean(result, nameof(DriverProcessingOptions.AutomaticBorderDetection), automaticBorderDetection,
                FindNamedProperty("AutomaticBorderDetection", "AutoBorderDetection", "BorderDetection",
                    "AutomaticBorder", "AutoBorder", "DocumentBoundaryDetection", "DocumentBoundary",
                    "BoundaryDetection", "AutoDetectBounds"), SupportsAnyBooleanValue, 1, 0);
        }

        if (_options.AutomaticCrop is { } automaticCrop)
        {
            ApplyBoolean(result, nameof(DriverProcessingOptions.AutomaticCrop), automaticCrop, AutoCropPropertyId,
                SupportsAutoCropValue, 1, 0, true);
        }

        if (_options.AutomaticColorDetection is { } automaticColor)
        {
            ApplyColor(result, automaticColor);
        }

        if (_options.AutomaticBlankPageDetection is { } automaticBlank)
        {
            ApplyBoolean(result, nameof(DriverProcessingOptions.AutomaticBlankPageDetection), automaticBlank,
                BlankPagesPropertyId, SupportsBlankPageValue, 1, 0, true);
        }

        return result.Build();
    }

    /// <summary>
    /// Creates a result for a configuration owned by the WIA native UI. The programmatic request is intentionally not
    /// replayed over settings chosen by that UI.
    /// </summary>
    public DriverProcessingResult NeutralizeRequestedSettings(string reason)
    {
        var result = new ResultBuilder();
        foreach (var setting in EnumerateRequestedSettings())
        {
            result.Add(setting.Name, setting.Value, DriverProcessingStatus.Neutralized, null, reason);
        }
        return result.Build();
    }

    private IEnumerable<(string Name, object Value)> EnumerateRequestedSettings()
    {
        if (_options.Brightness is { } brightness)
            yield return (nameof(DriverProcessingOptions.Brightness), brightness);
        if (_options.Contrast is { } contrast)
            yield return (nameof(DriverProcessingOptions.Contrast), contrast);
        if (_options.RotationDegrees is { } rotation)
            yield return (nameof(DriverProcessingOptions.RotationDegrees), rotation);
        if (_options.AutomaticOrientation is { } orientation)
            yield return (nameof(DriverProcessingOptions.AutomaticOrientation), orientation);
        if (_options.Deskew is { } deskew)
            yield return (nameof(DriverProcessingOptions.Deskew), deskew);
        if (_options.AutomaticBrightness is { } automaticBrightness)
            yield return (nameof(DriverProcessingOptions.AutomaticBrightness), automaticBrightness);
        if (_options.AutomaticPageSize is { } pageSize)
            yield return (nameof(DriverProcessingOptions.AutomaticPageSize), pageSize);
        if (_options.AutomaticBorderDetection is { } border)
            yield return (nameof(DriverProcessingOptions.AutomaticBorderDetection), border);
        if (_options.AutomaticCrop is { } crop)
            yield return (nameof(DriverProcessingOptions.AutomaticCrop), crop);
        if (_options.AutomaticColorDetection is { } color)
            yield return (nameof(DriverProcessingOptions.AutomaticColorDetection), color);
        if (_options.AutomaticBlankPageDetection is { } blank)
            yield return (nameof(DriverProcessingOptions.AutomaticBlankPageDetection), blank);
    }

    private PropertyObservation Observe(int propertyId, params string[] names)
    {
        var property = FindProperty(propertyId, names);
        return Observe(property);
    }

    private PropertyObservation Observe(WiaProperty? property)
    {
        if (property == null)
        {
            return _propertyCollectionQueryFailed
                ? PropertyObservation.QueryFailed
                : PropertyObservation.Missing;
        }

        try
        {
            if (property.Type != WiaPropertyType.I4)
            {
                _logger.LogDebug("WIA property {PropertyId} has unsupported type {PropertyType}",
                    property.Id, property.Type);
                return PropertyObservation.QueryFailed;
            }

            var attributes = property.Attributes;
            var canRead = attributes.Flags.HasFlag(WiaPropertyFlags.Read);
            var canWrite = attributes.Flags.HasFlag(WiaPropertyFlags.Write);
            var state = canRead
                ? canWrite ? DriverProcessingCapabilityState.Writable : DriverProcessingCapabilityState.ReadOnly
                : canWrite ? DriverProcessingCapabilityState.QueryFailed : DriverProcessingCapabilityState.Unsupported;

            object? current = null;
            if (canRead)
            {
                current = property.Value;
            }

            return new PropertyObservation(property, attributes, state, current, attributes.Nom);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not query WIA property {PropertyId}", property.Id);
            return PropertyObservation.QueryFailed;
        }
    }

    private DriverProcessingNumericCaps GetNumericCaps(int propertyId, Func<int, double> valueConverter,
        params string[] names)
    {
        var observation = Observe(propertyId, names);
        if (observation.State == DriverProcessingCapabilityState.QueryFailed)
        {
            return new DriverProcessingNumericCaps { State = observation.State };
        }
        if (observation.State == DriverProcessingCapabilityState.Unsupported)
        {
            return new DriverProcessingNumericCaps { State = observation.State };
        }

        try
        {
            var attributes = observation.Attributes!;
            double? minimum = null;
            double? maximum = null;
            double? step = null;
            if (attributes.Flags.HasFlag(WiaPropertyFlags.Range))
            {
                minimum = valueConverter(attributes.Min);
                maximum = valueConverter(attributes.Max);
                step = attributes.Step == 0 ? null : Math.Abs(valueConverter(attributes.Min + attributes.Step) -
                                                               valueConverter(attributes.Min));
            }
            else if (attributes.Flags.HasFlag(WiaPropertyFlags.List))
            {
                var values = GetIntValues(attributes).Select(valueConverter).OrderBy(value => value).ToArray();
                if (values.Length == 0)
                {
                    return new DriverProcessingNumericCaps { State = DriverProcessingCapabilityState.QueryFailed };
                }
                else
                {
                    minimum = values[0];
                    maximum = values[^1];
                    step = values.Length > 1 ? GetMinimumStep(values) : null;
                }
            }

            return new DriverProcessingNumericCaps
            {
                State = observation.State,
                Minimum = minimum,
                Maximum = maximum,
                Step = step,
                Current = ToDouble(observation.Current, valueConverter),
                Default = ToDouble(observation.Default, valueConverter)
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not map WIA numeric property {PropertyId}", propertyId);
            return new DriverProcessingNumericCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private DriverProcessingBooleanCaps GetBooleanCaps(int propertyId, Func<WiaPropertyAttributes, bool> supportsValue,
        params string[] names)
    {
        return GetBooleanCaps(FindProperty(propertyId, names), supportsValue, null);
    }

    private DriverProcessingBooleanCaps GetBooleanCaps(int propertyId, Func<WiaPropertyAttributes, bool> supportsValue,
        Func<int, bool> valueConverter, params string[] names)
    {
        return GetBooleanCaps(FindProperty(propertyId, names), supportsValue, valueConverter);
    }

    private DriverProcessingBooleanCaps GetBooleanCaps(WiaProperty? property,
        Func<WiaPropertyAttributes, bool> supportsValue, Func<int, bool>? valueConverter = null)
    {
        var observation = Observe(property);
        if (observation.State is DriverProcessingCapabilityState.QueryFailed or
            DriverProcessingCapabilityState.Unsupported)
        {
            return new DriverProcessingBooleanCaps { State = observation.State };
        }

        try
        {
            if (!supportsValue(observation.Attributes!))
            {
                return new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.Unsupported };
            }

            return new DriverProcessingBooleanCaps
            {
                State = observation.State,
                Current = ToBoolean(observation.Current, valueConverter),
                Default = ToBoolean(observation.Default, valueConverter)
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not map WIA boolean property {PropertyId}", property?.Id);
            return new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private DriverProcessingColorCaps GetColorCaps()
    {
        var observation = Observe(WiaPropertyId.IPA_DATATYPE);
        if (observation.State is DriverProcessingCapabilityState.QueryFailed or
            DriverProcessingCapabilityState.Unsupported)
        {
            return new DriverProcessingColorCaps { State = observation.State };
        }

        try
        {
            var attributes = observation.Attributes!;
            var values = GetIntValues(attributes).ToArray();
            var hasAutomatic = (attributes.Flags.HasFlag(WiaPropertyFlags.List) ||
                                attributes.Flags.HasFlag(WiaPropertyFlags.Range)) &&
                               SupportsRawValue(attributes, WiaDataAuto) ||
                               TryGetInt(observation.Current, out var currentValue) && currentValue == WiaDataAuto ||
                               TryGetInt(observation.Default, out var defaultValue) && defaultValue == WiaDataAuto;
            if (!hasAutomatic)
            {
                return new DriverProcessingColorCaps { State = DriverProcessingCapabilityState.Unsupported };
            }

            var modes = ImmutableList.CreateBuilder<DriverColorDetectionMode>();
            modes.Add(DriverColorDetectionMode.Automatic);
            if (values.Any(value => value is WiaDataThreshold or WiaDataGrayscale or WiaDataColor) ||
                (!attributes.Flags.HasFlag(WiaPropertyFlags.List) &&
                 (SupportsRawValue(attributes, WiaDataThreshold) ||
                  SupportsRawValue(attributes, WiaDataGrayscale) ||
                  SupportsRawValue(attributes, WiaDataColor))))
            {
                modes.Add(DriverColorDetectionMode.Off);
            }

            return new DriverProcessingColorCaps
            {
                State = observation.State,
                SupportedModes = modes.ToImmutable(),
                Current = ToColorMode(observation.Current),
                Default = ToColorMode(observation.Default)
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not map WIA color detection property");
            return new DriverProcessingColorCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private void ApplyNumeric(ResultBuilder result, string name, double requested, int propertyId,
        Func<int, double> fromRaw, Func<double, int> toRaw, double minimumRequest, double maximumRequest,
        params string[] names)
    {
        var observation = Observe(propertyId, names);
        if (observation.State == DriverProcessingCapabilityState.QueryFailed)
        {
            result.Add(name, requested, DriverProcessingStatus.Failed, null, "The WIA property could not be queried.");
            return;
        }
        if (observation.State == DriverProcessingCapabilityState.Unsupported)
        {
            result.AddUnsupported(name, requested, "The WIA property is not available.");
            return;
        }
        if (observation.State != DriverProcessingCapabilityState.Writable)
        {
            result.Add(name, requested, DriverProcessingStatus.Rejected, null,
                "The WIA property is read-only.");
            return;
        }
        if (requested < minimumRequest || requested > maximumRequest)
        {
            result.Add(name, requested, DriverProcessingStatus.Rejected, null,
                $"The requested value must be between {minimumRequest} and {maximumRequest}.");
            return;
        }

        try
        {
            var rawValue = SelectNumericValue(observation.Attributes!, requested, fromRaw, toRaw,
                minimumRequest, maximumRequest);
            observation.Property!.Value = rawValue;
            if (!TryGetInt(observation.Property.Value, out var effectiveRaw))
            {
                result.Add(name, requested, DriverProcessingStatus.Failed, null,
                    "The WIA property did not return a readable value after it was set.");
                return;
            }

            var effective = fromRaw(effectiveRaw);
            var expected = fromRaw(rawValue);
            if (Math.Abs(effective - expected) > 0.0001)
            {
                result.Add(name, requested, DriverProcessingStatus.Failed, effective,
                    $"The WIA driver read back {effective} after {expected} was requested.");
                return;
            }

            result.Add(name, requested, DriverProcessingStatus.Applied, effective);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not apply WIA property {PropertyId}", propertyId);
            result.Add(name, requested, DriverProcessingStatus.Failed, null, ex.Message);
        }
    }

    private void ApplyBoolean(ResultBuilder result, string name, bool requested, int propertyId,
        Func<WiaPropertyAttributes, bool> supportsValue, int trueValue, int falseValue, bool selectAlternativeTrue = false,
        Func<int, bool>? valueConverter = null,
        params string[] names)
    {
        ApplyBoolean(result, name, requested, FindProperty(propertyId, names), supportsValue, trueValue, falseValue,
            selectAlternativeTrue, valueConverter);
    }

    private void ApplyBoolean(ResultBuilder result, string name, bool requested, WiaProperty? property,
        Func<WiaPropertyAttributes, bool> supportsValue, int trueValue, int falseValue,
        bool selectAlternativeTrue = false, Func<int, bool>? valueConverter = null)
    {
        var observation = Observe(property);
        if (observation.State == DriverProcessingCapabilityState.QueryFailed)
        {
            result.Add(name, requested, DriverProcessingStatus.Failed, null, "The WIA property could not be queried.");
            return;
        }
        if (observation.State == DriverProcessingCapabilityState.Unsupported)
        {
            result.AddUnsupported(name, requested, "The WIA property is not available.");
            return;
        }
        if (observation.State != DriverProcessingCapabilityState.Writable)
        {
            result.Add(name, requested, DriverProcessingStatus.Rejected, null,
                "The WIA property is read-only.");
            return;
        }

        try
        {
            if (!supportsValue(observation.Attributes!))
            {
                result.AddUnsupported(name, requested, "The WIA property does not expose this operation.");
                return;
            }

            var rawValue = SelectBooleanValue(observation.Attributes!, requested, trueValue, falseValue,
                selectAlternativeTrue);
            if (rawValue == null)
            {
                result.AddUnsupported(name, requested, "The WIA property does not accept the requested value.");
                return;
            }

            observation.Property!.Value = rawValue.Value;
            if (!TryGetInt(observation.Property.Value, out var effectiveRaw))
            {
                result.Add(name, requested, DriverProcessingStatus.Failed, null,
                    "The WIA property did not return a readable value after it was set.");
                return;
            }

            var effective = valueConverter?.Invoke(effectiveRaw) ?? effectiveRaw != falseValue;
            if (effective != requested)
            {
                result.Add(name, requested, DriverProcessingStatus.Failed, effective,
                    $"The WIA driver read back {effective} after {requested} was requested.");
                return;
            }

            result.Add(name, requested, DriverProcessingStatus.Applied, effective);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not apply WIA property {PropertyId}", property?.Id);
            result.Add(name, requested, DriverProcessingStatus.Failed, null, ex.Message);
        }
    }

    private void ApplyColor(ResultBuilder result, DriverColorDetectionMode requested)
    {
        const string name = nameof(DriverProcessingOptions.AutomaticColorDetection);
        var observation = Observe(WiaPropertyId.IPA_DATATYPE);
        if (observation.State == DriverProcessingCapabilityState.QueryFailed)
        {
            result.Add(name, requested, DriverProcessingStatus.Failed, null, "The WIA property could not be queried.");
            return;
        }
        if (observation.State == DriverProcessingCapabilityState.Unsupported)
        {
            result.AddUnsupported(name, requested, "The WIA data type property is not available.");
            return;
        }
        if (observation.State != DriverProcessingCapabilityState.Writable)
        {
            result.Add(name, requested, DriverProcessingStatus.Rejected, null,
                "The WIA data type property is read-only.");
            return;
        }

        if (requested is DriverColorDetectionMode.ColorOrGrayscale or DriverColorDetectionMode.ColorOrBlackAndWhite)
        {
            result.Add(name, requested, DriverProcessingStatus.Rejected, null,
                "WIA exposes automatic data type selection but not this color split mode.");
            return;
        }

        try
        {
            var rawValue = requested == DriverColorDetectionMode.Automatic
                ? WiaDataAuto
                : GetDataTypeForBitDepth();
            var attributes = observation.Attributes!;
            var acceptsRawValue = attributes.Flags.HasFlag(WiaPropertyFlags.List) ||
                                  attributes.Flags.HasFlag(WiaPropertyFlags.Range)
                ? SupportsRawValue(attributes, rawValue)
                : (TryGetInt(observation.Current, out var currentValue) && currentValue == rawValue ||
                   TryGetInt(observation.Default, out var defaultValue) && defaultValue == rawValue);
            if (!acceptsRawValue)
            {
                result.AddUnsupported(name, requested, "The WIA property does not accept the requested data type.");
                return;
            }

            observation.Property!.Value = rawValue;
            if (!TryGetInt(observation.Property.Value, out var effectiveRaw))
            {
                result.Add(name, requested, DriverProcessingStatus.Failed, null,
                    "The WIA property did not return a readable value after it was set.");
                return;
            }

            var effective = ToColorMode(effectiveRaw);
            var expected = requested == DriverColorDetectionMode.Automatic
                ? DriverColorDetectionMode.Automatic
                : DriverColorDetectionMode.Off;
            if (effective != expected)
            {
                result.Add(name, requested, DriverProcessingStatus.Failed, effective,
                    $"The WIA driver read back {effective} after {expected} was requested.");
                return;
            }

            result.Add(name, requested, DriverProcessingStatus.Applied, effective);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not apply WIA color detection");
            result.Add(name, requested, DriverProcessingStatus.Failed, null, ex.Message);
        }
    }

    private WiaProperty? FindProperty(int propertyId, params string[] names)
    {
        try
        {
            var property = _item.Properties.GetOrNull(propertyId);
            if (property != null)
            {
                return property;
            }

            if (names.Length > 0)
            {
                foreach (var candidate in _item.Properties)
                {
                    if (names.Any(name => NamesEqual(candidate.Name, name)))
                    {
                        return candidate;
                    }
                }
            }

            return _device.Properties.GetOrNull(propertyId);
        }
        catch (Exception ex)
        {
            _propertyCollectionQueryFailed = true;
            _logger.LogDebug(ex, "Could not enumerate WIA properties while looking for {PropertyId}", propertyId);
            return null;
        }
    }

    private WiaProperty? FindNamedProperty(params string[] names)
    {
        try
        {
            foreach (var property in _item.Properties)
            {
                if (names.Any(name => NamesEqual(property.Name, name)))
                {
                    return property;
                }
            }

            foreach (var property in _device.Properties)
            {
                if (names.Any(name => NamesEqual(property.Name, name)))
                {
                    return property;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _propertyCollectionQueryFailed = true;
            _logger.LogDebug(ex, "Could not enumerate WIA properties while looking for a named property");
            return null;
        }
    }

    private static bool NamesEqual(string actual, string expected)
    {
        static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Normalize(actual) == Normalize(expected);
    }

    private static IEnumerable<int> GetIntValues(WiaPropertyAttributes attributes) =>
        attributes.Values?.OfType<int>() ?? Enumerable.Empty<int>();

    private static bool SupportsAnyBooleanValue(WiaPropertyAttributes attributes)
    {
        if (attributes.Flags.HasFlag(WiaPropertyFlags.List))
        {
            return GetIntValues(attributes).Any(value => value == 0 || value == 1);
        }
        if (attributes.Flags.HasFlag(WiaPropertyFlags.Range))
        {
            return SupportsRawValue(attributes, 0) && SupportsRawValue(attributes, 1);
        }
        return true;
    }

    private static bool SupportsAutoDeskewValue(WiaPropertyAttributes attributes) =>
        SupportsRawValue(attributes, WiaAutoDeskewOn) && SupportsRawValue(attributes, WiaAutoDeskewOff);

    private static bool SupportsAutoPageSizeValue(WiaPropertyAttributes attributes) =>
        SupportsRawValue(attributes, WiaPageAuto) && SupportsRawValue(attributes, WiaPageCustom);

    private static bool SupportsAutoCropValue(WiaPropertyAttributes attributes) =>
        SupportsRawValue(attributes, 1) || SupportsRawValue(attributes, 2);

    private static bool SupportsBlankPageValue(WiaPropertyAttributes attributes) =>
        SupportsRawValue(attributes, 1) || SupportsRawValue(attributes, 2);

    private static bool SupportsRawValue(WiaPropertyAttributes attributes, int value)
    {
        if (attributes.Flags.HasFlag(WiaPropertyFlags.List))
        {
            return GetIntValues(attributes).Contains(value);
        }
        if (attributes.Flags.HasFlag(WiaPropertyFlags.Range))
        {
            return value >= attributes.Min && value <= attributes.Max &&
                   (attributes.Step == 0 || (value - attributes.Min) % attributes.Step == 0);
        }
        return true;
    }

    private static int? SelectBooleanValue(WiaPropertyAttributes attributes, bool requested, int trueValue,
        int falseValue, bool selectAlternativeTrue)
    {
        if (!attributes.Flags.HasFlag(WiaPropertyFlags.List))
        {
            return requested ? trueValue : falseValue;
        }

        var values = GetIntValues(attributes).ToArray();
        if (!requested)
        {
            return values.Contains(falseValue) ? falseValue : null;
        }
        if (values.Contains(trueValue))
        {
            return trueValue;
        }
        if (selectAlternativeTrue)
        {
            foreach (var value in values)
            {
                if (value is 1 or 2)
                {
                    return value;
                }
            }
        }
        return null;
    }

    private static int SelectNumericValue(WiaPropertyAttributes attributes, double requested,
        Func<int, double> fromRaw, Func<double, int> toRaw, double minimumRequest, double maximumRequest)
    {
        if (attributes.Flags.HasFlag(WiaPropertyFlags.List))
        {
            return GetIntValues(attributes)
                .OrderBy(value => Math.Abs(fromRaw(value) - requested))
                .First();
        }

        var raw = toRaw(requested);
        if (attributes.Flags.HasFlag(WiaPropertyFlags.Range))
        {
            var requestRange = maximumRequest - minimumRequest;
            if (requestRange > 0)
            {
                var normalized = (requested - minimumRequest) / requestRange;
                raw = (int) Math.Round(attributes.Min + normalized * (attributes.Max - attributes.Min));
            }
            raw = Math.Min(Math.Max(raw, attributes.Min), attributes.Max);
            if (attributes.Step != 0)
            {
                raw -= (raw - attributes.Min) % attributes.Step;
            }
        }
        return raw;
    }

    private int GetDataTypeForBitDepth() => _bitDepth switch
    {
        BitDepth.Grayscale => WiaDataGrayscale,
        BitDepth.BlackAndWhite => WiaDataThreshold,
        _ => WiaDataColor
    };

    private static double RotationValueIdentity(int value) => value;

    private static int ToRotationRaw(double value)
    {
        var normalized = ((value % 360) + 360) % 360;
        var degrees = new[] { 0d, 90d, 180d, 270d };
        var index = Array.IndexOf(degrees, degrees.OrderBy(degree => Math.Abs(degree - normalized)).First());
        return index < 0 ? 0 : index;
    }

    private static double ToRotationDegrees(int value) => value switch
    {
        0 => 0,
        1 => 90,
        2 => 180,
        3 => 270,
        _ => value
    };

    private static double? ToDouble(object? value, Func<int, double> converter)
    {
        return TryGetInt(value, out var intValue) ? converter(intValue) : null;
    }

    private static bool? ToBoolean(object? value, Func<int, bool>? valueConverter = null) =>
        TryGetInt(value, out var intValue) ? valueConverter?.Invoke(intValue) ?? intValue != 0 : null;

    private static DriverColorDetectionMode? ToColorMode(object? value)
    {
        return TryGetInt(value, out var intValue) ? ToColorMode(intValue) : null;
    }

    private static DriverColorDetectionMode ToColorMode(int value) =>
        value == WiaDataAuto ? DriverColorDetectionMode.Automatic : DriverColorDetectionMode.Off;

    private static bool TryGetInt(object? value, out int result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                result = (int) longValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static double GetMinimumStep(double[] values)
    {
        var step = double.MaxValue;
        for (var i = 1; i < values.Length; i++)
        {
            step = Math.Min(step, values[i] - values[i - 1]);
        }
        return step == double.MaxValue ? 0 : step;
    }

    private sealed class PropertyObservation
    {
        public static PropertyObservation Missing { get; } = new(null, null,
            DriverProcessingCapabilityState.Unsupported, null, null);

        public static PropertyObservation QueryFailed { get; } = new(null, null,
            DriverProcessingCapabilityState.QueryFailed, null, null);

        public PropertyObservation(WiaProperty? property, WiaPropertyAttributes? attributes,
            DriverProcessingCapabilityState state, object? current, object? @default)
        {
            Property = property;
            Attributes = attributes;
            State = state;
            Current = current;
            Default = @default;
        }

        public WiaProperty? Property { get; }
        public WiaPropertyAttributes? Attributes { get; }
        public DriverProcessingCapabilityState State { get; }
        public object? Current { get; }
        public object? Default { get; }
    }

    private sealed class ResultBuilder
    {
        private readonly Dictionary<string, object?> _requested = new();
        private readonly Dictionary<string, object?> _effective = new();
        private readonly List<DriverProcessingSetting> _settings = new();
        private readonly List<string> _rejected = new();
        private readonly List<string> _unsupported = new();
        private readonly List<string> _neutralized = new();
        private readonly List<string> _failed = new();

        public void Add(string name, object? requested, DriverProcessingStatus status, object? effective,
            string? message = null)
        {
            _requested[name] = requested;
            if (effective != null)
            {
                _effective[name] = effective;
            }
            _settings.Add(new DriverProcessingSetting
            {
                Name = name,
                Status = status,
                RequestedValue = requested,
                EffectiveValue = effective,
                Message = message
            });
            switch (status)
            {
                case DriverProcessingStatus.Rejected:
                    _rejected.Add(name);
                    break;
                case DriverProcessingStatus.Unsupported:
                    _unsupported.Add(name);
                    break;
                case DriverProcessingStatus.Neutralized:
                    _neutralized.Add(name);
                    break;
                case DriverProcessingStatus.Failed:
                    _failed.Add(name);
                    break;
            }
        }

        public void AddUnsupported(string name, object? requested, string message)
        {
            Add(name, requested, DriverProcessingStatus.Unsupported, null, message);
        }

        public DriverProcessingResult Build() => new()
        {
            RequestedSettings = _requested,
            EffectiveSettings = _effective,
            Settings = _settings,
            RejectedSettings = _rejected,
            UnsupportedSettings = _unsupported,
            NeutralizedSettings = _neutralized,
            FailedSettings = _failed,
            FailureReason = _failed.Count == 0 ? null : "One or more WIA processing settings could not be applied."
        };
    }
}
#endif
