using System.Text;
using Moltaro.Lattice.Engine.Tests.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Unique;

// A frame of a load or of the stream with the UTF-8 bytes of one text of a root written over by another
// of the same length, the check made again (WireFrame.WithPayload), so that a child receives a root its
// master never held, as a stream that contradicts the rules of the master would carry it. A text this
// short lies in its root, so its descriptor stays valid.
internal static class RootText
{
    // The frame with the text given replaced by the other, true; false when its payload does not hold
    // the text.
    public static bool TryReplace(WireFrame frame, string text, string other, out WireFrame changed)
    {
        var from = Encoding.UTF8.GetBytes(text);
        var to = Encoding.UTF8.GetBytes(other);

        Assert.Equal(from.Length, to.Length);

        var payload = frame.Payload;
        var at = payload.AsSpan().IndexOf(from);

        if (at < 0)
        {
            changed = frame;

            return false;
        }

        to.CopyTo(payload, at);
        changed = frame.WithPayload(payload);

        return true;
    }
}
