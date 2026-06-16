namespace BallisticEngine.Networking;

// The read half of BitWriter — same bit order, same quantization. Reads a payload the writer packed.
public ref struct BitReader {
    readonly ReadOnlySpan<byte> buffer;
    int bitPos;

    public BitReader(ReadOnlySpan<byte> payload) {
        buffer = payload;
        bitPos = 0;
    }

    public int BitPos => bitPos;
    public int BitLength => buffer.Length * 8;
    public bool AtEnd => bitPos >= BitLength;

    public uint ReadBits(int count) {
        if (count is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be 1..32");
        if (bitPos + count > BitLength)
            throw new InvalidOperationException("BitReader: read past end of payload");
        uint value = 0;
        for (int i = 0; i < count; i++) {
            int bit = bitPos + i;
            if ((buffer[bit >> 3] & (1 << (bit & 7))) != 0)
                value |= 1u << i;
        }
        bitPos += count;
        return value;
    }

    public bool ReadBool() => ReadBits(1) != 0;
    public byte ReadByte() => (byte)ReadBits(8);
    public int ReadInt() => unchecked((int)ReadBits(32));
    public uint ReadUInt() => ReadBits(32);
    public float ReadFloat() => BitConverter.UInt32BitsToSingle(ReadBits(32));

    public float ReadQuantized(float min, float max, int bits) {
        uint q = ReadBits(bits);
        float t = ((1u << bits) - 1) > 0 ? q / (float)((1u << bits) - 1) : 0f;
        return min + t * (max - min);
    }
}
