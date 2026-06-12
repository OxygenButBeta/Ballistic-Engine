using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BallisticEngine.Serialization;
using OpenTK.Mathematics;

namespace BallisticEngine.Editor;

// The command port's method surface — a small, task-level set (token-lean by design: the scene
// SUMMARY is the default query response; full detail is per-entity on request). Every handler runs
// on the EDITOR MAIN THREAD (via RemoteCommandQueue), every mutation pushes EditorUndo first and
// marks the viewport dirty — remote edits behave exactly like human edits (undoable, repainted).
internal static class RemoteHandlers {
    static EditorState editorState = null!;
    static EngineBootstrap bootstrap = null!;

    static readonly object logGate = new();
    static readonly List<(DateTime Time, int Level, string Message)> logTail = new();

    public static void Install(EditorState state, EngineBootstrap boot) {
        editorState = state;
        bootstrap = boot;
        Debugging.OnMessage += (message, level) => {
            lock (logGate) {
                logTail.Add((DateTime.Now, level, message));
                if (logTail.Count > 600)
                    logTail.RemoveRange(0, 100);
            }
        };
    }

    public static object Dispatch(string method, JsonElement p) => method switch {
        "editor.status" => Status(),
        "scene.describe" => Describe(),
        "scene.save" => SceneSave(),
        "scene.open" => SceneOpen(RequireString(p, "path")),
        "entity.get" => EntityJson(Resolve(RequireString(p, "entity"))),
        "entity.create" => EntityCreate(p),
        "entity.delete" => EntityDelete(RequireString(p, "entity")),
        "component.add" => ComponentAdd(p),
        "component.remove" => ComponentRemove(p),
        "component.set" => ComponentSet(p),
        "select" => Select(RequireString(p, "entity")),
        "play.start" => PlayStart(),
        "play.stop" => Run(() => SceneManager.StopPlay()),
        "play.pause" => PlayPause(p),
        "play.step" => PlayStep(),
        "undo" => Run(EditorUndo.Undo),
        "redo" => Run(EditorUndo.Redo),
        "screenshot" => Screenshot(p),
        "console.tail" => ConsoleTail(p),
        "scripts.rebuild" => new { ok = bootstrap.ReloadGameScripts() },
        _ => throw new Exception($"unknown method '{method}' — methods: editor.status, scene.describe, " +
                                 "scene.save, scene.open, entity.get/create/delete, component.add/remove/set, " +
                                 "select, play.start/stop/pause/step, undo, redo, screenshot, console.tail, scripts.rebuild"),
    };

    // ---- queries -------------------------------------------------------------

    static object Status() {
        Scene scene = SceneManager.GetCurrentScene();
        return new {
            project = bootstrap.Project.RootPath,
            scene = SceneCommands.CurrentScenePath,
            sceneName = scene.Name,
            entities = scene.Entities.Count,
            isPlaying = SceneManager.IsPlaying,
            isPaused = SceneManager.IsPaused,
            dirty = EditorUndo.IsDirty,
            playBlocked = SceneManager.PlayBlocked?.Invoke(),
            selected = editorState.Selected?.Name,
            loading = SceneCommands.IsLoading ? SceneCommands.LoadingStatus : null,
        };
    }

    static object Describe() {
        Scene scene = SceneManager.GetCurrentScene();
        return new {
            name = scene.Name,
            sceneComponents = scene.SceneBehaviours.Select(b => b.GetType().Name).ToList(),
            entities = scene.Entities.Where(e => !e.IsDestroyed).Select(e => new {
                id = e.InstanceId.ToString("N"),
                name = e.Name,
                active = e.IsActive ? (bool?)null : false,
                parent = e.transform.Parent?.Entity?.Name,
                components = e.Behaviours.Select(b => b.GetType().Name).ToList(),
            }).ToList(),
        };
    }

    static object EntityJson(Entity e) => new {
        id = e.InstanceId.ToString("N"),
        name = e.Name,
        active = e.IsActive,
        tag = e.Tag,
        layer = e.Layer,
        transform = new {
            position = LiveJson(e.transform.Position),
            rotationEulerDegrees = LiveJson(e.transform.EulerAngles),
            scale = LiveJson(e.transform.Scale),
            parent = e.transform.Parent?.Entity?.Name,
        },
        components = e.Behaviours.Select(b => new {
            type = b.GetType().Name,
            enabled = b.IsEnabled,
            members = ComponentReflection.SerializableMembers(b.GetType())
                .ToDictionary(m => m.Name, m => LiveJson(SafeGet(m, b))),
        }).ToList(),
    };

    static object ConsoleTail(JsonElement p) {
        int count = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("count", out JsonElement c)
            ? c.GetInt32() : 50;
        lock (logGate) {
            return new {
                entries = logTail.TakeLast(count).Select(e => new {
                    time = e.Time.ToString("HH:mm:ss"),
                    level = e.Level switch { 2 => "error", 1 => "warning", _ => "info" },
                    message = e.Message,
                }).ToList(),
            };
        }
    }

    // ---- scene / play ----------------------------------------------------------

    static object SceneSave() {
        bool saved = SceneCommands.Save();
        return new { ok = saved, scene = SceneCommands.CurrentScenePath };
    }

    static object SceneOpen(string path) {
        SceneCommands.Open(path.Replace('\\', '/'));
        return new { opening = true, note = "poll editor.status until 'loading' is null" };
    }

    static object PlayStart() {
        string blocked = SceneManager.PlayBlocked?.Invoke();
        if (blocked is not null)
            throw new Exception($"play blocked: {blocked}");
        // Toolbar semantics: persist edits before play so a crash mid-play can't lose them.
        if (EditorUndo.IsDirty && !string.IsNullOrEmpty(SceneCommands.CurrentScenePath))
            SceneCommands.Save();
        SceneManager.StartPlay();
        editorState.MarkViewportDirty();
        return new { isPlaying = SceneManager.IsPlaying };
    }

    static object PlayPause(JsonElement p) {
        if (!SceneManager.IsPlaying)
            throw new Exception("not playing");
        SceneManager.IsPaused = p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("paused", out JsonElement v)
            ? !SceneManager.IsPaused
            : v.GetBoolean();
        return new { isPaused = SceneManager.IsPaused };
    }

    static object PlayStep() {
        if (!SceneManager.IsPlaying || !SceneManager.IsPaused)
            throw new Exception("step requires playing + paused");
        SceneManager.StepFrame();
        editorState.MarkViewportDirty();
        return new { stepped = true };
    }

    static object Run(Action action) {
        action();
        editorState.MarkViewportDirty();
        return new { ok = true };
    }

    static object Screenshot(JsonElement p) {
        string path = RequireString(p, "path");
        int settle = p.TryGetProperty("settleFrames", out JsonElement s) ? s.GetInt32() : 3;
        Screenshots.Capture(path, settle);
        editorState.MarkViewportDirty(); // make sure frames actually present for the capture
        return new { queued = true, path, note = "captures the editor window backbuffer" };
    }

    // ---- mutations -------------------------------------------------------------

    static object EntityCreate(JsonElement p) {
        string name = RequireString(p, "name");
        EditorUndo.Push($"Create {name} (remote)");
        Scene scene = SceneManager.GetCurrentScene();
        Entity entity = scene.CreateEntity(name);
        if (p.TryGetProperty("position", out JsonElement pos))
            entity.transform.Position = (Vector3)ConvertValue(typeof(Vector3), pos);
        if (p.TryGetProperty("parent", out JsonElement parent))
            entity.transform.SetParent(Resolve(parent.GetString()!).transform);
        Mutated();
        return new { id = entity.InstanceId.ToString("N"), name = entity.Name };
    }

    static object EntityDelete(string query) {
        Entity entity = Resolve(query);
        EditorUndo.Push($"Delete {entity.Name} (remote)");
        SceneManager.GetCurrentScene().DestroyEntity(entity);
        Mutated();
        return new { deleted = entity.Name };
    }

    static object ComponentAdd(JsonElement p) {
        Entity entity = Resolve(RequireString(p, "entity"));
        string typeName = RequireString(p, "type");
        Type type = ComponentRegistry.Resolve(typeName)
            ?? throw new Exception($"unknown component type '{typeName}'" + Hint(typeName,
                ComponentRegistry.Menu.Select(e => e.Type.Name)));
        EditorUndo.Push($"Add {type.Name} (remote)");
        Behaviour behaviour = entity.AddComponent(type);
        Mutated();
        return new { entity = entity.Name, component = behaviour.GetType().Name };
    }

    static object ComponentRemove(JsonElement p) {
        Entity entity = Resolve(RequireString(p, "entity"));
        Behaviour behaviour = FindComponent(entity, RequireString(p, "type"));
        EditorUndo.Push($"Remove {behaviour.GetType().Name} (remote)");
        entity.RemoveComponent(behaviour);
        Mutated();
        return new { entity = entity.Name, removed = behaviour.GetType().Name };
    }

    // component.set {entity, target, value} — target: name|active|tag|layer|transform.position|
    // transform.rotation (Euler degrees)|transform.scale|<Component>.<Member>, like `bal scene set`
    // but against the LIVE scene (typed conversion incl. asset loads through AssetDatabase).
    static object ComponentSet(JsonElement p) {
        Entity entity = Resolve(RequireString(p, "entity"));
        string target = RequireString(p, "target");
        JsonElement value = p.TryGetProperty("value", out JsonElement v)
            ? v : throw new Exception("missing 'value'");

        EditorUndo.Push($"Set {target} (remote)");
        object? written;
        switch (target.ToLowerInvariant()) {
            case "name": entity.Name = value.GetString() ?? ""; written = entity.Name; break;
            case "active": entity.SetActive(value.GetBoolean()); written = entity.IsActive; break;
            case "tag": entity.Tag = value.GetString(); written = entity.Tag; break;
            case "layer": entity.Layer = value.GetInt32(); written = entity.Layer; break;
            case "transform.position":
                entity.transform.Position = (Vector3)ConvertValue(typeof(Vector3), value);
                written = LiveJson(entity.transform.Position); break;
            case "transform.rotation":
                entity.transform.EulerAngles = (Vector3)ConvertValue(typeof(Vector3), value);
                written = LiveJson(entity.transform.EulerAngles); break;
            case "transform.scale":
                entity.transform.Scale = (Vector3)ConvertValue(typeof(Vector3), value);
                written = LiveJson(entity.transform.Scale); break;
            default: {
                int dot = target.IndexOf('.');
                if (dot <= 0 || dot == target.Length - 1)
                    throw new Exception($"unknown target '{target}' — name|active|tag|layer|transform.*|<Component>.<Member>");
                Behaviour behaviour = FindComponent(entity, target[..dot]);
                string memberName = target[(dot + 1)..];
                var members = ComponentReflection.SerializableMembers(behaviour.GetType()).ToList();
                MemberInfo member = members.FirstOrDefault(m =>
                        string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new Exception($"{behaviour.GetType().Name} has no serializable member '{memberName}'"
                        + Hint(memberName, members.Select(m => m.Name)));
                object converted = ConvertValue(ComponentReflection.MemberType(member), value);
                ComponentReflection.SetValue(member, behaviour, converted);
                written = LiveJson(SafeGet(member, behaviour));
                break;
            }
        }
        Mutated();
        return new { entity = entity.Name, target, value = written };
    }

    static object Select(string query) {
        Entity entity = Resolve(query);
        editorState.Selected = entity;
        editorState.MarkViewportDirty();
        return new { selected = entity.Name, id = entity.InstanceId.ToString("N") };
    }

    // ---- shared ----------------------------------------------------------------

    static void Mutated() {
        EditorUndo.MarkDirty();
        editorState.MarkViewportDirty();
    }

    // Same addressing rules as the bal CLI: exact id, unique id prefix, exact name, unique
    // name substring (case-insensitive).
    static Entity Resolve(string query) {
        var entities = SceneManager.GetCurrentScene().Entities.Where(e => !e.IsDestroyed).ToList();

        foreach (Entity e in entities)
            if (string.Equals(e.InstanceId.ToString("N"), query, StringComparison.OrdinalIgnoreCase))
                return e;
        if (query.Length >= 6) {
            var byPrefix = entities.Where(e =>
                e.InstanceId.ToString("N").StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byPrefix.Count == 1) return byPrefix[0];
        }
        var byName = entities.Where(e => string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1)
            throw new Exception($"entity name '{query}' is ambiguous ({byName.Count} share it) — use an id: "
                + string.Join(", ", byName.Select(e => e.InstanceId.ToString("N"))));
        var bySubstring = entities.Where(e =>
            e.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (bySubstring.Count == 1) return bySubstring[0];

        throw new Exception($"no entity matches '{query}'" + Hint(query, entities.Select(e => e.Name)));
    }

    static Behaviour FindComponent(Entity entity, string spec) {
        string typeName = spec;
        int? index = null;
        int at = spec.IndexOf('@');
        if (at >= 0) { typeName = spec[..at]; index = int.Parse(spec[(at + 1)..]); }

        var matches = entity.Behaviours.Where(b =>
            string.Equals(b.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            throw new Exception($"'{entity.Name}' has no {typeName} component"
                + Hint(typeName, entity.Behaviours.Select(b => b.GetType().Name)));
        if (matches.Count > 1 && index is null)
            throw new Exception($"'{entity.Name}' has {matches.Count} {typeName} components — address as {typeName}@0..{matches.Count - 1}");
        if (index is int i && i >= matches.Count)
            throw new Exception($"'{entity.Name}' has only {matches.Count} {typeName} component(s)");
        return matches[index ?? 0];
    }

    // JSON value -> live typed value. Accepts the natural JSON shape per type plus the CLI's
    // string forms ("1,2,3" vectors, Euler-degree rotations, "Assets/..." or "guid:" asset refs).
    static object ConvertValue(Type memberType, JsonElement value) {
        Type t = Nullable.GetUnderlyingType(memberType) ?? memberType;

        if (t == typeof(string)) return value.GetString()!;
        if (t == typeof(bool))
            return value.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => bool.Parse(value.GetString()!),
            };
        if (t.IsEnum) {
            string name = value.GetString()!;
            foreach (string candidate in Enum.GetNames(t))
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    return Enum.Parse(t, candidate);
            throw new Exception($"'{name}' is not a {t.Name} — valid: {string.Join(", ", Enum.GetNames(t))}");
        }
        if (t.IsPrimitive || t == typeof(decimal)) {
            double d = value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : double.Parse(value.GetString()!, CultureInfo.InvariantCulture);
            return Convert.ChangeType(d, t, CultureInfo.InvariantCulture);
        }
        if (t == typeof(Vector2)) { float[] c = Components(value, 2); return new Vector2(c[0], c[1]); }
        if (t == typeof(Vector3)) { float[] c = Components(value, 3); return new Vector3(c[0], c[1], c[2]); }
        if (t == typeof(Quaternion)) { // Euler degrees in, engine convention
            float[] c = Components(value, 3);
            return Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(c[0]), MathHelper.DegreesToRadians(c[1]), MathHelper.DegreesToRadians(c[2]));
        }
        if (typeof(BObject).IsAssignableFrom(t)) {
            string reference = value.GetString() ?? throw new Exception("asset refs are strings (Assets/... or guid:...)");
            object? asset = typeof(AssetDatabase).GetMethod("LoadRef")!.MakeGenericMethod(t)
                .Invoke(null, [reference]);
            return asset ?? throw new Exception($"asset '{reference}' failed to load (see console)");
        }
        if (t == typeof(AnimationCurve)) return AnimationCurve.Parse(value.GetString()!);
        if (t == typeof(ColorGradient)) return ColorGradient.Parse(value.GetString()!);
        throw new Exception($"members of type {t.Name} can't be set remotely yet");
    }

    // "x,y,z" string, [x,y,z] array, or {x:..,y:..,z:..} object.
    static float[] Components(JsonElement value, int count) {
        var result = new float[count];
        switch (value.ValueKind) {
            case JsonValueKind.String: {
                string[] parts = value.GetString()!.Trim().Trim('(', ')').Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != count) throw new Exception($"expected {count} components");
                for (int i = 0; i < count; i++) result[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
                return result;
            }
            case JsonValueKind.Array: {
                if (value.GetArrayLength() != count) throw new Exception($"expected {count} components");
                int i = 0;
                foreach (JsonElement item in value.EnumerateArray()) result[i++] = item.GetSingle();
                return result;
            }
            case JsonValueKind.Object: {
                string[] keys = ["x", "y", "z", "w"];
                for (int i = 0; i < count; i++)
                    result[i] = value.TryGetProperty(keys[i], out JsonElement c)
                        ? c.GetSingle() : throw new Exception($"missing '{keys[i]}'");
                return result;
            }
            default:
                throw new Exception($"expected a {count}-component vector ('x,y,z', [..] or {{x,y,z}})");
        }
    }

    static object? SafeGet(MemberInfo member, object target) {
        try { return ComponentReflection.GetValue(member, target); }
        catch (Exception ex) { return $"<threw: {ex.InnerException?.Message ?? ex.Message}>"; }
    }

    // Live value -> JSON-friendly (asset refs become their project paths — names over guids).
    static object? LiveJson(object? value) => value switch {
        null => null,
        Vector2 v => new { x = v.X, y = v.Y },
        Vector3 v => new { x = v.X, y = v.Y, z = v.Z },
        Quaternion q => new { x = q.X, y = q.Y, z = q.Z, w = q.W },
        AnimationCurve c => c.ToCompactString(),
        ColorGradient g => g.ToCompactString(),
        BEvent => "(event)",
        BObject asset => AssetDatabase.TryGetAssetGuid(asset, out Guid guid)
            ? AssetDatabase.GuidToAssetPath(guid) ?? $"guid:{guid:N}"
            : asset.ToString(),
        string or bool or int or uint or long or float or double or byte or short => value,
        Enum e => e.ToString(),
        _ => value.ToString(),
    };

    static string Hint(string typed, IEnumerable<string?> candidates) {
        string? best = candidates.FirstOrDefault(c =>
            c?.Contains(typed, StringComparison.OrdinalIgnoreCase) == true
            || typed.Contains(c ?? "\0", StringComparison.OrdinalIgnoreCase));
        return best is null ? "" : $" — did you mean '{best}'?";
    }

    static string RequireString(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new Exception($"missing string param '{name}'");
}
