#if !MACOS
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NTwain;
using NTwain.Data;

namespace NAPS2.Scan.Internal.Twain;

/// <summary>
/// Bridges the common driver processing contract to the TWAIN image capabilities.
/// </summary>
/// <remarks>
/// TWAIN capabilities are queried after the source has been opened. A source may expose a capability in its
/// container but still reject a query or a set for the currently selected camera, pixel type, or paper source, so all
/// reads and writes are isolated per setting. This keeps a single bad vendor capability from making the other values
/// unavailable.
/// </remarks>
internal static class TwainDriverProcessing
{
    internal const string Brightness = nameof(DriverProcessingOptions.Brightness);
    internal const string Contrast = nameof(DriverProcessingOptions.Contrast);
    internal const string RotationDegrees = nameof(DriverProcessingOptions.RotationDegrees);
    internal const string AutomaticOrientation = nameof(DriverProcessingOptions.AutomaticOrientation);
    internal const string Deskew = nameof(DriverProcessingOptions.Deskew);
    internal const string AutomaticBrightness = nameof(DriverProcessingOptions.AutomaticBrightness);
    internal const string AutomaticPageSize = nameof(DriverProcessingOptions.AutomaticPageSize);
    internal const string AutomaticBorderDetection = nameof(DriverProcessingOptions.AutomaticBorderDetection);
    internal const string AutomaticCrop = nameof(DriverProcessingOptions.AutomaticCrop);
    internal const string AutomaticColorDetection = nameof(DriverProcessingOptions.AutomaticColorDetection);
    internal const string AutomaticBlankPageDetection = nameof(DriverProcessingOptions.AutomaticBlankPageDetection);

    public static DriverProcessingCaps QueryCaps(DataSource source, ILogger? logger = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        return new DriverProcessingCaps
        {
            Brightness = QueryNumeric(source.Capabilities.ICapBrightness, logger, Brightness),
            Contrast = QueryNumeric(source.Capabilities.ICapContrast, logger, Contrast),
            RotationDegrees = QueryRotation(source, logger),
            AutomaticOrientation = QueryAutomaticOrientation(source, logger),
            Deskew = QueryBoolean(source.Capabilities.ICapAutomaticDeskew, logger, Deskew),
            AutomaticBrightness = QueryBoolean(source.Capabilities.ICapAutoBright, logger, AutomaticBrightness),
            AutomaticPageSize = QueryPageSize(source, logger),
            AutomaticBorderDetection = QueryBoolean(source.Capabilities.ICapAutomaticBorderDetection, logger,
                AutomaticBorderDetection),
            AutomaticCrop = QueryBoolean(source.Capabilities.ICapAutomaticCropUsesFrame, logger, AutomaticCrop),
            AutomaticColorDetection = QueryColor(source, logger),
            AutomaticBlankPageDetection = QueryBlankPage(source, logger)
        };
    }

    public static DriverProcessingResult Apply(DataSource source, DriverProcessingOptions? options,
        ILogger? logger = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (options == null || !options.HasRequests)
        {
            return new DriverProcessingResult();
        }

        var requested = new Dictionary<string, object?>(StringComparer.Ordinal);
        var effective = new Dictionary<string, object?>(StringComparer.Ordinal);
        var settings = new List<DriverProcessingSetting>();
        var rejected = new List<string>();
        var unsupported = new List<string>();
        var neutralized = new List<string>();
        var failed = new List<string>();

        ApplyNumeric(source.Capabilities.ICapBrightness, Brightness, options.Brightness, requested, effective, settings,
            rejected, unsupported, failed, logger);
        ApplyNumeric(source.Capabilities.ICapContrast, Contrast, options.Contrast, requested, effective, settings,
            rejected, unsupported, failed, logger);
        ApplyRotation(source, options.RotationDegrees, requested, effective, settings, rejected, unsupported, failed,
            logger);
        ApplyAutomaticOrientation(source, options.AutomaticOrientation, requested, effective, settings, rejected,
            unsupported, failed, logger);
        ApplyBoolean(source.Capabilities.ICapAutomaticDeskew, Deskew, options.Deskew, requested, effective, settings,
            rejected, unsupported, failed, logger);
        ApplyBoolean(source.Capabilities.ICapAutoBright, AutomaticBrightness, options.AutomaticBrightness, requested,
            effective, settings, rejected, unsupported, failed, logger);
        ApplyPageSize(source, options.AutomaticPageSize, requested, effective, settings, rejected, unsupported, failed,
            logger);
        ApplyBoolean(source.Capabilities.ICapAutomaticBorderDetection, AutomaticBorderDetection,
            options.AutomaticBorderDetection, requested, effective, settings, rejected, unsupported, failed, logger);
        ApplyReadOnlyBoolean(source.Capabilities.ICapAutomaticCropUsesFrame, AutomaticCrop, options.AutomaticCrop,
            requested, effective, settings, rejected, unsupported, failed, logger);
        ApplyColor(source, options.AutomaticColorDetection, requested, effective, settings, rejected, unsupported,
            failed, logger);
        ApplyBlankPage(source, options.AutomaticBlankPageDetection, requested, effective, settings, rejected,
            unsupported, failed, logger);

        return new DriverProcessingResult
        {
            RequestedSettings = requested,
            EffectiveSettings = effective,
            Settings = settings,
            RejectedSettings = rejected,
            UnsupportedSettings = unsupported,
            NeutralizedSettings = neutralized,
            FailedSettings = failed
        };
    }

    /// <summary>
    /// Builds the result for a request that was deliberately left to the native source UI.
    /// </summary>
    public static DriverProcessingResult NativeUiResult(DriverProcessingOptions? options)
    {
        if (options == null || !options.HasRequests)
        {
            return new DriverProcessingResult();
        }

        var requested = GetRequestedValues(options);
        var settings = requested.Select(x => new DriverProcessingSetting
        {
            Name = x.Key,
            Status = DriverProcessingStatus.Neutralized,
            RequestedValue = x.Value,
            Message = "The TWAIN native UI owns this setting."
        }).ToArray();
        return new DriverProcessingResult
        {
            RequestedSettings = requested,
            Settings = settings,
            NeutralizedSettings = requested.Keys.ToArray()
        };
    }

    private static IReadOnlyDictionary<string, object?> GetRequestedValues(DriverProcessingOptions options)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddRequested(values, Brightness, options.Brightness);
        AddRequested(values, Contrast, options.Contrast);
        AddRequested(values, RotationDegrees, options.RotationDegrees);
        AddRequested(values, AutomaticOrientation, options.AutomaticOrientation);
        AddRequested(values, Deskew, options.Deskew);
        AddRequested(values, AutomaticBrightness, options.AutomaticBrightness);
        AddRequested(values, AutomaticPageSize, options.AutomaticPageSize);
        AddRequested(values, AutomaticBorderDetection, options.AutomaticBorderDetection);
        AddRequested(values, AutomaticCrop, options.AutomaticCrop);
        AddRequested(values, AutomaticColorDetection, options.AutomaticColorDetection);
        AddRequested(values, AutomaticBlankPageDetection, options.AutomaticBlankPageDetection);
        return values;
    }

    private static void AddRequested<T>(IDictionary<string, object?> values, string name, T? value)
        where T : struct
    {
        if (value.HasValue)
        {
            values[name] = value.Value;
        }
    }

    private static DriverProcessingNumericCaps QueryNumeric(ICapWrapper<TWFix32> capability, ILogger? logger,
        string name)
    {
        try
        {
            var state = GetState(capability);
            if (state == DriverProcessingCapabilityState.Unsupported)
            {
                return new DriverProcessingNumericCaps { State = state };
            }

            var values = capability.CanGet
                ? capability.GetValues().Select(ToDouble).Distinct().OrderBy(x => x).ToArray()
                : Array.Empty<double>();
        var current = capability.CanGet ? (double?)ToDouble(capability.GetCurrent()) : null;
        var @default = capability.CanGetDefault ? (double?)ToDouble(capability.GetDefault()) : null;
            return new DriverProcessingNumericCaps
            {
                State = state,
                Minimum = values.Length == 0 ? null : values[0],
                Maximum = values.Length == 0 ? null : values[^1],
                Step = GetStep(values),
                Current = current,
                Default = @default
            };
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TWAIN capability query failed for {Capability}", name);
            return new DriverProcessingNumericCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private static DriverProcessingNumericCaps QueryRotation(DataSource source, ILogger? logger)
    {
        // CAP_ROTATION is the precise TWAIN representation and should be preferred. A number of sources only expose
        // CAP_ORIENTATION, however, so retain the quarter-turn values that capability can represent instead of
        // reporting rotation as unavailable.
        var rotation = QueryNumeric(source.Capabilities.ICapRotation, logger, RotationDegrees);
        var orientation = QueryOrientation(source.Capabilities.ICapOrientation, logger);
        if (rotation.State == DriverProcessingCapabilityState.Writable)
        {
            return rotation;
        }
        if (orientation.State == DriverProcessingCapabilityState.Writable)
        {
            return orientation;
        }
        if (rotation.State == DriverProcessingCapabilityState.ReadOnly)
        {
            return rotation;
        }
        if (rotation.State == DriverProcessingCapabilityState.Unsupported)
        {
            return orientation;
        }
        if (orientation.State != DriverProcessingCapabilityState.Unsupported)
        {
            return orientation;
        }
        return rotation;
    }

    private static DriverProcessingNumericCaps QueryOrientation(IReadOnlyCapWrapper<OrientationType> capability,
        ILogger? logger)
    {
        try
        {
            var state = GetState(capability);
            if (state == DriverProcessingCapabilityState.Unsupported)
            {
                return new DriverProcessingNumericCaps { State = state };
            }

            var values = capability.CanGet
                ? capability.GetValues()
                    .Select(ToRotationDegrees)
                    .Where(x => x.HasValue)
                    .Select(x => x!.Value)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToArray()
                : Array.Empty<double>();
            // An orientation capability that only offers automatic modes is useful to the automatic-orientation
            // query, but it does not represent a fixed rotation value.
            if (values.Length == 0)
            {
                return new DriverProcessingNumericCaps { State = DriverProcessingCapabilityState.Unsupported };
            }

            var current = capability.CanGet ? ToRotationDegrees(capability.GetCurrent()) : null;
            var @default = capability.CanGetDefault ? ToRotationDegrees(capability.GetDefault()) : null;
            return new DriverProcessingNumericCaps
            {
                State = state,
                Minimum = values[0],
                Maximum = values[^1],
                Step = GetStep(values),
                Current = current,
                Default = @default
            };
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TWAIN capability query failed for {Capability}", RotationDegrees);
            return new DriverProcessingNumericCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private static DriverProcessingBooleanCaps QueryAutomaticOrientation(DataSource source, ILogger? logger)
    {
        var automaticRotate = QueryBoolean(source.Capabilities.ICapAutomaticRotate, logger, AutomaticOrientation);
        var orientation = QueryAutomaticOrientationFromOrientation(source.Capabilities.ICapOrientation, logger);
        if (automaticRotate.State == DriverProcessingCapabilityState.Writable)
        {
            return automaticRotate;
        }
        if (orientation.State == DriverProcessingCapabilityState.Writable)
        {
            return orientation;
        }
        if (automaticRotate.State == DriverProcessingCapabilityState.ReadOnly)
        {
            return automaticRotate;
        }
        if (automaticRotate.State == DriverProcessingCapabilityState.Unsupported)
        {
            return orientation;
        }
        if (orientation.State != DriverProcessingCapabilityState.Unsupported)
        {
            return orientation;
        }
        return automaticRotate;
    }

    private static DriverProcessingBooleanCaps QueryAutomaticOrientationFromOrientation(
        IReadOnlyCapWrapper<OrientationType> capability, ILogger? logger)
    {
        try
        {
            var state = GetState(capability);
            if (state == DriverProcessingCapabilityState.Unsupported)
            {
                return new DriverProcessingBooleanCaps { State = state };
            }

            var values = capability.CanGet ? capability.GetValues().ToArray() : Array.Empty<OrientationType>();
        var current = capability.CanGet ? (bool?)IsAutomaticOrientation(capability.GetCurrent()) : null;
        var @default = capability.CanGetDefault ? (bool?)IsAutomaticOrientation(capability.GetDefault()) : null;
            var supportsAutomatic = values.Any(IsAutomaticOrientation) || current == true || @default == true;
            if (!supportsAutomatic)
            {
                return new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.Unsupported };
            }

            return new DriverProcessingBooleanCaps
            {
                State = state,
                Current = current,
                Default = @default
            };
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TWAIN capability query failed for {Capability}", AutomaticOrientation);
            return new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private static DriverProcessingBooleanCaps QueryBoolean(ICapWrapper<BoolType> capability, ILogger? logger,
        string name)
    {
        try
        {
            var state = GetState(capability);
            if (state == DriverProcessingCapabilityState.Unsupported)
            {
                return new DriverProcessingBooleanCaps { State = state };
            }
            return new DriverProcessingBooleanCaps
            {
                State = state,
                Current = capability.CanGet ? ToBool(capability.GetCurrent()) : null,
                Default = capability.CanGetDefault ? ToBool(capability.GetDefault()) : null
            };
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TWAIN capability query failed for {Capability}", name);
            return new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private static DriverProcessingBooleanCaps QueryBoolean(IReadOnlyCapWrapper<BoolType> capability, ILogger? logger,
        string name)
    {
        try
        {
            var state = GetState(capability);
            if (state == DriverProcessingCapabilityState.Unsupported)
            {
                return new DriverProcessingBooleanCaps { State = state };
            }
            return new DriverProcessingBooleanCaps
            {
                State = state,
                Current = capability.CanGet ? ToBool(capability.GetCurrent()) : null,
                Default = capability.CanGetDefault ? ToBool(capability.GetDefault()) : null
            };
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TWAIN capability query failed for {Capability}", name);
            return new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private static DriverProcessingBooleanCaps QueryPageSize(DataSource source, ILogger? logger)
    {
        try
        {
            var autoSize = source.Capabilities.ICapAutoSize;
            var lengthDetection = source.Capabilities.ICapAutomaticLengthDetection;
            if (autoSize.IsSupported)
            {
                return new DriverProcessingBooleanCaps
                {
                    State = GetState(autoSize),
                    Current = autoSize.CanGet ? autoSize.GetCurrent() == AutoSize.Auto : null,
                    Default = autoSize.CanGetDefault ? autoSize.GetDefault() == AutoSize.Auto : null
                };
            }
            return QueryBoolean(lengthDetection, logger, AutomaticPageSize);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TWAIN capability query failed for {Capability}", AutomaticPageSize);
            return new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private static DriverProcessingColorCaps QueryColor(DataSource source, ILogger? logger)
    {
        try
        {
            var enabled = source.Capabilities.ICapAutomaticColorEnabled;
            var nonColorPixelType = source.Capabilities.ICapAutomaticColorNonColorPixelType;
            var enabledState = GetState(enabled);
            var nonColorState = GetState(nonColorPixelType);
            if (enabledState == DriverProcessingCapabilityState.Unsupported &&
                nonColorState == DriverProcessingCapabilityState.Unsupported)
            {
                return new DriverProcessingColorCaps { State = DriverProcessingCapabilityState.Unsupported };
            }

            var modes = ImmutableList.CreateBuilder<DriverColorDetectionMode>();
            modes.Add(DriverColorDetectionMode.Off);
            if (enabledState is DriverProcessingCapabilityState.ReadOnly or DriverProcessingCapabilityState.Writable)
            {
                modes.Add(DriverColorDetectionMode.Automatic);
            }
            if (nonColorState is DriverProcessingCapabilityState.ReadOnly or DriverProcessingCapabilityState.Writable)
            {
                var values = nonColorPixelType.CanGet
                    ? nonColorPixelType.GetValues().ToArray()
                    : Array.Empty<PixelType>();
                if (values.Contains(PixelType.Gray)) modes.Add(DriverColorDetectionMode.ColorOrGrayscale);
                if (values.Contains(PixelType.BlackWhite)) modes.Add(DriverColorDetectionMode.ColorOrBlackAndWhite);
            }

            var state = enabledState == DriverProcessingCapabilityState.Writable ||
                        nonColorState == DriverProcessingCapabilityState.Writable
                ? DriverProcessingCapabilityState.Writable
                : enabledState != DriverProcessingCapabilityState.Unsupported
                    ? enabledState
                    : nonColorState;
            var currentEnabled = enabled.CanGet ? ToBool(enabled.GetCurrent()) : false;
            var current = currentEnabled
                ? ToColorMode(nonColorPixelType.CanGet ? nonColorPixelType.GetCurrent() : null)
                : DriverColorDetectionMode.Off;
            var defaultEnabled = enabled.CanGetDefault ? ToBool(enabled.GetDefault()) : false;
            var @default = defaultEnabled
                ? ToColorMode(nonColorPixelType.CanGetDefault ? nonColorPixelType.GetDefault() : null)
                : DriverColorDetectionMode.Off;
            return new DriverProcessingColorCaps
            {
                State = state,
                SupportedModes = modes.Distinct().ToImmutableList(),
                Current = current,
                Default = @default
            };
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TWAIN capability query failed for {Capability}", AutomaticColorDetection);
            return new DriverProcessingColorCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private static DriverProcessingBooleanCaps QueryBlankPage(DataSource source, ILogger? logger)
    {
        try
        {
            var capability = source.Capabilities.ICapAutoDiscardBlankPages;
            var state = GetState(capability);
            if (state == DriverProcessingCapabilityState.Unsupported)
            {
                return new DriverProcessingBooleanCaps { State = state };
            }
            return new DriverProcessingBooleanCaps
            {
                State = state,
                Current = capability.CanGet ? capability.GetCurrent() == BlankPage.Auto : null,
                Default = capability.CanGetDefault ? capability.GetDefault() == BlankPage.Auto : null
            };
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "TWAIN capability query failed for {Capability}", AutomaticBlankPageDetection);
            return new DriverProcessingBooleanCaps { State = DriverProcessingCapabilityState.QueryFailed };
        }
    }

    private static void ApplyNumeric(ICapWrapper<TWFix32> capability, string name, int? requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        ApplyNumeric(capability, name, requestedValue.HasValue ? requestedValue.Value : (double?) null, requested,
            effective, settings, rejected, unsupported, failed, logger);
    }

    private static void ApplyNumeric(ICapWrapper<TWFix32> capability, string name, double? requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        if (!requestedValue.HasValue) return;
        requested[name] = requestedValue.Value;
        try
        {
            if (!capability.IsSupported)
            {
                unsupported.Add(name);
                settings.Add(new DriverProcessingSetting
                {
                    Name = name,
                    Status = DriverProcessingStatus.Unsupported,
                    RequestedValue = requestedValue.Value
                });
                return;
            }
            if (!capability.CanSet || capability.IsReadOnly)
            {
                rejected.Add(name);
                settings.Add(new DriverProcessingSetting
                {
                    Name = name,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue.Value,
                    EffectiveValue = capability.CanGet ? ToDouble(capability.GetCurrent()) : null,
                    Message = "The TWAIN capability is read-only."
                });
                return;
            }
            var setResult = capability.SetValue(ToFix32(requestedValue.Value));
            if (!IsSetAccepted(setResult))
            {
                failed.Add(name);
                settings.Add(new DriverProcessingSetting
                {
                    Name = name,
                    Status = DriverProcessingStatus.Failed,
                    RequestedValue = requestedValue.Value,
                    Message = $"The TWAIN source rejected the requested value: {setResult}."
                });
                return;
            }
            var actual = capability.CanGet ? ToDouble(capability.GetCurrent()) : requestedValue.Value;
            effective[name] = actual;
            var applied = NearlyEqual(actual, requestedValue.Value);
            if (!applied) rejected.Add(name);
            settings.Add(new DriverProcessingSetting
            {
                Name = name,
                Status = applied ? DriverProcessingStatus.Applied : DriverProcessingStatus.Rejected,
                RequestedValue = requestedValue.Value,
                EffectiveValue = actual,
                Message = applied ? null : "The TWAIN source applied a different value."
            });
        }
        catch (Exception ex)
        {
            failed.Add(name);
            logger?.LogDebug(ex, "TWAIN capability set failed for {Capability}", name);
            settings.Add(new DriverProcessingSetting
            {
                Name = name,
                Status = DriverProcessingStatus.Failed,
                RequestedValue = requestedValue.Value,
                Message = ex.Message
            });
        }
    }

    private static void ApplyRotation(DataSource source, double? requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        if (!requestedValue.HasValue) return;

        var rotationState = GetCapabilityState(source.Capabilities.ICapRotation);
        var orientationState = GetCapabilityState(source.Capabilities.ICapOrientation);
        if (rotationState == DriverProcessingCapabilityState.Writable ||
            (rotationState == DriverProcessingCapabilityState.ReadOnly &&
             orientationState != DriverProcessingCapabilityState.Writable))
        {
            ApplyNumeric(source.Capabilities.ICapRotation, RotationDegrees, requestedValue, requested, effective,
                settings, rejected, unsupported, failed, logger);
            return;
        }
        if (orientationState == DriverProcessingCapabilityState.Writable ||
            (rotationState == DriverProcessingCapabilityState.Unsupported &&
             orientationState != DriverProcessingCapabilityState.Unsupported))
        {
            ApplyOrientation(source.Capabilities.ICapOrientation, requestedValue.Value, requested, effective, settings,
                rejected, unsupported, failed, logger);
            return;
        }

        // Preserve the diagnostic state from the preferred CAP_ROTATION path when both capability queries failed or
        // neither capability is supported.
        ApplyNumeric(source.Capabilities.ICapRotation, RotationDegrees, requestedValue, requested, effective, settings,
            rejected, unsupported, failed, logger);
    }

    private static void ApplyOrientation(IReadOnlyCapWrapper<OrientationType> capability, double requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        requested[RotationDegrees] = requestedValue;
        try
        {
            if (!capability.IsSupported)
            {
                unsupported.Add(RotationDegrees);
                settings.Add(new DriverProcessingSetting
                {
                    Name = RotationDegrees,
                    Status = DriverProcessingStatus.Unsupported,
                    RequestedValue = requestedValue
                });
                return;
            }
            if (!capability.CanSet || capability.IsReadOnly)
            {
                rejected.Add(RotationDegrees);
                settings.Add(new DriverProcessingSetting
                {
                    Name = RotationDegrees,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue,
                    EffectiveValue = capability.CanGet ? ToRotationDegrees(capability.GetCurrent()) : null,
                    Message = "The TWAIN orientation capability is read-only."
                });
                return;
            }

            var orientation = ToOrientation(requestedValue);
            if (!orientation.HasValue)
            {
                rejected.Add(RotationDegrees);
                settings.Add(new DriverProcessingSetting
                {
                    Name = RotationDegrees,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue,
                    Message = "The TWAIN orientation capability only supports quarter-turn values."
                });
                return;
            }

            var values = capability.CanGet ? capability.GetValues().ToArray() : Array.Empty<OrientationType>();
            if (values.Length > 0 && !values.Contains(orientation.Value))
            {
                rejected.Add(RotationDegrees);
                settings.Add(new DriverProcessingSetting
                {
                    Name = RotationDegrees,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue,
                    Message = "The TWAIN source does not accept the requested orientation."
                });
                return;
            }

            // IReadOnlyCapWrapper is intentionally read-only in the general contract. The TWAIN orientation property
            // is normally an ICapWrapper; this guard keeps the fallback safe if a future NTwain package changes it.
            if (capability is not ICapWrapper<OrientationType> writableCapability)
            {
                rejected.Add(RotationDegrees);
                settings.Add(new DriverProcessingSetting
                {
                    Name = RotationDegrees,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue,
                    Message = "The TWAIN orientation capability cannot be written by this NTwain adapter."
                });
                return;
            }

            var setResult = writableCapability.SetValue(orientation.Value);
            if (!IsSetAccepted(setResult))
            {
                failed.Add(RotationDegrees);
                settings.Add(new DriverProcessingSetting
                {
                    Name = RotationDegrees,
                    Status = DriverProcessingStatus.Failed,
                    RequestedValue = requestedValue,
                    Message = $"The TWAIN source rejected the requested orientation: {setResult}."
                });
                return;
            }
            var actualOrientation = capability.CanGet ? capability.GetCurrent() : orientation.Value;
            var actual = ToRotationDegrees(actualOrientation);
            if (!actual.HasValue)
            {
                failed.Add(RotationDegrees);
                settings.Add(new DriverProcessingSetting
                {
                    Name = RotationDegrees,
                    Status = DriverProcessingStatus.Failed,
                    RequestedValue = requestedValue,
                    Message = "The TWAIN source did not return a fixed orientation after it was set."
                });
                return;
            }

            effective[RotationDegrees] = actual.Value;
            var applied = NearlyEqual(actual.Value, requestedValue);
            if (!applied) rejected.Add(RotationDegrees);
            settings.Add(new DriverProcessingSetting
            {
                Name = RotationDegrees,
                Status = applied ? DriverProcessingStatus.Applied : DriverProcessingStatus.Rejected,
                RequestedValue = requestedValue,
                EffectiveValue = actual.Value,
                Message = applied ? null : "The TWAIN source applied a different orientation."
            });
        }
        catch (Exception ex)
        {
            failed.Add(RotationDegrees);
            logger?.LogDebug(ex, "TWAIN capability set failed for {Capability}", RotationDegrees);
            settings.Add(new DriverProcessingSetting
            {
                Name = RotationDegrees,
                Status = DriverProcessingStatus.Failed,
                RequestedValue = requestedValue,
                Message = ex.Message
            });
        }
    }

    private static void ApplyAutomaticOrientation(DataSource source, bool? requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        if (!requestedValue.HasValue) return;

        var automaticRotateState = GetCapabilityState(source.Capabilities.ICapAutomaticRotate);
        var orientationState = GetCapabilityState(source.Capabilities.ICapOrientation);
        if (automaticRotateState == DriverProcessingCapabilityState.Writable ||
            (automaticRotateState == DriverProcessingCapabilityState.ReadOnly &&
             orientationState != DriverProcessingCapabilityState.Writable))
        {
            ApplyBoolean(source.Capabilities.ICapAutomaticRotate, AutomaticOrientation, requestedValue, requested,
                effective, settings, rejected, unsupported, failed, logger);
            return;
        }
        if (orientationState == DriverProcessingCapabilityState.Writable ||
            (automaticRotateState == DriverProcessingCapabilityState.Unsupported &&
             orientationState != DriverProcessingCapabilityState.Unsupported))
        {
            ApplyOrientationAutomatic(source.Capabilities.ICapOrientation, requestedValue.Value, requested, effective,
                settings, rejected, unsupported, failed, logger);
            return;
        }

        ApplyBoolean(source.Capabilities.ICapAutomaticRotate, AutomaticOrientation, requestedValue, requested,
            effective, settings, rejected, unsupported, failed, logger);
    }

    private static void ApplyOrientationAutomatic(IReadOnlyCapWrapper<OrientationType> capability, bool requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        requested[AutomaticOrientation] = requestedValue;
        try
        {
            if (!capability.IsSupported)
            {
                unsupported.Add(AutomaticOrientation);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticOrientation,
                    Status = DriverProcessingStatus.Unsupported,
                    RequestedValue = requestedValue
                });
                return;
            }
            if (!capability.CanSet || capability.IsReadOnly)
            {
                rejected.Add(AutomaticOrientation);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticOrientation,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue,
                    EffectiveValue = capability.CanGet ? IsAutomaticOrientation(capability.GetCurrent()) : null,
                    Message = "The TWAIN orientation capability is read-only."
                });
                return;
            }
            if (capability is not ICapWrapper<OrientationType> writableCapability)
            {
                rejected.Add(AutomaticOrientation);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticOrientation,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue,
                    Message = "The TWAIN orientation capability cannot be written by this NTwain adapter."
                });
                return;
            }

            var values = capability.CanGet ? capability.GetValues().ToArray() : Array.Empty<OrientationType>();
            OrientationType? target;
            if (requestedValue)
            {
                target = values.Where(IsAutomaticOrientation).Select(x => (OrientationType?) x).FirstOrDefault();
                if (!target.HasValue || !IsAutomaticOrientation(target.Value))
                {
                    rejected.Add(AutomaticOrientation);
                    settings.Add(new DriverProcessingSetting
                    {
                        Name = AutomaticOrientation,
                        Status = DriverProcessingStatus.Rejected,
                        RequestedValue = requestedValue,
                        Message = "The TWAIN source does not expose an automatic orientation mode."
                    });
                    return;
                }
            }
            else
            {
                target = values.Where(x => ToRotationDegrees(x) == 0).Select(x => (OrientationType?) x)
                    .FirstOrDefault();
                if (!target.HasValue && values.Length == 0)
                {
                    target = OrientationType.Rot0;
                }
                if (!target.HasValue)
                {
                    rejected.Add(AutomaticOrientation);
                    settings.Add(new DriverProcessingSetting
                    {
                        Name = AutomaticOrientation,
                        Status = DriverProcessingStatus.Rejected,
                        RequestedValue = requestedValue,
                        Message = "The TWAIN source does not expose a fixed orientation to disable automatic rotation."
                    });
                    return;
                }
            }

            var setResult = writableCapability.SetValue(target.Value);
            if (!IsSetAccepted(setResult))
            {
                failed.Add(AutomaticOrientation);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticOrientation,
                    Status = DriverProcessingStatus.Failed,
                    RequestedValue = requestedValue,
                    Message = $"The TWAIN source rejected the requested orientation mode: {setResult}."
                });
                return;
            }
            var actual = capability.CanGet ? IsAutomaticOrientation(capability.GetCurrent()) : requestedValue;
            effective[AutomaticOrientation] = actual;
            var applied = actual == requestedValue;
            if (!applied) rejected.Add(AutomaticOrientation);
            settings.Add(new DriverProcessingSetting
            {
                Name = AutomaticOrientation,
                Status = applied ? DriverProcessingStatus.Applied : DriverProcessingStatus.Rejected,
                RequestedValue = requestedValue,
                EffectiveValue = actual,
                Message = applied ? null : "The TWAIN source applied a different orientation mode."
            });
        }
        catch (Exception ex)
        {
            failed.Add(AutomaticOrientation);
            logger?.LogDebug(ex, "TWAIN capability set failed for {Capability}", AutomaticOrientation);
            settings.Add(new DriverProcessingSetting
            {
                Name = AutomaticOrientation,
                Status = DriverProcessingStatus.Failed,
                RequestedValue = requestedValue,
                Message = ex.Message
            });
        }
    }

    private static void ApplyBoolean(ICapWrapper<BoolType> capability, string name, bool? requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        if (!requestedValue.HasValue) return;
        requested[name] = requestedValue.Value;
        try
        {
            if (!capability.IsSupported)
            {
                unsupported.Add(name);
                settings.Add(new DriverProcessingSetting
                {
                    Name = name,
                    Status = DriverProcessingStatus.Unsupported,
                    RequestedValue = requestedValue.Value
                });
                return;
            }
            if (!capability.CanSet || capability.IsReadOnly)
            {
                rejected.Add(name);
                settings.Add(new DriverProcessingSetting
                {
                    Name = name,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue.Value,
                    EffectiveValue = capability.CanGet ? ToBool(capability.GetCurrent()) : null,
                    Message = "The TWAIN capability is read-only."
                });
                return;
            }
            var setResult = capability.SetValue(requestedValue.Value ? BoolType.True : BoolType.False);
            if (!IsSetAccepted(setResult))
            {
                failed.Add(name);
                settings.Add(new DriverProcessingSetting
                {
                    Name = name,
                    Status = DriverProcessingStatus.Failed,
                    RequestedValue = requestedValue.Value,
                    Message = $"The TWAIN source rejected the requested value: {setResult}."
                });
                return;
            }
            var actual = capability.CanGet ? ToBool(capability.GetCurrent()) : requestedValue.Value;
            effective[name] = actual;
            var applied = actual == requestedValue.Value;
            if (!applied) rejected.Add(name);
            settings.Add(new DriverProcessingSetting
            {
                Name = name,
                Status = applied ? DriverProcessingStatus.Applied : DriverProcessingStatus.Rejected,
                RequestedValue = requestedValue.Value,
                EffectiveValue = actual,
                Message = applied ? null : "The TWAIN source applied a different value."
            });
        }
        catch (Exception ex)
        {
            failed.Add(name);
            logger?.LogDebug(ex, "TWAIN capability set failed for {Capability}", name);
            settings.Add(new DriverProcessingSetting
            {
                Name = name,
                Status = DriverProcessingStatus.Failed,
                RequestedValue = requestedValue.Value,
                Message = ex.Message
            });
        }
    }

    private static void ApplyReadOnlyBoolean(IReadOnlyCapWrapper<BoolType> capability, string name,
        bool? requestedValue, IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        if (!requestedValue.HasValue) return;
        requested[name] = requestedValue.Value;
        try
        {
            if (!capability.IsSupported)
            {
                unsupported.Add(name);
                settings.Add(new DriverProcessingSetting
                {
                    Name = name,
                    Status = DriverProcessingStatus.Unsupported,
                    RequestedValue = requestedValue.Value
                });
                return;
            }
            rejected.Add(name);
            var actual = capability.CanGet ? ToBool(capability.GetCurrent()) : (bool?) null;
            if (actual.HasValue) effective[name] = actual.Value;
            settings.Add(new DriverProcessingSetting
            {
                Name = name,
                Status = DriverProcessingStatus.Rejected,
                RequestedValue = requestedValue.Value,
                EffectiveValue = actual,
                Message = "TWAIN exposes automatic crop as a read-only frame policy."
            });
        }
        catch (Exception ex)
        {
            failed.Add(name);
            logger?.LogDebug(ex, "TWAIN capability query failed for {Capability}", name);
            settings.Add(new DriverProcessingSetting
            {
                Name = name,
                Status = DriverProcessingStatus.Failed,
                RequestedValue = requestedValue.Value,
                Message = ex.Message
            });
        }
    }

    private static void ApplyPageSize(DataSource source, bool? requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        if (!requestedValue.HasValue) return;
        requested[AutomaticPageSize] = requestedValue.Value;
        var autoSize = source.Capabilities.ICapAutoSize;
        try
        {
            if (!autoSize.IsSupported)
            {
                ApplyBoolean(source.Capabilities.ICapAutomaticLengthDetection, AutomaticPageSize, requestedValue,
                    requested, effective, settings, rejected, unsupported, failed, logger);
                return;
            }
            if (!autoSize.CanSet || autoSize.IsReadOnly)
            {
                rejected.Add(AutomaticPageSize);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticPageSize,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue.Value,
                    EffectiveValue = autoSize.CanGet && autoSize.GetCurrent() == AutoSize.Auto,
                    Message = "The TWAIN capability is read-only."
                });
                return;
            }
            var setResult = autoSize.SetValue(requestedValue.Value ? AutoSize.Auto : AutoSize.None);
            if (!IsSetAccepted(setResult))
            {
                failed.Add(AutomaticPageSize);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticPageSize,
                    Status = DriverProcessingStatus.Failed,
                    RequestedValue = requestedValue.Value,
                    Message = $"The TWAIN source rejected the requested auto-size mode: {setResult}."
                });
                return;
            }
            var actual = autoSize.CanGet && autoSize.GetCurrent() == AutoSize.Auto;
            effective[AutomaticPageSize] = actual;
            var applied = actual == requestedValue.Value;
            if (!applied) rejected.Add(AutomaticPageSize);
            settings.Add(new DriverProcessingSetting
            {
                Name = AutomaticPageSize,
                Status = applied ? DriverProcessingStatus.Applied : DriverProcessingStatus.Rejected,
                RequestedValue = requestedValue.Value,
                EffectiveValue = actual,
                Message = applied ? null : "The TWAIN source applied a different auto-size mode."
            });
        }
        catch (Exception ex)
        {
            failed.Add(AutomaticPageSize);
            logger?.LogDebug(ex, "TWAIN capability set failed for {Capability}", AutomaticPageSize);
            settings.Add(new DriverProcessingSetting
            {
                Name = AutomaticPageSize,
                Status = DriverProcessingStatus.Failed,
                RequestedValue = requestedValue.Value,
                Message = ex.Message
            });
        }
    }

    private static void ApplyColor(DataSource source, DriverColorDetectionMode? requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        if (!requestedValue.HasValue) return;
        requested[AutomaticColorDetection] = requestedValue.Value;
        var enabled = source.Capabilities.ICapAutomaticColorEnabled;
        var nonColor = source.Capabilities.ICapAutomaticColorNonColorPixelType;
        try
        {
            if (!enabled.IsSupported && !nonColor.IsSupported)
            {
                unsupported.Add(AutomaticColorDetection);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticColorDetection,
                    Status = DriverProcessingStatus.Unsupported,
                    RequestedValue = requestedValue.Value
                });
                return;
            }

            var targetEnabled = requestedValue.Value != DriverColorDetectionMode.Off;
            var targetNonColor = requestedValue.Value switch
            {
                DriverColorDetectionMode.ColorOrGrayscale => PixelType.Gray,
                DriverColorDetectionMode.ColorOrBlackAndWhite => PixelType.BlackWhite,
                _ => (PixelType?) null
            };
            if (targetNonColor.HasValue && (!nonColor.IsSupported || !nonColor.CanSet || nonColor.IsReadOnly))
            {
                rejected.Add(AutomaticColorDetection);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticColorDetection,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue.Value,
                    Message = "The TWAIN source cannot set the requested non-color pixel type."
                });
                return;
            }
            if (!enabled.IsSupported || !enabled.CanSet || enabled.IsReadOnly)
            {
                rejected.Add(AutomaticColorDetection);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticColorDetection,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue.Value,
                    EffectiveValue = enabled.CanGet && ToBool(enabled.GetCurrent()),
                    Message = "The TWAIN automatic-color capability is read-only."
                });
                return;
            }

            if (targetNonColor.HasValue)
            {
                var nonColorSetResult = nonColor.SetValue(targetNonColor.Value);
                if (!IsSetAccepted(nonColorSetResult))
                {
                    failed.Add(AutomaticColorDetection);
                    settings.Add(new DriverProcessingSetting
                    {
                        Name = AutomaticColorDetection,
                        Status = DriverProcessingStatus.Failed,
                        RequestedValue = requestedValue.Value,
                        Message = $"The TWAIN source rejected the requested non-color pixel type: {nonColorSetResult}."
                    });
                    return;
                }
            }
            var enabledSetResult = enabled.SetValue(targetEnabled ? BoolType.True : BoolType.False);
            if (!IsSetAccepted(enabledSetResult))
            {
                failed.Add(AutomaticColorDetection);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticColorDetection,
                    Status = DriverProcessingStatus.Failed,
                    RequestedValue = requestedValue.Value,
                    Message = $"The TWAIN source rejected automatic-color mode: {enabledSetResult}."
                });
                return;
            }
            var actualEnabled = enabled.CanGet && ToBool(enabled.GetCurrent());
            var actualMode = actualEnabled
                ? ToColorMode(nonColor.CanGet ? nonColor.GetCurrent() : null)
                : DriverColorDetectionMode.Off;
            effective[AutomaticColorDetection] = actualMode;
            var applied = actualMode == requestedValue.Value;
            if (!applied) rejected.Add(AutomaticColorDetection);
            settings.Add(new DriverProcessingSetting
            {
                Name = AutomaticColorDetection,
                Status = applied ? DriverProcessingStatus.Applied : DriverProcessingStatus.Rejected,
                RequestedValue = requestedValue.Value,
                EffectiveValue = actualMode,
                Message = applied ? null : "The TWAIN source applied a different automatic-color mode."
            });
        }
        catch (Exception ex)
        {
            failed.Add(AutomaticColorDetection);
            logger?.LogDebug(ex, "TWAIN capability set failed for {Capability}", AutomaticColorDetection);
            settings.Add(new DriverProcessingSetting
            {
                Name = AutomaticColorDetection,
                Status = DriverProcessingStatus.Failed,
                RequestedValue = requestedValue.Value,
                Message = ex.Message
            });
        }
    }

    private static void ApplyBlankPage(DataSource source, bool? requestedValue,
        IDictionary<string, object?> requested, IDictionary<string, object?> effective,
        ICollection<DriverProcessingSetting> settings, ICollection<string> rejected, ICollection<string> unsupported,
        ICollection<string> failed, ILogger? logger)
    {
        if (!requestedValue.HasValue) return;
        requested[AutomaticBlankPageDetection] = requestedValue.Value;
        var capability = source.Capabilities.ICapAutoDiscardBlankPages;
        try
        {
            if (!capability.IsSupported)
            {
                unsupported.Add(AutomaticBlankPageDetection);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticBlankPageDetection,
                    Status = DriverProcessingStatus.Unsupported,
                    RequestedValue = requestedValue.Value
                });
                return;
            }
            if (!capability.CanSet || capability.IsReadOnly)
            {
                rejected.Add(AutomaticBlankPageDetection);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticBlankPageDetection,
                    Status = DriverProcessingStatus.Rejected,
                    RequestedValue = requestedValue.Value,
                    EffectiveValue = capability.CanGet && capability.GetCurrent() == BlankPage.Auto,
                    Message = "The TWAIN blank-page capability is read-only."
                });
                return;
            }
            var setResult = capability.SetValue(requestedValue.Value ? BlankPage.Auto : BlankPage.Disable);
            if (!IsSetAccepted(setResult))
            {
                failed.Add(AutomaticBlankPageDetection);
                settings.Add(new DriverProcessingSetting
                {
                    Name = AutomaticBlankPageDetection,
                    Status = DriverProcessingStatus.Failed,
                    RequestedValue = requestedValue.Value,
                    Message = $"The TWAIN source rejected the requested blank-page mode: {setResult}."
                });
                return;
            }
            var actual = capability.CanGet && capability.GetCurrent() == BlankPage.Auto;
            effective[AutomaticBlankPageDetection] = actual;
            var applied = actual == requestedValue.Value;
            if (!applied) rejected.Add(AutomaticBlankPageDetection);
            settings.Add(new DriverProcessingSetting
            {
                Name = AutomaticBlankPageDetection,
                Status = applied ? DriverProcessingStatus.Applied : DriverProcessingStatus.Rejected,
                RequestedValue = requestedValue.Value,
                EffectiveValue = actual,
                Message = applied ? null : "The TWAIN source applied a different blank-page mode."
            });
        }
        catch (Exception ex)
        {
            failed.Add(AutomaticBlankPageDetection);
            logger?.LogDebug(ex, "TWAIN capability set failed for {Capability}", AutomaticBlankPageDetection);
            settings.Add(new DriverProcessingSetting
            {
                Name = AutomaticBlankPageDetection,
                Status = DriverProcessingStatus.Failed,
                RequestedValue = requestedValue.Value,
                Message = ex.Message
            });
        }
    }

    private static DriverProcessingCapabilityState GetState<T>(IReadOnlyCapWrapper<T> capability)
    {
        if (!capability.IsSupported) return DriverProcessingCapabilityState.Unsupported;
        return capability.IsReadOnly || !capability.CanSet
            ? DriverProcessingCapabilityState.ReadOnly
            : DriverProcessingCapabilityState.Writable;
    }

    private static double ToDouble(TWFix32 value) => value.Whole + value.Fraction / 65536d;

    private static TWFix32 ToFix32(double value)
    {
        var raw = (long) Math.Round(value * 65536d);
        return new TWFix32
        {
            Whole = (short) (raw >> 16),
            Fraction = (ushort) (raw & 0xffff)
        };
    }

    private static double? GetStep(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return null;
        var step = double.MaxValue;
        for (var i = 1; i < values.Count; i++)
        {
            step = Math.Min(step, values[i] - values[i - 1]);
        }
        return step == double.MaxValue ? null : step;
    }

    private static bool NearlyEqual(double actual, double requested) => Math.Abs(actual - requested) < 0.0001;

    private static bool IsSetAccepted(ReturnCode returnCode) => returnCode is ReturnCode.Success or ReturnCode.CheckStatus;

    private static bool ToBool(BoolType value) => value == BoolType.True;

    private static DriverProcessingCapabilityState GetCapabilityState<T>(IReadOnlyCapWrapper<T> capability)
    {
        try
        {
            return GetState(capability);
        }
        catch
        {
            return DriverProcessingCapabilityState.QueryFailed;
        }
    }

    private static double? ToRotationDegrees(OrientationType value) => value switch
    {
        OrientationType.Rot0 => 0,
        OrientationType.Rot90 => 90,
        OrientationType.Rot180 => 180,
        OrientationType.Rot270 => 270,
        _ => null
    };

    private static OrientationType? ToOrientation(double value)
    {
        if (NearlyEqual(value, 0)) return OrientationType.Rot0;
        if (NearlyEqual(value, 90)) return OrientationType.Rot90;
        if (NearlyEqual(value, 180)) return OrientationType.Rot180;
        if (NearlyEqual(value, 270)) return OrientationType.Rot270;
        return null;
    }

    private static bool IsAutomaticOrientation(OrientationType value) => value is OrientationType.Auto or
        OrientationType.AutoTet or OrientationType.AutoPicture;

    private static bool? IsAutomaticOrientation(OrientationType? value) =>
        value.HasValue ? IsAutomaticOrientation(value.Value) : null;

    private static DriverColorDetectionMode ToColorMode(PixelType? value) => value switch
    {
        PixelType.Gray => DriverColorDetectionMode.ColorOrGrayscale,
        PixelType.BlackWhite => DriverColorDetectionMode.ColorOrBlackAndWhite,
        _ => DriverColorDetectionMode.Automatic
    };
}
#endif
