using System.Reflection;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// Sizes actual backings independently of the engine's reservation arithmetic on the 64-bit manifest.
internal sealed class ManagedBackingSize
{
    private readonly Dictionary<Type, int> _arrayWidths = [];
    private static readonly Func<object, nuint> RawObjectSize = typeof(RuntimeHelpers)
        .GetMethod("GetRawObjectDataSize", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<object, nuint>>();

    public long Of(object backing)
    {
        var type = backing.GetType();
        if (backing is Array array)
        {
            if (!_arrayWidths.TryGetValue(type, out var width))
            {
                var element = type.GetElementType()!;
                width = element.IsValueType
                    ? (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(element).Invoke(null, null)!
                    : IntPtr.Size;
                _arrayWidths.Add(type, width);
            }

            return Align(3L * IntPtr.Size + array.LongLength * width);
        }

        // A weak runtime type cache can be recreated between measuring a pair of
        // allocations. Read the size of the actual backing instead.
        return Align(checked((long)RawObjectSize(backing) + 2L * IntPtr.Size));
    }

    private static long Align(long bytes) => checked((bytes + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size);

    public static object? Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner);
}
