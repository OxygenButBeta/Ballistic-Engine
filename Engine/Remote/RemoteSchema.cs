using System.Text.Json;

namespace BallisticEngine;

public static class RemoteSchema {
    public enum Kind { String, Number, Bool, Any }

    public readonly record struct Param(string Name, Kind Kind, bool Required);

    public sealed record MethodSchema(string Method, Param[] Params) {
        public IEnumerable<Param> Required => Params.Where(p => p.Required);
        public IEnumerable<Param> Optional => Params.Where(p => !p.Required);
    }

    static Param Req(string name, Kind kind = Kind.String) => new(name, kind, true);
    static Param Opt(string name, Kind kind = Kind.String) => new(name, kind, false);

    public static readonly IReadOnlyList<MethodSchema> Methods = new[] {
        new MethodSchema("editor.status",       Array.Empty<Param>()),
        new MethodSchema("scene.describe",       Array.Empty<Param>()),
        new MethodSchema("scene.save",           Array.Empty<Param>()),
        new MethodSchema("scene.open",           new[] { Req("path") }),
        new MethodSchema("entity.get",           new[] { Req("entity") }),
        new MethodSchema("entity.create",        new[] { Req("name"), Opt("position"), Opt("parent") }),
        new MethodSchema("entity.delete",        new[] { Req("entity") }),
        new MethodSchema("component.add",        new[] { Req("entity"), Req("type") }),
        new MethodSchema("component.remove",     new[] { Req("entity"), Req("type") }),
        new MethodSchema("component.set",        new[] { Req("entity"), Req("target"), Req("value", Kind.Any) }),
        new MethodSchema("select",               new[] { Req("entity") }),
        new MethodSchema("play.start",           Array.Empty<Param>()),
        new MethodSchema("play.stop",            Array.Empty<Param>()),
        new MethodSchema("play.pause",           new[] { Opt("paused", Kind.Bool) }),
        new MethodSchema("play.step",            Array.Empty<Param>()),
        new MethodSchema("undo",                 Array.Empty<Param>()),
        new MethodSchema("redo",                 Array.Empty<Param>()),
        new MethodSchema("screenshot",           new[] { Req("path"), Opt("settleFrames", Kind.Number) }),
        new MethodSchema("console.tail",         new[] { Opt("count", Kind.Number) }),
        new MethodSchema("scripts.rebuild",      Array.Empty<Param>()),
        new MethodSchema("unity.import",         new[] { Req("path"), Opt("subfolder") }),
        new MethodSchema("editor.frame",         new[] { Opt("entity"), Opt("dir"), Opt("fit", Kind.Number) }),
        new MethodSchema("editor.refresh",       Array.Empty<Param>()),
        new MethodSchema("editor.reimport",      Array.Empty<Param>()),
        new MethodSchema("scene.component.add",  new[] { Req("type") }),
        new MethodSchema("scene.component.set",  new[] { Req("type"), Req("member"), Req("value", Kind.Any) }),
        new MethodSchema("help",                 Array.Empty<Param>()),
    };

    static readonly Dictionary<string, MethodSchema> byName =
        Methods.ToDictionary(m => m.Method, StringComparer.Ordinal);

    public static bool IsKnownMethod(string method) => method is not null && byName.ContainsKey(method);

    public static MethodSchema For(string method) =>
        method is not null && byName.TryGetValue(method, out MethodSchema s) ? s : null;

    public static string Signature(MethodSchema schema) {
        if (schema is null || schema.Params.Length == 0)
            return schema?.Method ?? "";
        IEnumerable<string> parts = schema.Params
            .Where(p => p.Required).Select(p => p.Name)
            .Concat(schema.Params.Where(p => !p.Required).Select(p => p.Name + "?"));
        return $"{schema.Method} {{{string.Join(", ", parts)}}}";
    }

    public static string Signature(string method) => Signature(For(method));

    public static string[] Signatures => Methods.Select(Signature).ToArray();

    public readonly record struct CatalogParam(string Name, string Kind, bool Required);
    public readonly record struct CatalogEntry(string Method, string Signature, CatalogParam[] Params);

    public static CatalogEntry[] Catalog() => Methods.Select(m => new CatalogEntry(
        m.Method,
        Signature(m),
        m.Required.Concat(m.Optional)
            .Select(p => new CatalogParam(p.Name, p.Kind.ToString(), p.Required))
            .ToArray())).ToArray();

    public static string CoverageError(IEnumerable<string> registered) {
        var have = new HashSet<string>(registered ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var want = new HashSet<string>(Methods.Select(m => m.Method), StringComparer.Ordinal);
        var missing = want.Except(have).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var extra = have.Except(want).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (missing.Count == 0 && extra.Count == 0)
            return null;
        return "remote dispatch table drifted from RemoteSchema -- "
            + $"schema methods with NO handler: [{string.Join(", ", missing)}]; "
            + $"handlers with NO schema row: [{string.Join(", ", extra)}]";
    }

    public static string Validate(string method, JsonElement p) {
        if (string.IsNullOrEmpty(method))
            return "missing 'method'";
        if (!byName.TryGetValue(method, out MethodSchema schema))
            return $"unknown method '{method}' -- see 'help' for the method list";

        bool hasParams = p.ValueKind == JsonValueKind.Object;
        foreach (Param param in schema.Required) {
            if (!hasParams || !p.TryGetProperty(param.Name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
                return $"{method}: missing required param '{param.Name}' ({Describe(param.Kind)})";
            string kindError = CheckKind(param, v);
            if (kindError is not null)
                return $"{method}: {kindError}";
        }
        return null;
    }

    static string CheckKind(Param param, JsonElement v) => param.Kind switch {
        Kind.String => v.ValueKind == JsonValueKind.String
            ? null
            : $"param '{param.Name}' must be a string (got {v.ValueKind})",
        Kind.Bool => v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? null
            : $"param '{param.Name}' must be a boolean (got {v.ValueKind})",
        Kind.Number => v.ValueKind == JsonValueKind.Number
            || (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            ? null
            : $"param '{param.Name}' must be a number (got {v.ValueKind})",
        _ => null,
    };

    static string Describe(Kind kind) => kind switch {
        Kind.String => "string",
        Kind.Number => "number",
        Kind.Bool => "boolean",
        _ => "value",
    };
}
