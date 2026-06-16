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
    Rpc = 6,         // either direction: netId + typeId + methodId + packed args -> dispatch + invoke (P4)
    Input = 7,       // client->server: a batch of the owner's per-tick NetworkInput (the UP stream, P5b)
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
    // P5f: it also carries the echoed PREDICTION KEY — non-zero when this authoritative spawn answers a
    // client's predicted spawn (§8.5.1), so the owning client LINKS it to its predicted object instead of
    // building a duplicate. 0 = a normal (non-predicted) spawn.
    public static byte[] Spawn(int netId, int typeId, int ownerId, NetworkBehaviour state, uint predictKey = 0) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Spawn);
        w.WriteInt(netId);
        w.WriteInt(typeId);
        w.WriteInt(ownerId);
        w.WriteUInt(predictKey);   // P5f: 0 = normal spawn; non-zero = echoes a client's predicted spawn
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

    // An RPC frame (P4, plan §4b): the target object + method, then the packed args (the generated send
    // stub wrote `args` via WireCodec — this only frames the header around them). The SAME frame format is
    // used in BOTH directions (client->server To.Server, server->client To.Owner/To.All) — the receiver
    // looks the methodId's declared target up in the registry to apply the right owner-check. Reliability
    // is the channel the SENDER picks (the [Rpc] Reliable flag), not part of the bytes.
    public static byte[] Rpc(int netId, int typeId, int methodId, ReadOnlySpan<byte> args) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Rpc);
        w.WriteInt(netId);
        w.WriteInt(typeId);
        w.WriteInt(methodId);
        // Append the pre-packed arg bytes bit-for-bit. The args were written by a fresh BitWriter starting
        // at bit 0, so they are byte-aligned blocks; copy them after the (byte-aligned) header. The reader
        // resumes at the same bit offset because every header field is a whole 32 bits.
        var combined = new byte[w.ByteLength + args.Length];
        w.AsSpan().CopyTo(combined);
        args.CopyTo(combined.AsSpan(w.ByteLength));
        return combined;
    }

    // The input UP frame (P5b, plan §8.2 / §14 item 3): the owner's netId + a batch of per-tick
    // NetworkInputs since the last send boundary. Sent Reliable-ordered so the server never misses a tick
    // (input-starvation = the server can't simulate the in-between ticks). The batch carries ALL buffered
    // ticks (asymmetric up-rate: per-tick recorded, batched on the boundary, none dropped).
    public static byte[] Input(int netId, ReadOnlySpan<NetworkInput> batch) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Input);
        w.WriteInt(netId);
        w.WriteByte((byte)Math.Min(batch.Length, 255));
        for (int i = 0; i < batch.Length && i < 255; i++)
            batch[i].Write(w);
        return w.AsSpan().ToArray();
    }

    public static byte ReadTag(ReadOnlySpan<byte> payload) =>
        payload.Length > 0 ? payload[0] : (byte)0;
}
