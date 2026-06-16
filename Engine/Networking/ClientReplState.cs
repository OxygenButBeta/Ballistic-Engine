namespace BallisticEngine;

// P6 PER-CLIENT delta baseline (plan §13 late-join). P3 used ONE global per-object baseline — correct for
// a single observer, but wrong for multiple clients joining at different times (a delta sent only to client
// A advances the shared baseline so client B never learns it). The fix proven in %TEMP%\bal-baseline-test:
// each client gets exactly the deltas since ITS OWN last ack.
//
// Per client the server holds:
//   - baseline:  netId -> the boxed baseline TOKEN (the generated __NetBaseline struct) that this client has
//                ACKNOWLEDGED. SerializeState diffs the live values against it (via the swap in FlushStateDown).
//   - pending:   sendSeq -> (netId -> the token we SENT at that seq). On ack we promote pending <= the acked
//                seq into `baseline` (the client now holds those values). An UNACKED (dropped) send leaves
//                the baseline un-advanced, so the change re-diffs + re-sends next flush (latest-wins recovery).
//   - sendSeq:   a per-client monotonic snapshot sequence the client echoes in its Ack.
//
// The token is OPAQUE here (boxed by the component's __GetNetBaseline / consumed by __SetNetBaseline) — the
// manager only round-trips it; it never inspects fields, so this stays generator-agnostic and reflection-free.
sealed class ClientReplState {
    // netId -> the baseline token this client has acknowledged (what SerializeState diffs against).
    public readonly Dictionary<int, object> Baseline = new();

    // sendSeq -> (netId -> the token sent at that seq), awaiting ack. Promoted into Baseline on ack.
    public readonly Dictionary<uint, Dictionary<int, object>> Pending = new();

    public uint SendSeq;   // last assigned per-client snapshot seq (incremented per flush that sends something)

    // Seed (or re-seed) this client's baseline for one object to a token — used at join (the atomic
    // late-join baseline = the spawn's current values) and when an object is first spawned for the client.
    public void SeedBaseline(int netId, object token) => Baseline[netId] = token;

    // Advance the baseline to everything sent at seq <= acked (promote pending, drop the acked-or-older).
    public void Ack(uint ackedSeq) {
        if (Pending.Count == 0)
            return;
        // Promote in seq order so a later seq's token wins for the same object (it is the newer value).
        List<uint> toPromote = null;
        foreach (uint seq in Pending.Keys)
            if (seq <= ackedSeq)
                (toPromote ??= new()).Add(seq);
        if (toPromote is null)
            return;
        toPromote.Sort();
        foreach (uint seq in toPromote) {
            foreach (var kv in Pending[seq])
                Baseline[kv.Key] = kv.Value;
            Pending.Remove(seq);
        }
    }

    // Drop an object entirely from this client's bookkeeping (despawn) so a recycled netId/slot doesn't
    // inherit a stale baseline (the §8.5.4 pooling invariant — the generation in the netId already keeps
    // identities distinct, but clearing here avoids carrying dead tokens).
    public void Forget(int netId) {
        Baseline.Remove(netId);
        foreach (var p in Pending.Values)
            p.Remove(netId);
    }
}
