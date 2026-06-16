using System.Numerics;

namespace BallisticEngine.Networking;

// The per-FIELD-TYPE packing the source generator calls (plan §11). The generator NEVER inlines
// float/Vector packing — it emits `WireCodec.Write(w, value)` / `value = WireCodec.ReadFloat(ref r)` so
// the packing lives in ONE BCL-only place and the generated body is a thin changemask + field loop.
// PROVEN byte-for-byte in the isolated harness (%TEMP%\bal-netserde-test) against copies of BitWriter.
//
// Supported [Networked] field types (P2 — §14 item 13 defers collections; P2 = scalars + math + netId):
//   bool, byte, int, uint, float, Vector2, Vector3, Quaternion, and an int netId (NetworkRef<T> on wire).
// Math types are System.Numerics (the gameplay layer's choice — survives the DX12 OpenTK removal). A
// [Networked] field of an OpenTK vector decomposes to floats in the generated code, not here.
public static class WireCodec {
    // ---- full-precision (the default, lossless) ---------------------------------------------------
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

    // ---- opt-in quantized float (the ~mm packing, §11 — emitted for [Networked(Min,Max,Bits)]) ------
    public static void WriteQ(BitWriter w, float v, float min, float max, int bits) =>
        w.WriteQuantized(v, min, max, bits);
    public static float ReadQ(ref BitReader r, float min, float max, int bits) =>
        r.ReadQuantized(min, max, bits);

    // ---- FNV-1a 32-bit — the stable hash the generator uses for typeId/methodId/layout-hash ---------
    // Same algorithm at codegen time and runtime so a compile-time typeId matches a runtime one. The
    // delimiter makes token lists unambiguous (["ab","c"] != ["a","bc"]).
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
