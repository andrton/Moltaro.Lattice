using System.Reflection;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

// Traverse an actual holder independently of the admission ledger. Embedded values
// expose references, but a reflection-created box does not become a backing.
internal static class OwnedBackingGraph
{
    public static HashSet<object> Find(object owner, Predicate<object>? isShared = null, Predicate<FieldInfo>? sharedEdge = null)
    {
        var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var fields = new Dictionary<Type, FieldInfo[]>();
        var pending = new Stack<(object? Item, bool Embedded)>();
        pending.Push((owner, false));
        while (pending.TryPop(out var next))
        {
            var (item, embedded) = next;
            if (item is null or Array { Length: 0 } || isShared?.Invoke(item) == true)
            {
                continue;
            }

            if (item is Delegate callback)
            {
                var invocations = InvocationBacking(callback);
                var target = callback.Target;
                if ((invocations is not null || (target is not null && Fields(target.GetType()).Length != 0)) && owned.Add(callback))
                {
                    pending.Push((target, false));
                    pending.Push((invocations, false));
                }

                continue;
            }

            if (!embedded && !owned.Add(item))
            {
                continue;
            }

            var type = item.GetType();
            if (item is Array array)
            {
                var element = type.GetElementType()!;
                if (!element.IsPrimitive && !element.IsEnum)
                {
                    foreach (var value in array)
                    {
                        pending.Push((value, element.IsValueType));
                    }
                }

                continue;
            }

            if (type.IsPrimitive || type.IsEnum || item is string)
            {
                continue;
            }

            Assert.False(type.IsDefined(typeof(InlineArrayAttribute), inherit: false),
                $"The owned inline array {type.Name} needs a witness for every slot.");
            foreach (var field in Fields(type))
            {
                if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum && !field.FieldType.IsPointer &&
                    sharedEdge?.Invoke(field) != true)
                {
                    pending.Push((field.GetValue(item), field.FieldType.IsValueType));
                }
            }
        }

        return owned;

        object[]? InvocationBacking(Delegate callback) =>
            Fields(callback.GetType()).SingleOrDefault(field => field.DeclaringType == typeof(MulticastDelegate) &&
                field.Name == "_invocationList")?.GetValue(callback) as object[];

        FieldInfo[] Fields(Type type)
        {
            if (!fields.TryGetValue(type, out var members))
            {
                const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var all = new List<FieldInfo>();
                for (var level = type; level is not null; level = level.BaseType)
                {
                    all.AddRange(level.GetFields(declared));
                }

                members = [.. all];
                fields.Add(type, members);
            }

            return members;
        }
    }
}
