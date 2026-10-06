namespace NAPS2.Scan.Internal;

/// <summary>
/// One keyed setting request after validation, in the form backends report it.
/// </summary>
/// <param name="Name">The name used in the processing result.</param>
/// <param name="Request">The original request.</param>
/// <param name="RequestedValue">The requested value as a CLR scalar.</param>
/// <param name="Rejection">Why the request is malformed, or null when it can be passed to a binding.</param>
internal readonly record struct KeyedSettingRequest(
    string Name,
    DriverSettingRequest Request,
    object? RequestedValue,
    string? Rejection);

/// <summary>
/// Validates <see cref="DriverProcessingOptions.Settings"/> the same way for every backend.
/// </summary>
internal static class KeyedDriverSettings
{
    private const string UnnamedSetting = "(unnamed setting)";

    // The typed operations have their own members and negotiation code. A keyed request with one of these names would
    // be a second, conflicting request for the same operation.
    private static readonly HashSet<string> TypedOperationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(DriverProcessingOptions.Brightness),
        nameof(DriverProcessingOptions.Contrast),
        nameof(DriverProcessingOptions.RotationDegrees),
        nameof(DriverProcessingOptions.AutomaticOrientation),
        nameof(DriverProcessingOptions.Deskew),
        nameof(DriverProcessingOptions.AutomaticBrightness),
        nameof(DriverProcessingOptions.AutomaticPageSize),
        nameof(DriverProcessingOptions.AutomaticBorderDetection),
        nameof(DriverProcessingOptions.AutomaticCrop),
        nameof(DriverProcessingOptions.AutomaticColorDetection),
        nameof(DriverProcessingOptions.AutomaticBlankPageDetection)
    };

    /// <summary>
    /// Returns every keyed request in order. A malformed request is returned with a rejection rather than dropped, so
    /// the caller can report it.
    /// </summary>
    public static IEnumerable<KeyedSettingRequest> Read(DriverProcessingOptions? options)
    {
        if (options?.Settings == null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in options.Settings)
        {
            if (request == null)
            {
                continue;
            }

            var key = request.Key?.Trim() ?? "";
            var name = key.Length == 0 ? UnnamedSetting : key;
            var value = request.Value?.ToObject();
            string? rejection = null;
            if (key.Length == 0)
            {
                rejection = "A keyed driver setting must have a key.";
            }
            else if (TypedOperationNames.Contains(key))
            {
                rejection = $"'{key}' must be requested through the typed {nameof(DriverProcessingOptions)} member.";
            }
            else if (!seen.Add(key))
            {
                rejection = $"'{key}' was requested more than once; only the first request is used.";
            }
            else if (request.Value is not { HasValue: true })
            {
                rejection = $"'{key}' has no value.";
            }

            yield return new KeyedSettingRequest(name, request, value, rejection);
        }
    }

    /// <summary>
    /// The message for a well-formed key the backend has no binding for.
    /// </summary>
    public static string UnboundMessage(string backend) =>
        $"The {backend} driver has no binding for this setting.";
}
