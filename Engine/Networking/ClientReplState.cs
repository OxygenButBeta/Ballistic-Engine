namespace BallisticEngine;

sealed class ClientReplState {
    public readonly Dictionary<int, object> Baseline = new();

    public readonly Dictionary<uint, Dictionary<int, object>> Pending = new();

    public readonly Dictionary<int, object> SceneBaseline = new();
    public readonly Dictionary<uint, Dictionary<int, object>> ScenePending = new();
    public uint SceneSendSeq;

    public uint SendSeq;

    public readonly HashSet<int> Relevant = new();

    public readonly HashSet<int> ReseedOnRegain = new();

    public void SeedBaseline(int netId, object token) => Baseline[netId] = token;

    public void Ack(uint ackedSeq) => Promote(Pending, Baseline, ackedSeq);

    public void AckScene(uint ackedSeq) => Promote(ScenePending, SceneBaseline, ackedSeq);

    static void Promote(Dictionary<uint, Dictionary<int, object>> pending, Dictionary<int, object> baseline,
        uint ackedSeq) {
        if (pending.Count == 0)
            return;
        List<uint> toPromote = null;
        foreach (uint seq in pending.Keys)
            if (seq <= ackedSeq)
                (toPromote ??= new()).Add(seq);
        if (toPromote is null)
            return;
        toPromote.Sort();
        foreach (uint seq in toPromote) {
            foreach (var kv in pending[seq])
                baseline[kv.Key] = kv.Value;
            pending.Remove(seq);
        }
    }

    public void SeedScene(int replicationId, object token) => SceneBaseline[replicationId] = token;

    public void Forget(int netId) {
        Baseline.Remove(netId);
        foreach (var p in Pending.Values)
            p.Remove(netId);
        Relevant.Remove(netId);
        ReseedOnRegain.Remove(netId);
    }
}
