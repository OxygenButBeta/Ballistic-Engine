using System.Globalization;

namespace BallisticEngine.UI;

public readonly struct Color : IEquatable<Color>
{
    public readonly float R, G, B, A;

    public Color(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }

    public static Color Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);
    public static Color Rgba(byte r, byte g, byte b, float a) => new(r / 255f, g / 255f, b / 255f, a);

    public static readonly Color Transparent = new(0, 0, 0, 0);
    public static readonly Color White = new(1, 1, 1, 1);
    public static readonly Color Black = new(0, 0, 0, 1);

    public static Color FromHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Transparent;
        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#') s = s[1..];

        switch (s.Length)
        {
            case 3:
                return Rgb(Dup(s[0]), Dup(s[1]), Dup(s[2]));
            case 4:
                return Rgba(Dup(s[0]), Dup(s[1]), Dup(s[2]), Dup(s[3]) / 255f);
            case 6:
                return Rgb(Hex2(s[0], s[1]), Hex2(s[2], s[3]), Hex2(s[4], s[5]));
            case 8:
                return Rgba(Hex2(s[0], s[1]), Hex2(s[2], s[3]), Hex2(s[4], s[5]), Hex2(s[6], s[7]) / 255f);
            default:
                return Transparent;
        }
    }

    static byte Dup(char c) { int v = Nibble(c); return (byte)((v << 4) | v); }
    static byte Hex2(char hi, char lo) => (byte)((Nibble(hi) << 4) | Nibble(lo));
    static int Nibble(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0;

    public Vector4 ToVector4() => new(R, G, B, A);
    public static Color FromVector4(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

    public Color WithAlpha(float a) => new(R, G, B, a);

    public bool Equals(Color o) => R == o.R && G == o.G && B == o.B && A == o.A;
    public override bool Equals(object o) => o is Color c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "rgba({0:0.##}, {1:0.##}, {2:0.##}, {3:0.##})", R, G, B, A);
}
