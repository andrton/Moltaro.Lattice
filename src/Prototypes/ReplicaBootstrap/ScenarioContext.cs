namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal sealed class ScenarioContext(int maximumPending = 128) : IDisposable
{
    public static RootKey A { get; } = new(1, 1);
    public static RootKey B { get; } = new(2, 1);
    public static RootKey C { get; } = new(1, 2);

    public MasterModel Master { get; } = new(maximumPending);
    public ReferenceModel Reference { get; } = new();
    public ReplicaModel Replica { get; private set; } = null!;
    public CaptureStart Start { get; private set; } = null!;
    public int Captured { get; private set; }
    public long Boundary { get; private set; }

    public PublishedTransaction Write(params Mutation[] mutations)
    {
        var transaction = Master.Publish(mutations);
        Reference.Apply(mutations);
        return transaction;
    }

    public void Open(ReplayRuleEnum rule = ReplayRuleEnum.PerRoot)
    {
        Start = Master.StartCapture();
        Replica = new ReplicaModel(Start, rule);
    }

    public void CopyNext()
    {
        Replica.Install(CaptureDetached());
    }

    public CaptureFrame CaptureDetached()
    {
        var frame = Master.CaptureNext();
        Captured++;
        return frame;
    }

    public void CompleteBase()
    {
        while (Captured < Start.SlotCount) { CopyNext(); }
        Boundary = Master.CompleteCapture();
        Replica.CompleteBase(Boundary);
    }

    public static void Deliver(ReplicaModel replica, PublishedTransaction transaction)
    {
        replica.BeginTransaction(transaction.Position, transaction.Effects.Length);
        foreach (var effect in transaction.Effects) { replica.ReceiveEffect(effect); }
        replica.CommitFrame();
    }

    public void Finish(bool verify = true)
    {
        CompleteBase();
        foreach (var transaction in Master.Pending().Where(item => item.Position <= Boundary))
        {
            Deliver(Replica, transaction);
            Check.That(Replica.AppliedPosition is null, "Bootstrap must not acknowledge a mixed-version arena.");
        }
        Replica.MarkReady();
        Master.Acknowledge(Replica.AppliedPosition!.Value);
        Check.That(Master.PendingCount == 0, "Acknowledged delivery was retained.");
        Check.That(Master.RetainedDeletionCount == 0 && Replica.DeletionWatermarks == 0,
            "Initialization tombstones survived completed handoff.");
        if (verify) { Verify(); }
    }

    public void Verify() => Check.That(Check.Same(Reference.Values, Replica.Observe()), "Replica differs from logical oracle.");

    public void Dispose()
    {
        Replica?.Dispose();
        Master.Dispose();
    }
}
