namespace BallisticEngine.Networking;

// Bit-level packer for the wire format (plan §11). The source generator (P2) emits SerializeState
// over [Networked] fields against this; P0 only needs the primitive so the layer compiles and the
// loopback path can round-trip a payload. Grows its backing buffer; little-endian byte flush.
//
// Why bits not bytes: "unchanged object ≈ 1 bit" (§11 delta) and quantized floats (~mm) need
// sub-byte granularity. This is the BCL-only half; quantization helpers live alongside.
public sealed class BitWriter {
    byte[] buffer;
    int bitPos;   // total bits written

    public BitWriter(int initialBytes = 64) => buffer = new byte[Math.Max(1, initialBytes)];

    public int BitLength => bitPos;
    public int ByteLength => (bitPos + 7) >> 3;

    public void Reset() {
        bitPos = 0;
        Array.Clear(buffer);
    }

    // Write the low `count` bits of `value` (count 1..32).
    public void WriteBits(uint value, int count) {
        if (count is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be 1..32");
        EnsureCapacity(bitPos + count);
        for (int i = 0; i < count; i++) {
            if ((value & (1u << i)) != 0)
                buffer[(bitPos + i) >> 3] |= (byte)(1 << ((bitPos + i) & 7));
        }
        bitPos += count;
    }

    public void WriteBool(bool value) => WriteBits(value ? 1u : 0u, 1);
    public void WriteByte(byte value) => WriteBits(value, 8);
    public void WriteInt(int value) => WriteBits(unchecked((uint)value), 32);
    public void WriteUInt(uint value) => WriteBits(value, 32);

    public void WriteFloat(float value) =>
        WriteBits(BitConverter.SingleToUInt32Bits(value), 32);

    // Quantize a float in [min,max] to `bits` (the ~mm packing of §11). Out-of-range clamps.
    public void WriteQuantized(float value, float min, float max, int bits) {
        float t = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        uint q = (uint)Math.Round(t * ((1u << bits) - 1));
        WriteBits(q, bits);
    }

    // The packed payload as a span (length = ByteLength). Valid until the next write/Reset.
    public ReadOnlySpan<byte> AsSpan() => buffer.AsSpan(0, ByteLength);

    void EnsureCapacity(int bitsNeeded) {
        int bytesNeeded = (bitsNeeded + 7) >> 3;
        if (bytesNeeded <= buffer.Length)
            return;
        int next = buffer.Length * 2;
        while (next < bytesNeeded) next *= 2;
        Array.Resize(ref buffer, next);
    }
}
