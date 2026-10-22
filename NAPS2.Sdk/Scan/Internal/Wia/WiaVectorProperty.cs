#if !MACOS
using System.Reflection;
using System.Runtime.InteropServices;
using NAPS2.Wia;

namespace NAPS2.Scan.Internal.Wia;

/// <summary>
/// Reads and writes VT_VECTOR | VT_I4 WIA properties, such as WIA_IPS_ENABLED_BARCODE_TYPES.
/// </summary>
/// <remarks>
/// <para>
/// NAPS2.Wia only converts VT_I4 and VT_BSTR values. Its <see cref="WiaProperty"/> keeps the item's
/// IWiaPropertyStorage pointer in an internal Storage property, which is read once by reflection; the vector is then
/// read and written with IWiaPropertyStorage::ReadMultiple and ::WriteMultiple (the IPropertyStorage layout, vtable
/// slots 3 and 4). A NAPS2.Wia version without that property makes every vector call fail with a reason, never guess.
/// </para>
/// <para>
/// The calls run on the thread that already uses the item, as NAPS2.Wia's own property calls do.
/// </para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WiaVectorProperty
{
    internal const ushort VtI4Vector = 0x1003; // VT_VECTOR | VT_I4

    private const uint PrspecPropid = 1; // PRSPEC_PROPID
    private const uint PidFirstUsable = 2; // propidNameFirst; no names are written

    private static readonly PropertyInfo? StorageProperty =
        typeof(WiaProperty).GetProperty("Storage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadMultipleFn(IntPtr self, uint count, IntPtr specs, IntPtr variants);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int WriteMultipleFn(IntPtr self, uint count, IntPtr specs, IntPtr variants, uint nameFirst);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr variant);

    // PROPVARIANT: VARTYPE vt; three reserved WORDs; then the union, where CAL is { ULONG cElems; LONG* pElems }.
    private static int VariantSize => IntPtr.Size == 8 ? 24 : 16;

    // PROPSPEC: ULONG ulKind; then a union of PROPID and LPOLESTR, aligned to a pointer.
    private static int SpecSize => IntPtr.Size == 8 ? 16 : 8;

    public static int[] Read(WiaProperty property)
    {
        var storage = Storage(property);
        var spec = Marshal.AllocHGlobal(SpecSize);
        var variant = Marshal.AllocHGlobal(VariantSize);
        try
        {
            WriteSpec(spec, property.Id);
            Zero(variant, VariantSize);
            var read = Method<ReadMultipleFn>(storage, 3);
            Check(read(storage, 1, spec, variant), "ReadMultiple");
            try
            {
                var type = (ushort) Marshal.ReadInt16(variant);
                if (type != VtI4Vector)
                {
                    throw new InvalidOperationException($"The property holds type {type}, not a VT_I4 vector.");
                }
                var count = Marshal.ReadInt32(variant, 8);
                var elements = Marshal.ReadIntPtr(variant, IntPtr.Size == 8 ? 16 : 12);
                var values = new int[count];
                if (count > 0)
                {
                    Marshal.Copy(elements, values, 0, count);
                }
                return values;
            }
            finally
            {
                PropVariantClear(variant);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(spec);
        }
    }

    public static void Write(WiaProperty property, IReadOnlyList<int> values)
    {
        var storage = Storage(property);
        var spec = Marshal.AllocHGlobal(SpecSize);
        var variant = Marshal.AllocHGlobal(VariantSize);
        // PropVariantClear frees the elements with CoTaskMemFree.
        var elements = Marshal.AllocCoTaskMem(Math.Max(1, values.Count) * sizeof(int));
        try
        {
            WriteSpec(spec, property.Id);
            Zero(variant, VariantSize);
            Marshal.Copy(values.ToArray(), 0, elements, values.Count);
            Marshal.WriteInt16(variant, unchecked((short) VtI4Vector));
            Marshal.WriteInt32(variant, 8, values.Count);
            Marshal.WriteIntPtr(variant, IntPtr.Size == 8 ? 16 : 12, elements);
            elements = IntPtr.Zero;
            var write = Method<WriteMultipleFn>(storage, 4);
            Check(write(storage, 1, spec, variant, PidFirstUsable), "WriteMultiple");
        }
        finally
        {
            PropVariantClear(variant);
            if (elements != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(elements);
            }
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(spec);
        }
    }

    private static IntPtr Storage(WiaProperty property)
    {
        if (StorageProperty?.GetValue(property) is not IntPtr storage || storage == IntPtr.Zero)
        {
            throw new NotSupportedException(
                "This NAPS2.Wia version does not expose the property storage needed to access vector properties.");
        }
        return storage;
    }

    private static T Method<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private static void WriteSpec(IntPtr spec, int propertyId)
    {
        Zero(spec, SpecSize);
        Marshal.WriteInt32(spec, (int) PrspecPropid);
        Marshal.WriteInt32(spec, IntPtr.Size, propertyId);
    }

    private static void Zero(IntPtr pointer, int size)
    {
        for (var offset = 0; offset < size; offset += 4)
        {
            Marshal.WriteInt32(pointer, offset, 0);
        }
    }

    private static void Check(int hresult, string call)
    {
        // S_FALSE from ReadMultiple means the property is absent.
        if (hresult != 0)
        {
            throw new COMException($"IWiaPropertyStorage::{call} returned 0x{hresult:X8}.", hresult);
        }
    }
}
#endif
