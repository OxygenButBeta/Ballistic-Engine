using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// D2 (editor-rework Phase D, "MCP boundary schema validation") -- the headless oracle that the MCP server's
// tool surface stays in PARITY with the engine's RemoteSchema, so the command-port boundary has ONE source
// of truth on BOTH sides (the editor's RemoteHandlers.Dispatch validates with RemoteSchema; the MCP bridge
// maps tools->methods + requires args before the pipe call). The MCP process is ZERO-DEPENDENCY by design
// (it does NOT reference the engine -- see BallisticEngine.Mcp.csproj's deliberate "nothing to version-chase"
// note), so the parity CANNOT be enforced at MCP runtime, and -- exactly like MenuRegistryTests (A1) /
// ComponentPreviewTests (B1), which mirror an unreferenceable editor type -- this suite MIRRORS the MCP's
// ToolBindings table and checks the mirror against RemoteSchema.
//
// ★ THE MIRROR BELOW MUST MATCH `BallisticEngine.Mcp.Program.ToolBindings` EXACTLY. That table is the MCP's
// declarative boundary contract (one row per pipe-backed tool: the command-port method(s) it can produce +
// the args it makes required via Str() before the pipe call). The MCP's ToolCall asserts at RUNTIME that
// MapTool only ever produces a method its ToolBinding declares (so the table is load-bearing there), and
// THIS suite asserts the declared methods are real RemoteSchema methods + cover their required string params.
// Together: the MCP boundary can only ever send a real, schema-known method, and a missing required string is
// rejected at the MCP before the editor sees it. If a future edit adds an MCP tool / changes a binding, add
// the row both places -- a divergence makes (1)/(2) below RED.
//
// THE CONTRACT under test (plan 5/D2 -- "reject malformed params with a clean error before the editor sees
// them"): for every pipe-backed MCP tool, (a) each method it can produce is a REAL RemoteSchema method (no
// typo / no editor-undispatched method), and (b) the args the MCP boundary makes REQUIRED cover that method's
// required STRING params. Non-string required params (component.set's Kind.Any "value") are NOT boundary-
// required (the MCP reads them with TryGetProperty so an agent can pass a number/array); the editor's Validate
// enforces those -- that division of labour is asserted too. The two CLI-backed tools (scene_query/
// scene_gbuffer) shell out to `bal` and have NO command-port method -- they must NOT appear as bindings.
internal static class McpBoundaryTests {
    // Local mirror of BallisticEngine.Mcp.Program.ToolBinding (the MCP type is unreferenceable: referencing
    // the Exe project would force its locked apphost.exe into this runner's bin, the documented .exe-lock
    // hazard). Built the SAME way the MCP declares it. RequiredArgs = the agent must supply (Str()-guarded);
    // DefaultedArgs = the MCP fills if omitted (e.g. screenshot `path`). The boundary GUARANTEE to the editor
    // = RequiredArgs UNION DefaultedArgs. A parity regression surfaces here.
    readonly record struct ToolBinding(string Tool, string[] Methods, string[] RequiredArgs, string[] DefaultedArgs);

    // ★ KEEP IN LOCKSTEP with BallisticEngine.Mcp.Program.ToolBindings (see file header).
    static readonly ToolBinding[] Bindings = [
        new("editor_status",       ["editor.status"],        [],                  []),
        new("scene_describe",      ["scene.describe"],       [],                  []),
        new("entity_get",          ["entity.get"],           ["entity"],          []),
        new("entity_create",       ["entity.create"],        ["name"],            []),
        new("entity_delete",       ["entity.delete"],        ["entity"],          []),
        new("component_add",       ["component.add"],         ["entity", "type"], []),
        new("component_remove",    ["component.remove"],      ["entity", "type"], []),
        new("component_set",       ["component.set"],         ["entity", "target"], []),
        new("editor_select",       ["select"],               ["entity"],          []),
        new("play_control",        ["play.start", "play.stop", "play.pause", "play.step"], ["action"], []),
        new("scene_save",          ["scene.save"],           [],                  []),
        new("scene_open",          ["scene.open"],           ["path"],            []),
        new("editor_undo",         ["undo", "redo"],         [],                  []),
        new("editor_screenshot",   ["screenshot"],           [],                  ["path"]), // path defaults to a temp file
        new("console_tail",        ["console.tail"],         [],                  []),
        new("scripts_rebuild",     ["scripts.rebuild"],      [],                  []),
        new("editor_frame",        ["editor.frame"],         [],                  []),
        new("editor_refresh",      ["editor.refresh"],       [],                  []),
        new("scene_component_set", ["scene.component.set"],  ["type", "member"],  []),
    ];

    public static int Run() {
        var h = new Harness();

        // -- (1) Every binding maps to REAL schema methods (no typo, no editor-undispatched method) --------
        // A method the MCP can send that the schema doesn't know would 404 / bypass validation -- exactly the
        // drift this guard exists to catch. Checked per method so a regression names the offending one.
        foreach (ToolBinding b in Bindings)
            foreach (string method in b.Methods)
                h.Check($"MCP tool '{b.Tool}' -> '{method}' is a real RemoteSchema method",
                    RemoteSchema.IsKnownMethod(method),
                    $"'{method}' is not in RemoteSchema (typo or the engine renamed/removed it)");

        // -- (2) The boundary GUARANTEES each method's REQUIRED STRING param is present in the pipe call -----
        // For every required String param of the methods a tool maps to, the MCP must either require it from
        // the agent (Str()-guard, rejected cleanly if absent) OR fill it with a default -- i.e. it must be in
        // RequiredArgs UNION DefaultedArgs. Otherwise a missing required string slips past the MCP to the
        // editor (the exact drift this guard catches). (Required Kind.Any/Number params are NOT covered here
        // -- the editor's Validate enforces those; see check (3).)
        foreach (ToolBinding b in Bindings) {
            var guaranteed = new HashSet<string>(b.RequiredArgs.Concat(b.DefaultedArgs), StringComparer.Ordinal);
            foreach (string method in b.Methods) {
                RemoteSchema.MethodSchema schema = RemoteSchema.For(method);
                if (schema is null) continue; // (1) already flagged it; don't double-fault
                foreach (RemoteSchema.Param p in schema.Required) {
                    if (p.Kind != RemoteSchema.Kind.String) continue; // only String requireds are boundary-guarded
                    h.Check($"MCP '{b.Tool}' guarantees schema-required string '{p.Name}' of '{method}'",
                        guaranteed.Contains(p.Name),
                        $"'{method}' requires string '{p.Name}' but the MCP boundary neither Str()-guards nor "
                        + "defaults it -> a missing one slips past the MCP to the editor");
                }
            }
        }
        // The one DefaultedArg case must be real: editor_screenshot defaults `path`, which screenshot requires.
        h.Check("editor_screenshot defaults the schema-required string 'path' (agent may omit it)",
            Binding("editor_screenshot") is { } sh && sh.DefaultedArgs.Contains("path")
                && !sh.RequiredArgs.Contains("path")
                && RemoteSchema.For("screenshot")!.Required.Any(p => p.Name == "path" && p.Kind == RemoteSchema.Kind.String));
        // Defaulted args must themselves be real params of the method (a typo would silently never satisfy
        // anything). Every DefaultedArg must name a param the schema declares for one of the tool's methods.
        h.Check("every DefaultedArg names a real schema param of its tool's method(s)",
            Bindings.All(b => b.DefaultedArgs.All(arg =>
                b.Methods.Any(m => RemoteSchema.For(m) is { } s && s.Params.Any(p => p.Name == arg)))));

        // -- (3) The boundary does NOT over-require: a Kind.Any required param is NOT boundary-required ------
        // component.set / scene.component.set carry a required "value" of Kind.Any -- the MCP reads it with
        // TryGetProperty (so the agent can pass a number/array/object), and the editor's RemoteSchema.Validate
        // enforces its presence. The MCP boundary must NOT list "value" as a required STRING arg (that would
        // wrongly reject a numeric value at the boundary). This pins the division of labour between the two
        // boundary checks so it can't silently collapse one way or the other.
        h.Check("component_set boundary requires entity+target but NOT value (value is Kind.Any, editor-enforced)",
            Binding("component_set") is { } cs
                && cs.RequiredArgs.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(new[] { "entity", "target" })
                && !cs.RequiredArgs.Contains("value"));
        h.Check("scene_component_set boundary requires type+member but NOT value (value is Kind.Any)",
            Binding("scene_component_set") is { } scs
                && scs.RequiredArgs.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(new[] { "member", "type" })
                && !scs.RequiredArgs.Contains("value"));
        // The editor side DOES require value for both (Kind.Any), so the division is complete, not a hole.
        h.Check("RemoteSchema requires 'value' (Kind.Any) for component.set",
            RemoteSchema.For("component.set")!.Required.Any(p => p.Name == "value" && p.Kind == RemoteSchema.Kind.Any));
        h.Check("RemoteSchema requires 'value' (Kind.Any) for scene.component.set",
            RemoteSchema.For("scene.component.set")!.Required.Any(p => p.Name == "value" && p.Kind == RemoteSchema.Kind.Any));

        // -- (4) Fan-out tools name only valid methods, and the set is complete ----------------------------
        // play_control fans its 'action' arg to the four play.* methods; editor_undo to undo|redo. Assert the
        // fan-out set is EXACTLY the schema's play.*/undo+redo methods, so a new play method (or a renamed one)
        // forces this binding to be updated (and surfaces here if it isn't).
        h.Check("play_control fans to exactly {play.start, play.stop, play.pause, play.step}",
            Binding("play_control") is { } pc
                && pc.Methods.OrderBy(s => s, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "play.pause", "play.start", "play.step", "play.stop" }));
        h.Check("editor_undo fans to exactly {undo, redo}",
            Binding("editor_undo") is { } eu
                && eu.Methods.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(new[] { "redo", "undo" }));

        // -- (5) The CLI-backed tools are NOT bindings (they have no command-port method) -------------------
        // scene_query / scene_gbuffer shell out to `bal` (the device-free CLI path) -- they intentionally
        // bypass the pipe + RemoteSchema. They MUST NOT appear as pipe bindings (that would imply a phantom
        // command-port method). This pins the documented exception so it can't be added by mistake.
        h.Check("scene_query is NOT a pipe binding (CLI-backed, no command-port method)",
            !Bindings.Any(b => b.Tool == "scene_query"));
        h.Check("scene_gbuffer is NOT a pipe binding (CLI-backed, no command-port method)",
            !Bindings.Any(b => b.Tool == "scene_gbuffer"));

        // -- (6) Internal consistency of the binding table itself ------------------------------------------
        h.Check("every binding has at least one method",
            Bindings.All(b => b.Methods.Length > 0));
        h.Check("tool names are unique (no duplicate binding rows)",
            Bindings.Select(b => b.Tool).Distinct(StringComparer.Ordinal).Count() == Bindings.Length);
        h.Check("methods within a binding are unique",
            Bindings.All(b => b.Methods.Distinct(StringComparer.Ordinal).Count() == b.Methods.Length));
        h.Check("required args within a binding are unique",
            Bindings.All(b => b.RequiredArgs.Distinct(StringComparer.Ordinal).Count() == b.RequiredArgs.Length));

        // -- (7) Coverage breadth: the MCP-mapped methods are a clean SUBSET of the schema ------------------
        // Not every schema method needs an MCP tool (help / unity.import / scene.component.add are pipe/CLI-only
        // or editor-internal), so this is NOT a 1:1 cover -- but the mapped methods must be a SUBSET of the
        // schema (already proven per-method in (1)); assert the set relation explicitly as a single guard so a
        // phantom command-port method is one obvious failure.
        var mappedMethods = Bindings.SelectMany(b => b.Methods).ToHashSet(StringComparer.Ordinal);
        var schemaMethods = RemoteSchema.Methods.Select(m => m.Method).ToHashSet(StringComparer.Ordinal);
        h.Check("all MCP-mapped methods are a SUBSET of RemoteSchema (no phantom command-port method)",
            mappedMethods.IsSubsetOf(schemaMethods),
            $"phantom methods: [{string.Join(", ", mappedMethods.Except(schemaMethods))}]");

        return h.Report("MCP boundary (D2)");
    }

    static ToolBinding? Binding(string tool) {
        foreach (ToolBinding b in Bindings)
            if (b.Tool == tool) return b;
        return null;
    }
}
