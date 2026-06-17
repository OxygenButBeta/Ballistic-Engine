using System.Text.Json;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// D2 (editor-rework Phase D, "AI-operability hardening") -- the headless oracle for the command-port
// boundary guard. RemoteSchema is engine-side (the test project references the engine LIBRARY only, like
// the bal CLI, NOT the editor exe), so the schema + Validate() are directly reachable here -- the editor's
// RemoteHandlers.Dispatch is the WIRING (call Validate first, throw on error), tested by inspection; this
// suite proves the LOGIC.
//
// THE CONTRACT under test (plan 3.5 -- "malformed JSON can NRE the editor"): Validate(method, params)
// returns null for a well-formed request, else ONE clean error string, BEFORE any handler runs. It rejects
// (a) an unknown method, (b) a missing required param, (c) a required param of the wrong JSON kind (the
// exact NRE-class bug: a number where GetString() is called). It does NOT reject a well-formed request with
// extra/optional params, nor does it semantically validate values (unknown entity/type is the handler's
// job against the live scene). The suite also proves table/Dispatch parity: every method the schema knows
// is a real method, and the required-param list matches what the handlers actually demand.
internal static class RemoteSchemaTests {
    public static int Run() {
        var h = new Harness();

        // -- (1) Well-formed requests pass (Validate returns null) ------------------------------------
        h.Check("no-param method with no params object is valid",
            RemoteSchema.Validate("editor.status", default) is null);
        h.Check("no-param method tolerates an empty params object",
            RemoteSchema.Validate("scene.save", Params("{}")) is null);
        h.Check("entity.get with a string entity is valid",
            RemoteSchema.Validate("entity.get", Params("""{"entity":"Lamp"}""")) is null);
        h.Check("component.add with entity+type strings is valid",
            RemoteSchema.Validate("component.add", Params("""{"entity":"Lamp","type":"PointLight"}""")) is null);
        h.Check("component.set value may be a NUMBER (Kind.Any)",
            RemoteSchema.Validate("component.set", Params("""{"entity":"L","target":"PointLight.Lumens","value":1500}""")) is null);
        h.Check("component.set value may be a STRING (Kind.Any)",
            RemoteSchema.Validate("component.set", Params("""{"entity":"L","target":"transform.position","value":"1,2,3"}""")) is null);
        h.Check("component.set value may be an ARRAY (Kind.Any)",
            RemoteSchema.Validate("component.set", Params("""{"entity":"L","target":"transform.scale","value":[1,2,3]}""")) is null);
        h.Check("scene.component.set requires type+member+value, all present is valid",
            RemoteSchema.Validate("scene.component.set", Params("""{"type":"ProceduralSky","member":"exposure","value":2}""")) is null);

        // Optional params: present or absent, both valid; extra unknown params are ignored (not rejected).
        h.Check("entity.create with only the required name is valid (optionals absent)",
            RemoteSchema.Validate("entity.create", Params("""{"name":"Lamp"}""")) is null);
        h.Check("entity.create with optional position present is valid",
            RemoteSchema.Validate("entity.create", Params("""{"name":"Lamp","position":"1,2,3"}""")) is null);
        h.Check("extra unknown params do not fail validation",
            RemoteSchema.Validate("entity.get", Params("""{"entity":"Lamp","bogus":42}""")) is null);
        h.Check("editor.frame is all-optional, empty object valid",
            RemoteSchema.Validate("editor.frame", Params("{}")) is null);

        // -- (2) Unknown method is rejected ------------------------------------------------------------
        h.Check("unknown method rejected",
            RemoteSchema.Validate("entity.teleport", default) is { } e1 && e1.Contains("unknown method"));
        h.Check("empty method rejected",
            RemoteSchema.Validate("", default) is { } e2 && e2.Contains("missing 'method'"));
        h.Check("null method rejected",
            RemoteSchema.Validate(null!, default) is { } e3 && e3.Contains("missing 'method'"));

        // -- (3) Missing required param is rejected, naming the param ----------------------------------
        h.Check("entity.get with NO params object rejected",
            RemoteSchema.Validate("entity.get", default) is { } e4 && e4.Contains("entity"));
        h.Check("component.add missing 'type' rejected, names 'type'",
            RemoteSchema.Validate("component.add", Params("""{"entity":"Lamp"}""")) is { } e5 && e5.Contains("type"));
        h.Check("component.set missing 'value' rejected, names 'value'",
            RemoteSchema.Validate("component.set", Params("""{"entity":"L","target":"t"}""")) is { } e6 && e6.Contains("value"));
        h.Check("a required param present but NULL counts as missing",
            RemoteSchema.Validate("scene.open", Params("""{"path":null}""")) is { } e7 && e7.Contains("path"));

        // -- (4) Wrong JSON kind on a required param is rejected (the NRE-class bug) -------------------
        h.Check("entity (string) given a NUMBER is rejected -- the GetString() NRE class",
            RemoteSchema.Validate("entity.get", Params("""{"entity":5}""")) is { } e8 && e8.Contains("must be a string"));
        h.Check("entity (string) given an OBJECT is rejected",
            RemoteSchema.Validate("select", Params("""{"entity":{"x":1}}""")) is { } e9 && e9.Contains("must be a string"));
        h.Check("scene.component.set member (string) given a number is rejected",
            RemoteSchema.Validate("scene.component.set", Params("""{"type":"Sky","member":3,"value":2}""")) is { } e10 && e10.Contains("must be a string"));

        // Number kind: a JSON number OR a numeric string is accepted; a non-numeric string is rejected.
        // (Only checked where a Number param is REQUIRED -- none today are required, so verify via the
        // public CheckKind contract indirectly: settleFrames/count/fit are OPTIONAL and NOT kind-checked,
        // matching the handlers, so a well-formed call with a numeric or even non-numeric optional passes.)
        h.Check("optional number param is NOT kind-checked (handler tolerates it)",
            RemoteSchema.Validate("screenshot", Params("""{"path":"a.bmp","settleFrames":"oops"}""")) is null);

        // -- (5) Table <-> Dispatch parity: the schema mirrors the real method surface ----------------
        // Every method the schema declares must be a known method (no typos / drift), and the set must be
        // exactly the methods RemoteHandlers.Dispatch handles. This guards the single maintenance rule
        // (add a method -> add a row). The expected set is the literal Dispatch case labels.
        var expected = new HashSet<string>(StringComparer.Ordinal) {
            "editor.status", "scene.describe", "scene.save", "scene.open",
            "entity.get", "entity.create", "entity.delete",
            "component.add", "component.remove", "component.set", "select",
            "play.start", "play.stop", "play.pause", "play.step",
            "undo", "redo", "screenshot", "console.tail", "scripts.rebuild",
            "unity.import", "editor.frame", "editor.refresh",
            "scene.component.add", "scene.component.set", "help",
        };
        var actual = RemoteSchema.Methods.Select(m => m.Method).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        h.Check("schema method set == Dispatch case set (no drift)", missing.Count == 0 && extra.Count == 0,
            $"missing from schema: [{string.Join(", ", missing)}]  extra in schema: [{string.Join(", ", extra)}]");
        h.Check("every schema method is IsKnownMethod",
            RemoteSchema.Methods.All(m => RemoteSchema.IsKnownMethod(m.Method)));
        h.Check("For() returns the schema for a known method",
            RemoteSchema.For("component.set") is { } cs && cs.Required.Count() == 3);
        h.Check("For() returns null for an unknown method",
            RemoteSchema.For("nope") is null);

        // Required-param parity spot checks (the handlers' RequireString calls): these must be REQUIRED.
        h.Check("component.set required params are entity,target,value",
            RemoteSchema.For("component.set")!.Required.Select(p => p.Name).OrderBy(n => n)
                .SequenceEqual(new[] { "entity", "target", "value" }));
        h.Check("scene.open requires exactly 'path'",
            RemoteSchema.For("scene.open")!.Required.Select(p => p.Name).SequenceEqual(new[] { "path" }));
        h.Check("entity.create requires only 'name' (position/parent optional)",
            RemoteSchema.For("entity.create")!.Required.Select(p => p.Name).SequenceEqual(new[] { "name" })
            && RemoteSchema.For("entity.create")!.Optional.Select(p => p.Name).OrderBy(n => n)
                .SequenceEqual(new[] { "parent", "position" }));

        // -- (6) D1: the help/method catalog is GENERATED from the table (single source of truth) -------
        // RemoteHandlers.Help() and the dispatch backstop now read RemoteSchema.Signatures instead of a
        // hand-listed array, so the agent-facing catalog can never drift from the dispatch/validation
        // surface. These prove the signature renderer over the SAME table the boundary guard validates.
        h.Check("Signatures has exactly one entry per method (catalog == table)",
            RemoteSchema.Signatures.Length == RemoteSchema.Methods.Count);
        h.Check("a paramless method renders as just its name",
            RemoteSchema.Signature("editor.status") == "editor.status");
        h.Check("a required-only method renders 'method {req}'",
            RemoteSchema.Signature("scene.open") == "scene.open {path}");
        h.Check("required params come BEFORE optionals, optionals marked with '?'",
            RemoteSchema.Signature("entity.create") == "entity.create {name, position?, parent?}");
        h.Check("a multi-required method lists all required, no '?'",
            RemoteSchema.Signature("component.set") == "component.set {entity, target, value}");
        h.Check("an all-optional method marks every param with '?'",
            RemoteSchema.Signature("editor.frame") == "editor.frame {entity?, dir?, fit?}");
        h.Check("Signature(string) agrees with Signature(MethodSchema)",
            RemoteSchema.Signature("component.add") == RemoteSchema.Signature(RemoteSchema.For("component.add")));
        h.Check("Signature of an unknown method is empty (safe, no throw)",
            RemoteSchema.Signature("nope") == "");
        h.Check("every signature starts with its own method name (catalog faithfully derived, no drift)",
            RemoteSchema.Methods.All(m => RemoteSchema.Signature(m).StartsWith(m.Method, StringComparison.Ordinal)));
        h.Check("the Signatures order matches the table (help-listing order preserved)",
            RemoteSchema.Signatures.SequenceEqual(RemoteSchema.Methods.Select(RemoteSchema.Signature)));

        return h.Report("RemoteSchema (D2)");
    }

    // Build a JsonElement "params" object from a literal. The document is disposed, but JsonElement values
    // copied out of a disposed document throw; Validate() only reads during the call, so we keep the doc
    // alive by cloning the root (Clone detaches it from the document lifetime).
    static JsonElement Params(string json) {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
