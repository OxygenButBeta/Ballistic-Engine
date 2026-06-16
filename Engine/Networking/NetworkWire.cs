using BallisticEngine.Networking;

namespace BallisticEngine;

// The wire MESSAGE FRAMING (plan §11/§12.1) — OURS, riding inside the transport's opaque payload. The
// transport (LiteNetLib/loopback) never inspects this; it just delivers the bytes. Every message starts
// with a 1-byte tag so the receiver can demux. Reliability is chosen by the SENDER per message type
// (handshake/spawn/despawn = Reliable; snapshot = Unreliable) and passed to Transport.Send as the Channel.
//
// Kept deliberately small + explicit: P3 carries handshake + spawn + despawn + a delta snapshot batch.
// RPC frames (§4b) are P4; prediction input frames (§8.2 UP) are P5. New tags slot in without touching
// the transport (the §12.1 guarantee).
public enum NetMessage : byte {
    Handshake = 1,   // client->server on connect: the layout-hash digest (gate 0c drift reject)
    HandshakeOk = 2, // server->client: accepted (+ the client's assigned connection id)
    Spawn = 3,       // server->client: netId + typeId + owner + full baseline state -> build the mirror
    Despawn = 4,     // server->client: netId -> tear down the mirror
    Snapshot = 5,    // server->client: a batch of [netId, typeId, delta-state] for dirty objects
}

// Static read/write helpers over BitWriter/BitReader for the frame headers. The per-object STATE bytes
// are produced by the generated SerializeState (§11) — this only frames around them. A digest of all
// registered layout hashes is the handshake guard: a peer on a drifted build mismatches and is rejected
// with an explicit error instead of a silent desync (§8.6.1 — accident-detection, not safe-reload).
public static class NetworkWire {
    // Combine every registered type's (typeId, layoutHash) into one stable digest. Two peers on the same
    // build produce the same digest; any [Networked] field add/reorder/retype on either side shifts it.
    public static int LayoutDigest() {
        // Order-independent fold (XOR of per-type FNV pairs) so a Dictionary's iteration order can't make
        // the digest unstable across peers. Each type folds (typeId, layoutHash) so a hash collision in
        // one axis is caught by the other.
        int digest = 0;
        foreach (NetworkTypeDescriptor d in NetworkReplicationRegistry.All)
            digest ^= WireCodec.Fnv(d.TypeId.ToString(), d.LayoutHash.ToString());
        return digest;
    }

    // ---- message builders (each returns the framed payload bytes) ---------------------------------
    public static byte[] Handshake(int layoutDigest) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Handshake);
        w.WriteInt(layoutDigest);
        return w.AsSpan().ToArray();
    }

    public static byte[] HandshakeOk(int assignedConnectionId) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.HandshakeOk);
        w.WriteInt(assignedConnectionId);
        return w.AsSpan().ToArray();
    }

    // Spawn carries the object identity + a FULL state snapshot (every field, vs a zero baseline) so the
    // client mirror starts correct (the §8.5 "OnSpawned = baseline delivered atomically" invariant).
    public static byte[] Spawn(int netId, int typeId, int ownerId, NetworkBehaviour state) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Spawn);
        w.WriteInt(netId);
        w.WriteInt(typeId);
        w.WriteInt(ownerId);
        // FULL snapshot (every field, mask all-set) — NOT a delta. A delta would be empty here because the
        // server captured the baseline at spawn (live == baseline), so the mirror would start at defaults.
        state.SerializeFullState(w);
        return w.AsSpan().ToArray();
    }

    public static byte[] Despawn(int netId) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Despawn);
        w.WriteInt(netId);
        return w.AsSpan().ToArray();
    }

    public static byte ReadTag(ReadOnlySpan<byte> payload) =>
        payload.Length > 0 ? payload[0] : (byte)0;
}
