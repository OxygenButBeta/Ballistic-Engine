using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BallisticEngine.Mcp;

// MCP stdio server -> editor named pipe bridge. Each MCP tool maps onto one command-port method;
// the editor does the real work on its main thread (undoable, viewport-repainting — see
// BallisticEngine.Editor/Remote/). Tools are TASK-LEVEL and few on purpose: tool definitions live
// in the agent's context window, so a small multiplexed surface beats one-tool-per-verb.
//
// Stdio transport: one JSON-RPC 2.0 message per line. Handles initialize / notifications/* /
// tools/list / tools/call / ping. The pipe connects lazily and reconnects per failure, so the
// server can start before the editor does.
internal static class Program {
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static int Main() {
        Console.OutputEncoding = new UTF8Encoding(false);
        string? line;
        while ((line = Console.In.ReadLine()) is not null) {
            if (line.Length == 0)
                continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }
            using (doc) {
                JsonElement root = doc.RootElement;
                string method = root.TryGetProperty("method", out JsonElement m) ? m.GetString() ?? "" : "";
                bool hasId = root.TryGetProperty("id", out JsonElement idEl);
                if (!hasId)
                    continue; // notifications need no response

                object response = method switch {
                    "initialize" => Result(idEl, new {
                        protocolVersion = root.GetProperty("params").TryGetProperty("protocolVersion", out JsonElement pv)
                            ? pv.GetString() : "2024-11-05",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "ballistic-editor", version = "1.0" },
                    }),
                    "ping" => Result(idEl, new { }),
                    "tools/list" => Result(idEl, new { tools = ToolDefinitions }),
                    "tools/call" => ToolCall(idEl, root.GetProperty("params")),
                    _ => Error(idEl, -32601, $"method not found: {method}"),
                };
                Console.Out.WriteLine(JsonSerializer.Serialize(response));
                Console.Out.Flush();
            }
        }
        return 0;
    }

    static object Result(JsonElement id, object result) =>
        new { jsonrpc = "2.0", id, result };

    static object Error(JsonElement id, int code, string message) =>
        new { jsonrpc = "2.0", id, error = new { code, message } };

    // ---- tools/call -> pipe ----------------------------------------------------

    static object ToolCall(JsonElement id, JsonElement p) {
        string tool = p.GetProperty("name").GetString() ?? "";
        JsonElement args = p.TryGetProperty("arguments", out JsonElement a) ? a : default;

        // CLI-backed tools (scene_query / scene_gbuffer): run the `bal` CLI headlessly (no editor needed) —
        // GpuSceneQuery + raw G-buffer. The editor's LIVE query surface is deferred (it touches the GPU
        // surface that hung before), so these shell out to the proven device-free CLI subprocess path.
        if (tool is "scene_query" or "scene_gbuffer")
            return RunCliTool(id, tool, args);

        string method;
        object? pipeParams;
        try { (method, pipeParams) = MapTool(tool, args); }
        catch (Exception ex) { return Result(id, ToolText(ex.Message, isError: true)); }
        if (method.Length == 0)
            return Result(id, ToolText($"unknown tool '{tool}'", isError: true));

        try {
            JsonElement reply = CallPipe(method, pipeParams);
            return Result(id, reply.TryGetProperty("error", out JsonElement err)
                ? ToolText(err.GetString() ?? "error", isError: true)
                : ToolText(JsonSerializer.Serialize(reply.GetProperty("result"), Indented), isError: false));
        }
        catch (Exception ex) {
            return Result(id, ToolText(
                $"editor not reachable ({ex.Message}) — is the Ballistic editor running?", isError: true));
        }
    }

    static object ToolText(string text, bool isError) =>
        new { content = new[] { new { type = "text", text } }, isError };

    // ---- CLI-backed tools (scene_query / scene_gbuffer) -> `bal` subprocess ---------

    static object RunCliTool(JsonElement id, string tool, JsonElement a) {
        try {
            string scene = Str(a, "scene");
            var args = new List<string>();
            if (tool == "scene_query") {
                args.Add("query");
                args.Add(Str(a, "op"));
                args.Add(scene);
                string? points = OptStr(a, "points");
                string? pairs = OptStr(a, "pairs");
                if (points is not null) { args.Add("--points"); args.Add(points); }
                if (pairs is not null) { args.Add("--pairs"); args.Add(pairs); }
            } else { // scene_gbuffer
                args.Add("gbuffer");
                args.Add(scene);
                string? outDir = OptStr(a, "out");
                if (outDir is not null) { args.Add("--out"); args.Add(outDir); }
            }
            (int code, string stdout, string stderr) = RunCli(args);
            return Result(id, ToolText(stdout.Length > 0 ? stdout
                : (code == 0 ? "(ok, no output)" : $"bal exited {code}: {stderr}"), isError: code != 0));
        } catch (Exception ex) {
            return Result(id, ToolText(ex.Message, isError: true));
        }
    }

    static (int, string, string) RunCli(List<string> args) {
        string bal = FindBalExe();
        var psi = new System.Diagnostics.ProcessStartInfo {
            FileName = bal, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string s in args) psi.ArgumentList.Add(s);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(300_000)) { try { proc.Kill(true); } catch { } throw new Exception("bal timed out"); }
        return (proc.ExitCode, stdout, stderr);
    }

    // bal.exe sits next to this MCP server's build output, or in the engine repo build tree.
    static string FindBalExe() {
        string local = Path.Combine(AppContext.BaseDirectory, "bal.exe");
        if (File.Exists(local)) return local;
        string? engineRoot = Environment.GetEnvironmentVariable("BALLISTIC_ENGINE_ROOT");
        if (engineRoot is null) {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "BallisticEngine.slnx"))) { engineRoot = dir.FullName; break; }
        }
        if (engineRoot is not null) {
            string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            foreach (string c in new[] { config, config == "Debug" ? "Release" : "Debug" }) {
                string exe = Path.Combine(engineRoot, "BallisticEngine.Cli", "bin", c, "net9.0", "bal.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        throw new Exception("bal.exe not found (build BallisticEngine.Cli or set BALLISTIC_ENGINE_ROOT)");
    }

    // Tool name + MCP arguments -> command-port method + params (mostly pass-through).
    static (string method, object? p) MapTool(string tool, JsonElement a) => tool switch {
        "editor_status" => ("editor.status", null),
        "scene_describe" => ("scene.describe", null),
        "entity_get" => ("entity.get", new { entity = Str(a, "entity") }),
        "entity_create" => ("entity.create", new {
            name = Str(a, "name"),
            position = OptStr(a, "position"),
            parent = OptStr(a, "parent"),
        }),
        "entity_delete" => ("entity.delete", new { entity = Str(a, "entity") }),
        "component_add" => ("component.add", new { entity = Str(a, "entity"), type = Str(a, "type") }),
        "component_remove" => ("component.remove", new { entity = Str(a, "entity"), type = Str(a, "type") }),
        "component_set" => ("component.set", new {
            entity = Str(a, "entity"),
            target = Str(a, "target"),
            value = a.TryGetProperty("value", out JsonElement v) ? (object)v : "",
        }),
        "editor_select" => ("select", new { entity = Str(a, "entity") }),
        "play_control" => (Str(a, "action") switch {
            "start" => "play.start", "stop" => "play.stop", "pause" => "play.pause", "step" => "play.step",
            var other => throw new Exception($"play_control action must be start|stop|pause|step (got '{other}')"),
        }, (object?)null),
        "scene_save" => ("scene.save", null),
        "scene_open" => ("scene.open", new { path = Str(a, "path") }),
        "editor_undo" => (OptStr(a, "action") == "redo" ? "redo" : "undo", (object?)null),
        "editor_screenshot" => ("screenshot", new {
            path = OptStr(a, "path") ?? Path.Combine(Path.GetTempPath(), $"ballistic-editor-{Environment.TickCount64}.bmp"),
            settleFrames = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("settleFrames", out JsonElement s)
                ? s.GetInt32() : 3,
        }),
        "console_tail" => ("console.tail", new {
            count = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("count", out JsonElement c) ? c.GetInt32() : 50,
        }),
        "scripts_rebuild" => ("scripts.rebuild", null),
        "editor_frame" => ("editor.frame", new {
            entity = OptStr(a, "entity"),
            dir = OptStr(a, "dir"),
            fit = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("fit", out JsonElement f) ? (object)f.GetDouble() : 1.0,
        }),
        "editor_refresh" => ("editor.refresh", null),
        "scene_component_set" => ("scene.component.set", new {
            type = Str(a, "type"),
            member = Str(a, "member"),
            value = a.TryGetProperty("value", out JsonElement scv) ? (object)scv : "",
        }),
        _ => ("", null),
    };

    static string Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new Exception($"missing argument '{name}'");

    static string? OptStr(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ---- pipe client -------------------------------------------------------------

    static NamedPipeClientStream? pipe;
    static StreamReader? pipeReader;
    static StreamWriter? pipeWriter;
    static long pipeSeq;

    // Optional args must VANISH from the pipe request, not arrive as null (the editor treats a
    // present-but-null param as a value).
    static readonly JsonSerializerOptions SkipNulls = new() {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    static JsonElement CallPipe(string method, object? parameters) {
        for (int attempt = 0; attempt < 2; attempt++) {
            try {
                EnsureConnected();
                var request = new Dictionary<string, object?> { ["id"] = ++pipeSeq, ["method"] = method };
                if (parameters is not null)
                    request["params"] = parameters;
                pipeWriter!.WriteLine(JsonSerializer.Serialize(request, SkipNulls));
                string reply = pipeReader!.ReadLine() ?? throw new IOException("editor closed the pipe");
                return JsonDocument.Parse(reply).RootElement.Clone();
            }
            catch (Exception) when (attempt == 0) {
                Disconnect(); // stale pipe from an earlier editor session — reconnect once
            }
        }
        throw new IOException("pipe call failed");
    }

    static void EnsureConnected() {
        if (pipe is { IsConnected: true })
            return;
        Disconnect();
        pipe = new NamedPipeClientStream(".", "BallisticEditor", PipeDirection.InOut);
        pipe.Connect(timeout: 3000);
        pipeReader = new StreamReader(pipe, Encoding.UTF8);
        pipeWriter = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
    }

    static void Disconnect() {
        pipeReader = null;
        pipeWriter = null;
        try { pipe?.Dispose(); } catch { }
        pipe = null;
    }

    // ---- tool definitions ----------------------------------------------------------

    static object Schema(params (string Name, string Type, string Description, bool Required)[] props) => new {
        type = "object",
        properties = props.ToDictionary(p => p.Name, p => (object)new { type = p.Type, description = p.Description }),
        required = props.Where(p => p.Required).Select(p => p.Name).ToArray(),
    };

    static readonly object[] ToolDefinitions = [
        new { name = "editor_status", description = "Editor state: open scene, play mode, selection, entity count, pending load.", inputSchema = Schema() },
        new { name = "scene_describe", description = "Summary of the open scene: entities with components, parents, scene components. Use entity_get for full member values.", inputSchema = Schema() },
        new { name = "entity_get", description = "Full live detail of one entity: transform, components with current member values.", inputSchema = Schema(("entity", "string", "Entity name, unique name substring, id, or id prefix", true)) },
        new { name = "entity_create", description = "Create an entity (undoable).", inputSchema = Schema(
            ("name", "string", "Entity name", true),
            ("position", "string", "Optional world position 'x,y,z'", false),
            ("parent", "string", "Optional parent entity", false)) },
        new { name = "entity_delete", description = "Delete an entity (undoable).", inputSchema = Schema(("entity", "string", "Entity to delete", true)) },
        new { name = "component_add", description = "Add a component by type name (engine or game script). Undoable.", inputSchema = Schema(
            ("entity", "string", "Target entity", true),
            ("type", "string", "Component type, e.g. PointLight, Rigidbody, PlayerController", true)) },
        new { name = "component_remove", description = "Remove a component (Type or Type@n when repeated). Undoable.", inputSchema = Schema(
            ("entity", "string", "Target entity", true),
            ("type", "string", "Component type, @n suffix when the entity has several", true)) },
        new { name = "component_set", description = "Set a value (undoable). target: name|active|tag|layer|transform.position|transform.rotation (Euler deg)|transform.scale|<Component>.<Member>. Asset members take 'Assets/...' paths.", inputSchema = Schema(
            ("entity", "string", "Target entity", true),
            ("target", "string", "What to set, e.g. 'PointLight.Lumens' or 'transform.position'", true),
            ("value", "string", "New value ('x,y,z' for vectors, enum names, asset paths, numbers)", true)) },
        new { name = "editor_select", description = "Select an entity in the editor (shows it in the Inspector).", inputSchema = Schema(("entity", "string", "Entity to select", true)) },
        new { name = "play_control", description = "Control play mode: start (saves first), stop, pause (toggle), step (one frame while paused).", inputSchema = Schema(("action", "string", "start | stop | pause | step", true)) },
        new { name = "scene_save", description = "Save the open scene to its file.", inputSchema = Schema() },
        new { name = "scene_open", description = "Open a scene by project-relative path (async; poll editor_status until 'loading' clears).", inputSchema = Schema(("path", "string", "e.g. Assets/Scenes/Level1.scene", true)) },
        new { name = "editor_undo", description = "Undo or redo the last edit (remote edits are undoable too).", inputSchema = Schema(("action", "string", "undo | redo (default undo)", false)) },
        new { name = "editor_screenshot", description = "Capture the editor window to a BMP (returns the queued path; the file appears within a few frames).", inputSchema = Schema(
            ("path", "string", "Output .bmp path (default: temp file)", false),
            ("settleFrames", "number", "Frames to wait before capture (default 3)", false)) },
        new { name = "console_tail", description = "Recent editor console entries (errors/warnings/info).", inputSchema = Schema(("count", "number", "How many entries (default 50)", false)) },
        new { name = "scripts_rebuild", description = "Recompile game scripts and hot-reload them (compile-first: nothing changes on errors).", inputSchema = Schema() },
        new { name = "editor_frame", description = "Frame the Scene-view camera on an entity (or the whole scene if none given) so a screenshot shows it. The Scene view uses the editor fly camera, NOT an HDCamera entity.", inputSchema = Schema(
            ("entity", "string", "Entity to frame; omit to frame the whole scene", false),
            ("dir", "string", "Look direction 'x,y,z' e.g. '0.3,-0.5,1' for a 3/4 top view; omit to keep current", false),
            ("fit", "number", "Zoom multiplier on the framed radius (1=default, <1 closer, >1 wider)", false)) },
        new { name = "editor_refresh", description = "Force a full asset reimport (registers newly written .scene/.volume/.mat assets).", inputSchema = Schema() },
        new { name = "scene_component_set", description = "Set a member on a scene-wide component (Skybox/ProceduralSky/SceneLighting), e.g. tune sky exposure. Undoable.", inputSchema = Schema(
            ("type", "string", "Scene component type, e.g. ProceduralSky", true),
            ("member", "string", "Member name, e.g. exposure", true),
            ("value", "string", "New value (number, 'x,y,z' vector, enum name)", true)) },
        new { name = "scene_query", description = "Spatial perception over a scene's geometry (headless ray queries over the DXR TLAS — the agent's 'eyes'). op=occupancy (is each point inside solid?), classify (open/enclosed/solid), nudge (move occupied points to free space), rooms (visibility-cluster labels), visibility (clear line of sight per A>B pair). Takes a scene FILE path; runs headlessly (no editor needed). Returns JSON.", inputSchema = Schema(
            ("op", "string", "occupancy | classify | nudge | rooms | visibility", true),
            ("scene", "string", "Scene file path, e.g. Assets/Scenes/Level1.scene", true),
            ("points", "string", "Semicolon-separated world points 'x,y,z; x,y,z' (occupancy/classify/nudge/rooms)", false),
            ("pairs", "string", "Semicolon-separated A>B pairs 'ax,ay,az>bx,by,bz; ...' (visibility)", false)) },
        new { name = "scene_gbuffer", description = "Dump the raw G-buffer (depth/world-normal/albedo) of a scene so the agent can read geometry directly, not just the tonemapped pixel. Renders one deterministic frame headlessly; writes depth.bin/normal.bin/albedo.bin + manifest.json (dims/format/decode notes). Returns the file paths.", inputSchema = Schema(
            ("scene", "string", "Scene file path, e.g. Assets/Scenes/Level1.scene", true),
            ("out", "string", "Output directory (default: <project>/Library/GBuffer)", false)) },
    ];
}
