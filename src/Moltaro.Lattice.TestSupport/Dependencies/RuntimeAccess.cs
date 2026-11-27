using System.Reflection;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.TestSupport.Dependencies;

// What a shipped assembly reaches of other assemblies past their public surface: the members that
// carry an UnsafeAccessor, each named with the type it is aimed at — the type of its first
// parameter, or of the value it makes for a constructor, or the type an UnsafeAccessorType names.
// A shipped assembly may aim one only at a type of the shipped assemblies of Lattice.
internal static class RuntimeAccess
{
    public static IReadOnlyList<string> AccessorsAimedOutside(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly;
        var found = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(all))
            {
                if (method.GetCustomAttribute<UnsafeAccessorAttribute>() is not { } accessor)
                {
                    continue;
                }

                var target = Target(method, accessor.Kind);
                if (target?.Assembly.GetName().Name?.StartsWith("Moltaro.Lattice", StringComparison.Ordinal) != true)
                {
                    found.Add($"{type.FullName}.{method.Name} aimed at {target?.FullName ?? "a type it names"}");
                }
            }
        }

        return found;
    }

    private static Type? Target(MethodInfo method, UnsafeAccessorKind kind)
    {
        if (kind == UnsafeAccessorKind.Constructor)
        {
            return method.ReturnType;
        }

        var first = method.GetParameters().FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        if (first.GetCustomAttribute<UnsafeAccessorTypeAttribute>() is { } named)
        {
            return Type.GetType(named.TypeName, throwOnError: false);
        }

        return first.ParameterType.IsByRef ? first.ParameterType.GetElementType() : first.ParameterType;
    }
}
