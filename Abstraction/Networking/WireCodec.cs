namespace BallisticEngine.Networking;

public static class WireCodec {
    public static void Write(BitWriter w, bool v)  => w.WriteBool(v);
    public static void Write(BitWriter w, byte v)  => w.WriteByte(v);
    public static void Write(BitWriter w, int v)   => w.WriteInt(v);
    public static void Write(BitWriter w, uint v)  => w.WriteUInt(v);
    public static void Write(BitWriter w, float v) => w.WriteFloat(v);
    public static void Write(BitWriter w, Vector2 v) { w.WriteFloat(v.X); w.WriteFloat(v.Y); }
    public static void Write(BitWriter w, Vector3 v) { w.WriteFloat(v.X); w.WriteFloat(v.Y); w.WriteFloat(v.Z); }
    public static void Write(BitWriter w, Quaternion v) {
        w.WriteFloat(v.X); w.WriteFloat(v.Y); w.WriteFloat(v.Z); w.WriteFloat(v.W);
    }

    public static bool  ReadBool(ref BitReader r) => r.ReadBool();
    public static byte  ReadByte(ref BitReader r) => r.ReadByte();
    public static int   ReadInt(ref BitReader r)  => r.ReadInt();
    public static uint  ReadUInt(ref BitReader r) => r.ReadUInt();
    public static float ReadFloat(ref BitReader r) => r.ReadFloat();
    public static Vector2 ReadVector2(ref BitReader r) => new(r.ReadFloat(), r.ReadFloat());
    public static Vector3 ReadVector3(ref BitReader r) => new(r.ReadFloat(), r.ReadFloat(), r.ReadFloat());
    public static Quaternion ReadQuaternion(ref BitReader r) =>
        new(r.ReadFloat(), r.ReadFloat(), r.ReadFloat(), r.ReadFloat());

    public static void WriteQ(BitWriter w, float v, float min, float max, int bits) =>
        w.WriteQuantized(v, min, max, bits);
    public static float ReadQ(ref BitReader r, float min, float max, int bits) =>
        r.ReadQuantized(min, max, bits);

    public static int Fnv(params string[] tokens) {
        unchecked {
            uint h = 2166136261;
            foreach (string t in tokens) {
                foreach (char c in t) { h ^= c; h *= 16777619; }
                h ^= (byte)'|'; h *= 16777619;
            }
            return (int)h;
        }
    }

    public static int FnvString(string s) {
        unchecked {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }
}
