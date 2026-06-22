namespace BallisticEngine.Networking;

public sealed class BitWriter {
    byte[] buffer;
    int bitPos;

    public BitWriter(int initialBytes = 64) => buffer = new byte[Math.Max(1, initialBytes)];

    public int BitLength => bitPos;
    public int ByteLength => (bitPos + 7) >> 3;

    public void Reset() {
        bitPos = 0;
        Array.Clear(buffer);
    }

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

    public void WriteQuantized(float value, float min, float max, int bits) {
        float t = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        uint levels = (uint)((1UL << bits) - 1UL);
        uint q = (uint)Math.Round(t * levels);
        WriteBits(q, bits);
    }

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
