using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// A bound model and the ordinary typed read of that same model.
internal sealed record CollectionMatrixBinding(
    string TypeName,
    Func<EngineDatabase, object, int, int, object?, LatticeRevision, Action> Capture)
{
    public static CollectionMatrixBinding? Of(LatticeFieldTypeEnum key, CollectionMatrixProfile profile) => key switch
    {
        LatticeFieldTypeEnum.String => ForKey<string>(profile),
        LatticeFieldTypeEnum.UInt64 => ForKey<ulong>(profile),
        _ => null,
    };

    private static CollectionMatrixBinding ForKey<TKey>(CollectionMatrixProfile profile) where TKey : notnull =>
        profile.Name switch
        {
            "PackedList" => Make<TKey, List<long?>>(profile.Kind),
            "RecordList" => Make<TKey, List<string?>>(profile.Kind),
            "PackedMap" => Make<TKey, Dictionary<long, sbyte>>(profile.Kind),
            "UnsignedMap" => Make<TKey, Dictionary<ulong, sbyte>>(profile.Kind),
            "WideMap" => Make<TKey, Dictionary<long, long>>(profile.Kind),
            "RecordMap" => Make<TKey, Dictionary<string, string?>>(profile.Kind),
            "PackedSet" => Make<TKey, HashSet<long?>>(profile.Kind),
            "WideSet" => Make<TKey, HashSet<Guid?>>(profile.Kind),
            "RecordSet" => Make<TKey, HashSet<string?>>(profile.Kind),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Name, "The collection profile has no CLR binding."),
        };

    private static CollectionMatrixBinding Make<TKey, TCollection>(LatticeFieldTypeEnum kind)
        where TKey : notnull
        where TCollection : class
    {
        return new CollectionMatrixBinding(typeof(OwnedCollectionItem<TKey, TCollection>).FullName!, Capture);

        Action Capture(EngineDatabase node, object key, int ordinal, int stamp, object? expected, LatticeRevision revision)
        {
            var primary = (TKey)key;
            var set = node.Set<OwnedCollectionItem<TKey, TCollection>>();
            Assert.True(set.TryGet(primary, out var item, out var seen));
            Assert.Equal(revision, seen);
            CheckCopy();
            return CheckCopy;

            void CheckCopy()
            {
                Assert.Equal(primary, item.Id);
                Assert.Equal(ordinal, item.Case);
                Assert.Equal(stamp, item.Stamp);
                TypedOwnedCollectionTransferTests.AssertCollection((TCollection?)expected, item.Values, kind);
            }
        }
    }
}
