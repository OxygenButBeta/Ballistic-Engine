using BallisticEngine.Networking;

namespace BallisticEngine;

public static class NetworkWire {
    public static int LayoutDigest() {
        int digest = 0;
        foreach (NetworkTypeDescriptor d in NetworkReplicationRegistry.All)
            digest ^= WireCodec.Fnv(d.TypeId.ToString(), d.LayoutHash.ToString());
        foreach (SceneReplDescriptor d in SceneReplicationRegistry.All)
            digest ^= WireCodec.Fnv(d.TypeId.ToString(), d.LayoutHash.ToString());
        return digest;
    }

    public static byte[] Handshake(int layoutDigest, ConnectionToken token = default) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Handshake);
        w.WriteInt(layoutDigest);
        w.WriteUInt((uint)(token.Hi >> 32)); w.WriteUInt((uint)token.Hi);
        w.WriteUInt((uint)(token.Lo >> 32)); w.WriteUInt((uint)token.Lo);
        return w.AsSpan().ToArray();
    }

    public static ConnectionToken ReadToken(ref BitReader r) {
        ulong hi = ((ulong)r.ReadUInt() << 32) | r.ReadUInt();
        ulong lo = ((ulong)r.ReadUInt() << 32) | r.ReadUInt();
        return new ConnectionToken(hi, lo);
    }

    public static byte[] HandshakeOk(int assignedConnectionId, ConnectionToken token = default) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.HandshakeOk);
        w.WriteInt(assignedConnectionId);
        w.WriteUInt((uint)(token.Hi >> 32)); w.WriteUInt((uint)token.Hi);
        w.WriteUInt((uint)(token.Lo >> 32)); w.WriteUInt((uint)token.Lo);
        return w.AsSpan().ToArray();
    }

    public static byte[] Spawn(int netId, int typeId, int ownerId, NetworkBehaviour state, uint predictKey = 0) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Spawn);
        w.WriteInt(netId);
        w.WriteInt(typeId);
        w.WriteInt(ownerId);
        w.WriteUInt(predictKey);
        state.SerializeFullState(w);
        return w.AsSpan().ToArray();
    }

    public static byte[] Despawn(int netId) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Despawn);
        w.WriteInt(netId);
        return w.AsSpan().ToArray();
    }

    public static byte[] Rpc(int netId, int typeId, int methodId, ReadOnlySpan<byte> args) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Rpc);
        w.WriteInt(netId);
        w.WriteInt(typeId);
        w.WriteInt(methodId);
        var combined = new byte[w.ByteLength + args.Length];
        w.AsSpan().CopyTo(combined);
        args.CopyTo(combined.AsSpan(w.ByteLength));
        return combined;
    }

    public static byte[] Input(int netId, ReadOnlySpan<NetworkInput> batch) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Input);
        w.WriteInt(netId);
        w.WriteByte((byte)Math.Min(batch.Length, 255));
        for (int i = 0; i < batch.Length && i < 255; i++)
            batch[i].Write(w);
        return w.AsSpan().ToArray();
    }

    public static byte[] Ack(uint snapshotSeq) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Ack);
        w.WriteUInt(snapshotSeq);
        return w.AsSpan().ToArray();
    }

    public static byte[] SceneAck(uint sceneSeq) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.SceneAck);
        w.WriteUInt(sceneSeq);
        return w.AsSpan().ToArray();
    }

    public static byte[] Possess(int controllerNetId, int pawnNetId) {
        var w = new BitWriter();
        w.WriteByte((byte)NetMessage.Possess);
        w.WriteInt(controllerNetId);
        w.WriteInt(pawnNetId);
        return w.AsSpan().ToArray();
    }

    public static byte ReadTag(ReadOnlySpan<byte> payload) =>
        payload.Length > 0 ? payload[0] : (byte)0;
}
