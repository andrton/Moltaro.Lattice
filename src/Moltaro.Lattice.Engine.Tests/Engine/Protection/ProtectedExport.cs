using System.Buffers;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

internal static class ProtectedExport
{
    public static byte[] Capture<TKey>(ILatticeSet items, TKey key, Arena arena, RootLayout layout, ArenaAddress root,
        bool checkLength = true)
        where TKey : notnull
    {
        Assert.True(items.TryView(key, _ =>
        {
            var output = new ArrayBufferWriter<byte>();
            BlockExport.Write(output, arena, layout, root);
            if (checkLength)
            {
                Assert.Equal(output.WrittenCount, BlockExport.Length(arena, layout, root));
            }

            return output.WrittenSpan.ToArray();
        }, out var bytes));
        return bytes;
    }
}
