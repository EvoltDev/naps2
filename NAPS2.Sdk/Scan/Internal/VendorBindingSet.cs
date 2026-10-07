using System.Globalization;

namespace NAPS2.Scan.Internal;

/// <summary>
/// One vendor binding with the driver version it requires.
/// </summary>
/// <param name="Binding">The binding. Vendor bindings set <see cref="NativeSettingBinding.RequireExactType"/>.</param>
/// <param name="MinimumDriverVersion">
/// The first driver version the binding is documented for, or null when the documentation gives none. A binding with
/// a minimum is never used when the driver version cannot be read.
/// </param>
internal sealed record VendorBinding(NativeSettingBinding Binding, Version? MinimumDriverVersion = null);

/// <summary>
/// Vendor-defined bindings that apply only to sources whose reported identity matches the vendor.
/// </summary>
/// <remarks>
/// Custom capability and property ids are vendor-scoped: the same id means different things to different vendors,
/// and sometimes to different driver versions of one vendor. A vendor binding is therefore offered only when the
/// identity matches, the driver version meets the binding's documented minimum, and — when it is written — the
/// source reports the capability with the documented type.
/// </remarks>
internal sealed class VendorBindingSet
{
    public required string Name { get; init; }

    public required Func<DriverDeviceIdentity, bool> Matches { get; init; }

    public required IReadOnlyList<VendorBinding> Bindings { get; init; }

    public IEnumerable<NativeSettingBinding> Applicable(DriverDeviceIdentity? identity)
    {
        if (identity == null || !Matches(identity))
        {
            return [];
        }
        var version = ParseVersion(identity.DriverVersion);
        return Bindings
            .Where(x => x.MinimumDriverVersion == null || version != null && version >= x.MinimumDriverVersion)
            .Select(x => x.Binding);
    }

    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var parts = text!.Trim().Split('.');
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }
        return new Version(major, minor);
    }
}
