using System.Globalization;
using System.Reflection;
using BallisticEngine.Serialization;

namespace BallisticEngine.Cli;

internal static class SceneFile {
    public static SceneDocument Load(string path) {
        if (!File.Exists(path))
            throw new Exception($"scene file not found: '{path}'");
        SceneDocument? doc;
        try { doc = SceneYaml.Deserializer.Deserialize<SceneDocument>(File.ReadAllText(path)); }
        catch (Exception ex) { throw new Exception($"{path}: malformed YAML — {ex.Message}"); }
        if (doc is null)
            throw new Exception($"{path}: empty or unreadable scene");
        doc.SceneComponents ??= new();
        doc.Entities ??= new();
        return doc;
    }

    public static void Save(string path, SceneDocument doc) {
        NormalizeDocument(doc);
        File.WriteAllText(path, SceneYaml.Serializer.Serialize(doc));
    }

    public static void NormalizeDocument(SceneDocument doc) {
        foreach (ComponentDocument c in doc.SceneComponents ?? [])
            NormalizeMembers(c, c.Type is null ? null : ComponentRegistry.ResolveScene(c.Type));
        foreach (EntityDocument e in doc.Entities ?? [])
            foreach (ComponentDocument c in e.Components ?? [])
                NormalizeMembers(c, c.Type is null ? null : ComponentRegistry.Resolve(c.Type));
    }

    static void NormalizeMembers(ComponentDocument c, Type? type) {
        if (c.Members is null || c.Members.Count == 0) return;
        Dictionary<string, MemberInfo>? byName = null;
        if (type is not null) {
            byName = new(StringComparer.OrdinalIgnoreCase);
            foreach (MemberInfo m in ComponentReflection.SerializableMembers(type))
                byName[m.Name] = m;
        }
        foreach (string key in c.Members.Keys.ToList()) {
            Type? memberType = byName is not null && byName.TryGetValue(key, out MemberInfo? m)
                ? ComponentReflection.MemberType(m) : null;
            c.Members[key] = NormalizeValue(c.Members[key], memberType);
        }
    }

    static object? NormalizeValue(object? raw, Type? memberType) {
        if (raw is null) return null;
        if (memberType is not null) {
            Type t = Nullable.GetUnderlyingType(memberType) ?? memberType;
            if (t == typeof(string) || t.IsEnum || typeof(BObject).IsAssignableFrom(t) ||
                t == typeof(AnimationCurve) || t == typeof(ColorGradient))
                return raw;
            if (raw is string s) {
                if (t == typeof(bool) && bool.TryParse(s, out bool b)) return b;
                if (IsIntegerType(t) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                if (IsFloatType(t) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            }
            if (raw is Dictionary<object, object> map) {
                if (t == typeof(Vector2) && TryVec(map, 2, out float[] v2)) return new Vector2(v2[0], v2[1]);
                if (t == typeof(Vector3) && TryVec(map, 3, out float[] v3)) return new Vector3(v3[0], v3[1], v3[2]);
                if (t == typeof(Quaternion) && TryVec(map, 4, out float[] q)) return new Quaternion(q[0], q[1], q[2], q[3]);
            }
        }
        return NormalizeLoose(raw);
    }

    static object NormalizeLoose(object raw) {
        switch (raw) {
            case string s:
                if (s is "true" or "false") return s == "true";
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                return s;
            case Dictionary<object, object> map: {
                if (TryVec(map, 2, out float[] v2)) return new Vector2(v2[0], v2[1]);
                if (TryVec(map, 3, out float[] v3)) return new Vector3(v3[0], v3[1], v3[2]);
                if (TryVec(map, 4, out float[] q)) return new Quaternion(q[0], q[1], q[2], q[3]);
                var dict = new Dictionary<string, object?>();
                foreach (var kv in map) dict[kv.Key?.ToString() ?? ""] = kv.Value is null ? null : NormalizeLoose(kv.Value);
                return dict;
            }
            case List<object> list:
                return list.Select(x => x is null ? null : NormalizeLoose(x)).ToList();
            default:
                return raw;
        }
    }

    static bool IsIntegerType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
        t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

    static bool IsFloatType(Type t) => t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    static bool TryVec(Dictionary<object, object> map, int count, out float[] values) {
        values = new float[count];
        if (map.Count != count) return false;
        string[] keys = ["x", "y", "z", "w"];
        for (int i = 0; i < count; i++) {
            object? raw = null;
            foreach (var kv in map)
                if (string.Equals(kv.Key?.ToString(), keys[i], StringComparison.OrdinalIgnoreCase)) { raw = kv.Value; break; }
            if (raw is null || !float.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }
        return true;
    }

    public static void BuildRegistry(string scenePath) => BuildRegistryForRoot(FindProjectRoot(scenePath));

    public static void BuildRegistryForRoot(string? root) {
        var assemblies = new List<Assembly> { typeof(SceneManager).Assembly };
        string? dll = root is null ? null : Path.Combine(root, "Library", "ScriptAssemblies", "GameScripts.dll");
        if (dll is not null && File.Exists(dll)) {
            try { assemblies.Add(Assembly.Load(File.ReadAllBytes(dll))); }
            catch (Exception ex) { Console.Error.WriteLine($"bal: game scripts not loaded ({ex.Message}); engine components only"); }
        }
        ComponentRegistry.Build(assemblies.ToArray());
    }

    public static string? FindProjectRoot(string path) {
        DirectoryInfo? dir = new FileInfo(Path.GetFullPath(path)).Directory;
        for (int i = 0; dir is not null && i < 16; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "project.json")))
                return dir.FullName;
        return null;
    }

    public static string ResolveProjectRoot(string pathArg) =>
        Directory.Exists(pathArg) && File.Exists(Path.Combine(pathArg, "project.json"))
            ? Path.GetFullPath(pathArg)
            : FindProjectRoot(pathArg)
              ?? throw new Exception($"no project.json found at or above '{pathArg}'");

    public static EntityDocument ResolveEntity(SceneDocument doc, string query) {
        List<EntityDocument> entities = doc.Entities ?? [];

        foreach (EntityDocument e in entities)
            if (string.Equals(e.Id, query, StringComparison.OrdinalIgnoreCase))
                return e;

        if (query.Length >= 6) {
            var byPrefix = entities.Where(e => e.Id?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true).ToList();
            if (byPrefix.Count == 1) return byPrefix[0];
            if (byPrefix.Count > 1)
                throw new Exception($"entity id prefix '{query}' is ambiguous ({byPrefix.Count} matches)");
        }

        var byName = entities.Where(e => string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
            throw new Exception($"entity name '{query}' is ambiguous — {byName.Count} entities share it; " +
                                $"use an id: {string.Join(", ", byName.Select(e => e.Id))}");

        var bySubstring = entities.Where(e => e.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (bySubstring.Count == 1) return bySubstring[0];

        string? hint = Suggest.Closest(query, entities.Select(e => e.Name));
        throw new Exception($"no entity matches '{query}'"
            + (bySubstring.Count > 1 ? $" uniquely ({bySubstring.Count} name matches)" : "")
            + (hint is null ? "" : $" — did you mean '{hint}'?"));
    }

    public static object? ParseMemberValue(Type memberType, string input, string scenePath) {
        Type t = Nullable.GetUnderlyingType(memberType) ?? memberType;

        if (t == typeof(string)) return input;
        if (t == typeof(bool)) return ParseBool(input);
        if (IsIntegerType(t))
            return long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
                ? l : throw new Exception($"'{input}' is not an integer");
        if (IsFloatType(t))
            return double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d : throw new Exception($"'{input}' is not a number");
        if (t.IsEnum) {
            foreach (string name in Enum.GetNames(t))
                if (string.Equals(name, input, StringComparison.OrdinalIgnoreCase))
                    return name;
            throw new Exception($"'{input}' is not a {t.Name} — valid values: {string.Join(", ", Enum.GetNames(t))}");
        }
        if (t == typeof(Vector2)) return ParseVec2(input);
        if (t == typeof(Vector3)) return ParseVec3(input);
        if (t == typeof(Quaternion)) return EulerDegreesToQuaternion(ParseVec3(input));
        if (typeof(BObject).IsAssignableFrom(t)) return ParseAssetRef(input, scenePath);
        if (t == typeof(AnimationCurve)) {
            try { AnimationCurve.Parse(input); } catch (Exception ex) { throw new Exception($"invalid AnimationCurve string: {ex.Message}"); }
            return input;
        }
        if (t == typeof(ColorGradient)) {
            try { ColorGradient.Parse(input); } catch (Exception ex) { throw new Exception($"invalid ColorGradient string: {ex.Message}"); }
            return input;
        }
        throw new Exception($"members of type {t.Name} can't be set from the CLI yet (use the editor or an editor script)");
    }

    public static object ParseLoose(string input) {
        if (bool.TryParse(input, out bool b)) return b;
        if (long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
        if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        return input;
    }

    public static bool ParseBool(string input) => input.ToLowerInvariant() switch {
        "true" or "1" or "on" or "yes" => true,
        "false" or "0" or "off" or "no" => false,
        _ => throw new Exception($"'{input}' is not a bool (true/false)"),
    };

    public static Vector2 ParseVec2(string input) {
        float[] c = ParseComponents(input, 2);
        return new Vector2(c[0], c[1]);
    }

    public static Vector3 ParseVec3(string input) {
        float[] c = ParseComponents(input, 3);
        return new Vector3(c[0], c[1], c[2]);
    }

    static float[] ParseComponents(string input, int count) {
        string[] parts = input.Trim().Trim('(', ')').Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != count)
            throw new Exception($"'{input}' is not a {count}-component vector — expected '{string.Join(",", "xyzw".Take(count))}'");
        var values = new float[count];
        for (int i = 0; i < count; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                throw new Exception($"'{parts[i]}' in '{input}' is not a number");
        return values;
    }

    public static Quaternion EulerDegreesToQuaternion(Vector3 degrees) =>
        BQuaternion.FromEulerAngles(
            MathHelper.DegreesToRadians(degrees.X),
            MathHelper.DegreesToRadians(degrees.Y),
            MathHelper.DegreesToRadians(degrees.Z));

    public static Vector3 QuaternionToEulerDegrees(Quaternion q) {
        Vector3 radians = q.ToEulerAngles();
        return new Vector3(
            MathHelper.RadiansToDegrees(radians.X),
            MathHelper.RadiansToDegrees(radians.Y),
            MathHelper.RadiansToDegrees(radians.Z));
    }

    static string? ParseAssetRef(string input, string scenePath) {
        if (input is "none" or "null") return null;
        if (input.StartsWith("guid:", StringComparison.OrdinalIgnoreCase)) {
            string hex = input[5..];
            if (hex.Length != 32 || !hex.All(Uri.IsHexDigit))
                throw new Exception($"'{input}' is not a valid guid ref (guid:<32 hex chars>)");
            return input;
        }
        string norm = input.Replace('\\', '/');
        if (!norm.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"asset refs are 'Assets/...' paths or 'guid:<32hex>' (got '{input}')");
        string? root = FindProjectRoot(scenePath);
        if (root is not null && !File.Exists(Path.Combine(root, norm)))
            throw new Exception($"asset file not found: '{norm}' (under '{root}')");
        return norm;
    }

    public static string CamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public static object? ToJsonValue(object? value) => value switch {
        null => null,
        Vector2 v => new { x = v.X, y = v.Y },
        Vector3 v => new { x = v.X, y = v.Y, z = v.Z },
        Quaternion q => new { x = q.X, y = q.Y, z = q.Z, w = q.W },
        Dictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => ToJsonValue(kv.Value)),
        Dictionary<object, object> d => d.ToDictionary(kv => kv.Key?.ToString() ?? "", kv => ToJsonValue(kv.Value)),
        List<object?> l => l.Select(ToJsonValue).ToList(),
        _ => value,
    };
}
