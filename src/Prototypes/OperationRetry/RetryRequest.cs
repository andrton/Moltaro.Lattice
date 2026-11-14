using System.Security.Cryptography;
using System.Text;

namespace Moltaro.Lattice.Prototypes.OperationRetry;

// Fixed synthetic grammar, not the production schema codec. Adds deliberately model
// non-idempotent effects so a duplicate execution cannot hide behind an Upsert.
internal sealed record RetryRequest(int FirstKey, int SecondKey, int Delta, bool Delete = false)
{
    public string Encode() => FormattableString.Invariant($"v1|{FirstKey}|{SecondKey}|{Delta}|{Delete}");

    public byte[] Fingerprint() => SHA256.HashData(Encoding.UTF8.GetBytes(Encode()));
}
