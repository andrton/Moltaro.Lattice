using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// The text keys of a measurement, all of one length, in one pinned array, so that a probe can point at
// them: key n lies at n times the length. From one seed, the same on every run.
// - random: lower-case letters drawn at random, which differ within their first bytes;
// - numbered: as the fixture's logins, "user-" and the number with leading zeros to the length ("u"
//   and seven digits at 8 bytes), so that neighbouring keys share all but their last bytes and a
//   prefix of eight bytes separates nothing.
internal sealed unsafe class TextKeys
{
    private const int Seed = 20261118;

    private readonly byte[] _bytes;

    public TextKeys(string distribution, int count, int length)
    {
        Length = length;
        Count = count;
        _bytes = GC.AllocateArray<byte>(count * length, pinned: true);

        var random = new Random(Seed);

        for (var number = 0; number < count; number++)
        {
            var key = _bytes.AsSpan(number * length, length);

            switch (distribution)
            {
                case "random":
                    for (var place = 0; place < length; place++)
                    {
                        key[place] = (byte)('a' + random.Next(26));
                    }

                    break;

                case "numbered":
                    var head = length >= 16 ? "user-" : "u";
                    var digits = number.ToString(CultureInfo.InvariantCulture).PadLeft(length - head.Length, '0');

                    Encoding.ASCII.GetBytes(head + digits, key);
                    break;

                default:
                    throw new ArgumentException($"No text distribution is named '{distribution}'; use random or numbered.");
            }
        }
    }

    public int Length { get; }

    public int Count { get; }

    public ReadOnlySpan<byte> Key(int number) => _bytes.AsSpan(number * Length, Length);

    public byte* Pointer(int number) => (byte*)Unsafe.AsPointer(ref _bytes[number * Length]);
}
