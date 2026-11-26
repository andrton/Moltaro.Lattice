using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Budgeting;

// Explicit capacity changes reserve the replacement before the collection allocates it. The
// .NET 10 backing fields identify the actual arrays for the independent test inventory.
internal static class BudgetCollections
{
    public static ref T[] Items<T>(List<T> list) => ref Backing<T>.Items(list);

    public static ref T[] Items<T>(Queue<T> queue) => ref Backing<T>.Items(queue);

    // A queue can return to a prepaid backing even after dequeue and wraparound.
    public static void UseBacking<T>(this Queue<T> queue, T[] backing)
    {
        queue.CopyTo(backing, 0);
        Items(queue) = backing;
        Backing<T>.Head(queue) = 0;
        Backing<T>.Tail(queue) = queue.Count == backing.Length ? 0 : queue.Count;
        Backing<T>.Version(queue)++;
    }

    private static class Backing<T>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]
        public static extern ref T[] Items(List<T> list);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_array")]
        public static extern ref T[] Items(Queue<T> queue);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_head")]
        public static extern ref int Head(Queue<T> queue);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_tail")]
        public static extern ref int Tail(Queue<T> queue);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
        public static extern ref int Version(Queue<T> queue);
    }

    public static int CapacityFor(int current, int needed) =>
        Math.Max(needed, current == 0 ? 4 : (int)Math.Min(2L * current, Array.MaxLength));

    public static void EnsureCapacity<T>(this List<T> list, int count, MemoryBudget? budget, string owner,
        bool restoresPreparation = false)
    {
        if (count <= list.Capacity)
        {
            return;
        }

        if (budget is null)
        {
            list.EnsureCapacity(count);
            return;
        }

        var previous = Items(list);
        var next = CapacityFor(previous.Length, count);
        var bytes = MemoryBudget.ArrayBytes<T>(next);
        budget.Reserve(bytes);

        try
        {
            list.Capacity = next;
            var replacement = Items(list);
            budget.Own(replacement, owner);
            if (restoresPreparation)
            {
                budget.Replace(previous, replacement, restored =>
                {
                    list.Clear();
                    Array.Clear(restored);
                    Items(list) = restored;
                });
            }
            else
            {
                budget.Release(previous);
            }
        }
        catch
        {
            Items(list) = previous;
            budget.Release(bytes);
            throw;
        }

    }

    public static void EnsureCapacity<T>(this Queue<T> queue, int count, MemoryBudget? budget, string owner)
    {
        if (budget is null)
        {
            queue.EnsureCapacity(count);
            return;
        }

        var previous = Items(queue);
        if (count <= previous.Length)
        {
            return;
        }

        var next = CapacityFor(previous.Length, count);
        var bytes = MemoryBudget.ArrayBytes<T>(next);
        budget.Reserve(bytes);

        try
        {
            queue.EnsureCapacity(next);
            budget.Own(Items(queue), owner);
        }
        catch
        {
            budget.Release(bytes);
            throw;
        }

        budget.Release(previous);
    }
}
