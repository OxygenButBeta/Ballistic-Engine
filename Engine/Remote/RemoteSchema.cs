using System.Text.Json;

namespace BallisticEngine;

// D2 (editor-rework Phase D, "AI-operability hardening"): the DECLARATIVE schema for the editor's
// command-port method surface, living in the ENGINE LIBRARY so it is the ONE source of truth shared by
// the editor's RemoteHandlers.Dispatch AND the MCP boundary -- and reachable by the headless harness
// (which references the engine library only, like the bal CLI, NOT the editor exe).
//
// THE PROBLEM (plan 3.5): remote/MCP params arrive as raw JSON; a malformed request (missing a required
// field, or a required field of the wrong JSON kind -- e.g. "entity": 5 where a string is expected) used
// to reach the handler and throw a cryptic InvalidOperationException deep inside JsonElement.GetString(),
// or NRE the editor. Each handler hand-rolled its own RequireString check, so coverage drifted and the
// error messages were inconsistent. This table makes the per-method param contract data, and Validate()
// rejects a malformed request with ONE clean, uniform error string BEFORE any handler runs.
//
// SCOPE: validates method existence + the REQUIRED params (presence + JSON kind). It does NOT semantically
// validate values (an unknown entity name / component type is still the handler's job -- those need the
// live scene). It is a boundary guard against shape errors, not a business-rule checker. Optional params
// are listed for documentation/help but are not required to be present; when present they are NOT
// kind-checked here (the handlers tolerate missing/typed optionals already, e.g. fit defaults to 1).
public static class RemoteSchema {
    // The JSON shape a required param must have. Any = accept any JsonValueKind that is present and not
    // Null/Undefined (component.set / scene.component.set "value" can be a string, number, bool, array,
    // or object -- the handler's ConvertValue dispatches on the member type, so the boundary only checks
    // it is PRESENT and non-null).
    public enum Kind { String, Number, Bool, Any }

    public readonly record struct Param(string Name, Kind Kind, bool Required);

    public sealed record MethodSchema(string Method, Param[] Params) {
        public IEnumerable<Param> Required => Params.Where(p => p.Required);
        public IEnumerable<Param> Optional => Params.Where(p => !p.Required);
    }

    static Param Req(string name, Kind kind = Kind.String) => new(name, kind, true);
    static Param Opt(string name, Kind kind = Kind.String) => new(name, kind, false);

    // The method table -- mirrors RemoteHandlers.Dispatch exactly (every case there has a row here). Order
    // is the help-listing order. Keeping these in lockstep is the single maintenance rule: a new remote
    // method adds one row; a new required param adds one Req(...). Validate() refuses any method NOT here.
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
        new MethodSchema("scene.component.add",  new[] { Req("type") }),
        new MethodSchema("scene.component.set",  new[] { Req("type"), Req("member"), Req("value", Kind.Any) }),
        new MethodSchema("help",                 Array.Empty<Param>()),
    };

    static readonly Dictionary<string, MethodSchema> byName =
        Methods.ToDictionary(m => m.Method, StringComparer.Ordinal);

    public static bool IsKnownMethod(string method) => method is not null && byName.ContainsKey(method);

    public static MethodSchema For(string method) =>
        method is not null && byName.TryGetValue(method, out MethodSchema s) ? s : null;

    // D1 (editor-rework Phase D, "command registry"): the help/method listing is GENERATED from this same
    // table, so it can never drift from the dispatch surface or the validation contract -- the editor's
    // `help` command and any agent-facing method catalog read ONE source. Format: "method" when paramless,
    // else "method {a, b?}" with a trailing '?' marking optional params (required first, then optional, in
    // declaration order). A boundary guard and a self-describing catalog are the same table by construction.
    public static string Signature(MethodSchema schema) {
        if (schema is null || schema.Params.Length == 0)
            return schema?.Method ?? "";
        IEnumerable<string> parts = schema.Params
            .Where(p => p.Required).Select(p => p.Name)
            .Concat(schema.Params.Where(p => !p.Required).Select(p => p.Name + "?"));
        return $"{schema.Method} {{{string.Join(", ", parts)}}}";
    }

    public static string Signature(string method) => Signature(For(method));

    // Every method's signature, in table (help-listing) order -- the canonical method catalog.
    public static string[] Signatures => Methods.Select(Signature).ToArray();

    // The boundary guard. Returns null when the request is well-formed, else a single clean error string
    // describing the FIRST problem (unknown method, then required params in declaration order). The caller
    // (RemoteHandlers.Dispatch / the MCP MapTool) surfaces this as the request error -- the handler never
    // runs on a malformed request, so a bad shape can't NRE the editor or throw a cryptic JsonElement
    // exception. `p` is the request's "params" element; default/Undefined means "no params object".
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

    // Per-kind shape check for a PRESENT required param. String/Bool are strict (a number where a string
    // is required is the exact NRE-class bug). Number accepts a JSON number OR a numeric string (the
    // handlers parse "3" too, e.g. settleFrames/count). Any accepts anything non-null (already screened).
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
        _ => null, // Kind.Any -- presence + non-null already checked
    };

    static string Describe(Kind kind) => kind switch {
        Kind.String => "string",
        Kind.Number => "number",
        Kind.Bool => "boolean",
        _ => "value",
    };
}
