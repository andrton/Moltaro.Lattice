using System.Diagnostics.CodeAnalysis;

namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal sealed class ReplicaModel(CaptureStart start, ReplayRuleEnum rule = ReplayRuleEnum.PerRoot) : IDisposable
{
    #region State and capture installation

    // A protocol-model map and publication gate, not the production primary index or lock implementation.
    private readonly object _gate = new();
    private readonly Dictionary<RootKey, RootEntry> _entries = [];
    private int _installedFrames;
    private long _processed = start.Position;
    private long _maximumBaseRevision;
    private long? _boundary;
    private long? _framePosition;
    private ResolvedEffect[]? _frameEffects;
    private int _receivedEffects;

    public ReplicaStateEnum State { get; private set; } = ReplicaStateEnum.Loading;
    public long? AppliedPosition { get; private set; }
    public int DeletionWatermarks => _entries.Values.Count(entry => entry.Block is null);
    public int SkippedEffects { get; private set; }

    public void Install(CaptureFrame frame)
    {
        lock (_gate)
        {
            Require(State == ReplicaStateEnum.Loading && frame.Slot == _installedFrames
                && _installedFrames < start.SlotCount, "Missing, duplicate or late base frame.");
            if (frame.Key is { } key && (frame.Block is not null || rule != ReplayRuleEnum.IgnoreDeletionWatermarks))
            {
                Require(!_entries.ContainsKey(key), "Repeated base identity.");
                _entries.Add(key, new RootEntry(key, frame.Revision,
                    frame.Block is null ? null : new NativeBlock(frame.Block)));
            }
            _maximumBaseRevision = Math.Max(_maximumBaseRevision, frame.Revision);
            _installedFrames++;
        }
    }

    public void CompleteBase(long boundary)
    {
        lock (_gate)
        {
            Require(State == ReplicaStateEnum.Loading && _installedFrames == start.SlotCount
                && boundary >= start.Position && boundary >= _maximumBaseRevision,
                "Incomplete capture or invalid readiness boundary.");
            _boundary = boundary;
            State = ReplicaStateEnum.CatchingUp;
        }
    }

    #endregion

    #region Transaction framing and publication

    public void BeginTransaction(long position, int effectCount)
    {
        lock (_gate)
        {
            Require(State is ReplicaStateEnum.CatchingUp or ReplicaStateEnum.Ready,
                "Replica cannot accept a transaction in this state.");
            Require(_frameEffects is null && effectCount is >= 1 and <= 4, "Invalid or overlapping frame.");
            Require(position <= _processed || position == _processed + 1, "Missing transaction range.");
            _framePosition = position;
            _frameEffects = new ResolvedEffect[effectCount];
            _receivedEffects = 0;
        }
    }

    public void ReceiveEffect(ResolvedEffect effect)
    {
        lock (_gate)
        {
            Require(_frameEffects is not null && _receivedEffects < _frameEffects.Length, "Unexpected effect.");
            _frameEffects![_receivedEffects++] = effect;
        }
    }

    public void CommitFrame()
    {
        lock (_gate)
        {
            Require(_frameEffects is not null && _receivedEffects == _frameEffects.Length,
                "Incomplete batch frame.");
            var position = _framePosition!.Value;
            if (position > _processed)
            {
                Require(State != ReplicaStateEnum.CatchingUp || position <= _boundary,
                    "Live changes must follow readiness at the fixed boundary.");
                Publish(position, _frameEffects!);
                _processed = position;
                if (State == ReplicaStateEnum.Ready)
                {
                    AppliedPosition = position;
                    PruneDeletionWatermarks();
                }
            }
            _framePosition = null;
            _frameEffects = null;
            _receivedEffects = 0;
        }
    }

    private void Publish(long position, ResolvedEffect[] effects)
    {
        var staged = new List<RootEntry>();
        try
        {
            foreach (var effect in effects)
            {
                _entries.TryGetValue(effect.Key, out var current);
                var covered = current is not null && current.Revision >= position;
                if (rule == ReplayRuleEnum.MaximumBaseRevision)
                {
                    covered = _maximumBaseRevision >= position;
                }
                if (rule != ReplayRuleEnum.Unconditional && covered)
                {
                    SkippedEffects++;
                    continue;
                }
                if (effect.Kind != MutationKindEnum.Upsert)
                {
                    Require(current?.Block is not null, "Missing root for a partial effect.");
                    Require(rule == ReplayRuleEnum.Unconditional || current.Revision == effect.BeforeRevision,
                        "Root predecessor mismatch.");
                }

                byte[]? bytes;
                switch (effect.Kind)
                {
                    case MutationKindEnum.Upsert:
                        bytes = effect.FullBlock ?? throw new ProtocolException("Missing Upsert image.");
                        break;
                    case MutationKindEnum.SetNumber:
                        bytes = current!.Block!.Copy();
                        BlockCodec.SetNumber(bytes, effect.Number);
                        break;
                    case MutationKindEnum.Append:
                        bytes = current!.Block!.Copy();
                        BlockCodec.Append(bytes, effect.Number);
                        break;
                    case MutationKindEnum.Delete:
                        bytes = null;
                        break;
                    default:
                        throw new ProtocolException("Unknown effect.");
                }
                staged.Add(new RootEntry(effect.Key, position, bytes is null ? null : new NativeBlock(bytes)));
            }
            _entries.EnsureCapacity(_entries.Count + staged.Count);
            foreach (var entry in staged)
            {
                if (_entries.Remove(entry.Key, out var old)) { old.Block?.Dispose(); }
                _entries.Add(entry.Key, entry);
            }
            staged.Clear();
        }
        finally
        {
            foreach (var entry in staged) { entry.Block?.Dispose(); }
        }
    }

    #endregion

    #region Readiness and observation

    public void MarkReady()
    {
        lock (_gate)
        {
            Require(State == ReplicaStateEnum.CatchingUp && _frameEffects is null && _processed == _boundary,
                "Cannot expose a partial capture or batch.");
            AppliedPosition = _processed;
            PruneDeletionWatermarks();
            State = ReplicaStateEnum.Ready;
        }
    }

    // Test observation under the model gate, not a proposed multi-root public read API.
    public Dictionary<RootKey, DocumentValue> Observe()
    {
        lock (_gate)
        {
            if (State != ReplicaStateEnum.Ready) { throw new ProtocolException("Replica is not ready."); }
            return _entries.Values.Where(entry => entry.Block is not null)
                .ToDictionary(entry => entry.Key, entry => BlockCodec.Decode(entry.Block!.Copy()));
        }
    }

    private void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (condition) { return; }
        State = ReplicaStateEnum.ReloadRequired;
        throw new ProtocolException(message);
    }

    private void PruneDeletionWatermarks()
    {
        foreach (var key in _entries.Where(pair => pair.Value.Block is null).Select(pair => pair.Key).ToArray())
        {
            _entries.Remove(key);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values) { entry.Block?.Dispose(); }
            _entries.Clear();
            _frameEffects = null;
        }
    }

    #endregion
}
