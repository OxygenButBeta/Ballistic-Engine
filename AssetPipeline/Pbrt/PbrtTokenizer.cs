using System.Globalization;
using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline;

enum TokenKind { Identifier, QuotedString, Number, LBracket, RBracket }

readonly struct Token {
    public readonly TokenKind Kind;
    public readonly string Text;
    public Token(TokenKind kind, string text) { Kind = kind; Text = text; }
}

// pbrt's lexical grammar: free-form, whitespace-insensitive, '#'-to-EOL comments, double-quoted
// strings (which may contain spaces and '#', e.g. the texture name "Map #594"), numbers, and the
// array brackets '[' ']'. Bare identifiers are directive keywords. The tokenizer is a forward cursor;
// the parser peeks/consumes as each directive's grammar requires.
sealed class Tokenizer {
    readonly string src;
    int pos;

    public Tokenizer(string source) { src = source; pos = 0; }

    public bool TryNext(out Token token) {
        SkipTrivia();
        if (pos >= src.Length) { token = default; return false; }

        char c = src[pos];
        if (c == '[') { pos++; token = new Token(TokenKind.LBracket, "["); return true; }
        if (c == ']') { pos++; token = new Token(TokenKind.RBracket, "]"); return true; }
        if (c == '"') { token = ReadQuoted(); return true; }
        if (c == '-' || c == '+' || c == '.' || char.IsDigit(c)) { token = ReadNumber(); return true; }
        token = ReadIdentifier();
        return true;
    }

    // Look at the next token without consuming it.
    bool TryPeek(out Token token) {
        int save = pos;
        bool ok = TryNext(out token);
        pos = save;
        return ok;
    }

    void SkipTrivia() {
        while (pos < src.Length) {
            char c = src[pos];
            if (c == '#') { while (pos < src.Length && src[pos] != '\n') pos++; }
            else if (char.IsWhiteSpace(c)) pos++;
            else break;
        }
    }

    Token ReadQuoted() {
        pos++; // opening quote
        int start = pos;
        while (pos < src.Length && src[pos] != '"') pos++;
        string text = src.Substring(start, pos - start);
        if (pos < src.Length) pos++; // closing quote
        return new Token(TokenKind.QuotedString, text);
    }

    Token ReadNumber() {
        int start = pos;
        if (src[pos] == '-' || src[pos] == '+') pos++;
        while (pos < src.Length) {
            char c = src[pos];
            if (char.IsDigit(c) || c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') pos++;
            else break;
        }
        return new Token(TokenKind.Number, src.Substring(start, pos - start));
    }

    Token ReadIdentifier() {
        int start = pos;
        while (pos < src.Length && !char.IsWhiteSpace(src[pos]) &&
               src[pos] != '"' && src[pos] != '[' && src[pos] != ']' && src[pos] != '#') pos++;
        // Defensive: an unrecognized single char shouldn't loop forever.
        if (pos == start) pos++;
        return new Token(TokenKind.Identifier, src.Substring(start, pos - start));
    }

    // ---- typed consumption used by the directive parsers ----

    public string ReadQuotedString() {
        if (TryNext(out var t) && t.Kind == TokenKind.QuotedString) return t.Text;
        return "";
    }

    // A directive's implementation name is a quoted string immediately following it (e.g.
    // Camera "perspective"). Only consumed if the next token actually is a quoted string; some
    // directives omit it. Returns false (and consumes nothing) if the next token isn't a string.
    public bool TryReadImplName(out string name) {
        if (TryPeek(out var t) && t.Kind == TokenKind.QuotedString) {
            TryNext(out _);
            name = t.Text;
            return true;
        }
        name = null;
        return false;
    }

    // Reads exactly n numbers, optionally wrapped in a single pair of brackets.
    public float[] ReadFloats(int n) {
        var result = new float[n];
        bool bracketed = ConsumeOptionalLBracket();
        for (int i = 0; i < n; i++) result[i] = NextFloat();
        if (bracketed) ConsumeRBracket();
        return result;
    }

    // Reads a bracketed array of exactly n numbers (Transform/ConcatTransform).
    public float[] ReadFloatArray(int n) => ReadFloats(n);

    float NextFloat() {
        if (TryNext(out var t) &&
            float.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            return f;
        return 0f;
    }

    bool ConsumeOptionalLBracket() {
        if (TryPeek(out var t) && t.Kind == TokenKind.LBracket) { TryNext(out _); return true; }
        return false;
    }

    void ConsumeRBracket() {
        if (TryPeek(out var t) && t.Kind == TokenKind.RBracket) TryNext(out _);
    }

    // Parses a directive's full parameter list: a run of `"type name" value-or-[array]` entries.
    // Stops at the next directive keyword (bare identifier) or EOF, leaving it unconsumed.
    public ParamSet ReadParams() {
        var set = new ParamSet();
        while (TryPeek(out var t)) {
            if (t.Kind != TokenKind.QuotedString) break; // next directive keyword or array end
            // A declarator is "type name"; a lone quoted string here that ISN'T a declarator would be
            // a grammar error, but real scenes always pair them. Consume the declarator.
            TryNext(out var decl);
            var (type, name) = SplitDeclarator(decl.Text);
            if (name == null) break; // not a declarator (single-word string) — leave it
            var values = ReadValueList();
            set.Add(type, name, values);
        }
        return set;
    }

    static (string type, string name) SplitDeclarator(string s) {
        int sp = s.IndexOf(' ');
        if (sp < 0) return (null, null);
        return (s[..sp], s[(sp + 1)..].Trim());
    }

    // Reads the value(s) after a declarator: a bracketed list, or a single bare scalar/string/bool.
    List<Token> ReadValueList() {
        var values = new List<Token>();
        if (TryPeek(out var t) && t.Kind == TokenKind.LBracket) {
            TryNext(out _); // [
            while (TryPeek(out var v) && v.Kind != TokenKind.RBracket) { TryNext(out var got); values.Add(got); }
            ConsumeRBracket();
        }
        else if (TryNext(out var single)) {
            values.Add(single);
        }
        return values;
    }

    // Skips an optional impl name then a full parameter list — used to discard directives we don't
    // model (Sampler, Integrator, ...) without mis-parsing their values as directives.
    public void SkipImplAndParams() {
        TryReadImplName(out _);
        ReadParams();
    }
}

// One parameter's parsed values, retaining the declared type so the accessor knows how to read it.
sealed class ParamSet {
    readonly Dictionary<string, (string type, List<Token> values)> entries = new(StringComparer.Ordinal);

    public void Add(string type, string name, List<Token> values) => entries[name] = (Normalize(type), values);

    // v3 aliases -> v4 strict names so the rest of the parser only sees one vocabulary.
    static string Normalize(string type) => type switch {
        "point" => "point3",
        "vector" => "vector3",
        "normal" => "normal3",
        "color" => "rgb",
        _ => type,
    };

    public bool TryFloat(string name, out float value) {
        value = 0f;
        if (!entries.TryGetValue(name, out var e) || e.values.Count == 0) return false;
        return float.TryParse(e.values[0].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public bool TryInt(string name, out int value) {
        value = 0;
        if (!entries.TryGetValue(name, out var e) || e.values.Count == 0) return false;
        if (int.TryParse(e.values[0].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        // Some files write integers with a decimal point.
        if (float.TryParse(e.values[0].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) {
            value = (int)f; return true;
        }
        return false;
    }

    // An rgb/spectrum value given as three numbers. A single number is treated as gray. A spectrum
    // given as wavelength/value pairs or a named-spectrum string returns false (handled as a fallback).
    public bool TryRgb(string name, out Vector3 value) {
        value = Vector3.One;
        if (!entries.TryGetValue(name, out var e)) return false;
        var nums = Numbers(e.values);
        if (nums.Count == 3) { value = new Vector3(nums[0], nums[1], nums[2]); return true; }
        if (nums.Count == 1) { value = new Vector3(nums[0]); return true; }
        return false;
    }

    public bool TryPoint(string name, out Vector3 value) => TryRgb(name, out value);

    // A parameter bound to a named texture: declared type "texture", value is the texture name string.
    public bool GetTextureRef(string name, out string textureName) {
        textureName = null;
        if (!entries.TryGetValue(name, out var e)) return false;
        if (e.type == "texture" && e.values.Count > 0 && e.values[0].Kind == TokenKind.QuotedString) {
            textureName = e.values[0].Text;
            return true;
        }
        return false;
    }

    public string GetString(string name) {
        if (entries.TryGetValue(name, out var e) && e.values.Count > 0 && e.values[0].Kind == TokenKind.QuotedString)
            return e.values[0].Text;
        return null;
    }

    public List<float> GetFloatList(string name) {
        if (!entries.TryGetValue(name, out var e)) return null;
        return Numbers(e.values);
    }

    public List<int> GetIntList(string name) {
        if (!entries.TryGetValue(name, out var e)) return null;
        var list = new List<int>(e.values.Count);
        foreach (var tk in e.values)
            if (int.TryParse(tk.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) list.Add(i);
            else if (float.TryParse(tk.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) list.Add((int)f);
        return list;
    }

    static List<float> Numbers(List<Token> values) {
        var list = new List<float>(values.Count);
        foreach (var tk in values)
            if (tk.Kind == TokenKind.Number &&
                float.TryParse(tk.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                list.Add(f);
        return list;
    }
}
