namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// The master never executes a request whose last frame it did not receive (brief of Epic 1, sprint S12,
// decided after the study, points 7 and 12): an incomplete request is dropped when its connection ends.
// A child made by hand (LoadedPeer) sends a Delete of "code" cut into frames of a few bytes, all but its
// last frame, and ends its connection; once the master holds nothing for it any more, "code" is there with
// the revision it had.
public sealed class IncompleteRequestTests
{
    [Fact]
    public async Task Master_never_executes_a_request_whose_last_frame_it_did_not_receive()
    {
        await using var master = await LoadedPeer.OpenMasterAsync();
        var revision = LoadedPeer.RevisionOfCode(master);
        List<WireFrame> frames;

        using (var child = await LoadedPeer.ConnectAsync(master))
        {
            frames = child.RequestFrames(1, LoadedPeer.DeleteOfCode(), framePayload: 8);

            foreach (var frame in frames.SkipLast(1))
            {
                await child.Peer.SendAsync(frame);
            }
        }

        await LoadedPeer.ExpectNothingRegisteredAsync(master);

        Assert.True(frames.Count > 2, "The request does not have several frames.");
        Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));
    }
}
