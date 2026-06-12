using BallisticEngine.Serialization;
using OpenTK.Mathematics;

namespace BallisticEngine.Cli.Commands;

// `bal scene <action>` — block-level scene CRUD keyed by stable entity ids (GL-free, document
// level: parse YAML -> mutate -> validated re-serialize). Agents use this instead of hand-editing
// scene YAML: every write is validated at parse time (typed member values, registry-resolved
// component types, did-you-mean errors) and the file only changes after the whole edit succeeded.
//
// Entities are addressed by name, unique name substring, id, or id prefix. Components repeated on
// one entity are addressed as Type@0, Type@1, ... New ids are MINTED HERE (agents never invent
// identifiers); asset refs are written path-form by policy.
internal sealed class SceneCommand : ICommand {
    public string Name => "scene";
    public string Summary => "Read and edit a .scene: get/set/add/remove/find.";
    public string Usage =>
        """
        Usage: bal scene <action> <scene.scene> ...
          get        <scene> [entity]                 entity detail, or the flat entity list
          set        <scene> <entity> <target> <value>
                     target: name|active|tag|layer|transform.position|transform.rotation (Euler deg)
                             |transform.scale|transform.parent|<Component>.<Member>
          add-entity <scene> <name> [--parent <entity>] [--position x,y,z] [--rotation x,y,z] [--scale x,y,z]
          add-component    <scene> <entity|--scene> <Type> [--set Member=value ...]
          remove-entity    <scene> <entity>           (removes its children too)
          remove-component <scene> <entity> <Type[@n]>
          find       <scene> (--type <Component> | --name <text>)
        """;

    public int Run(string[] args) {
        if (args.Length == 0) throw new CliUsageException("expected an action (get/set/add-entity/...)");
        string action = args[0];
        if (args.Length < 2) throw new CliUsageException($"'{action}' needs a scene path");
        string scenePath = args[1];

        SceneFile.BuildRegistry(scenePath);
        SceneDocument doc = SceneFile.Load(scenePath);
        string[] rest = args[2..];

        return action switch {
            "get" => Get(doc, rest),
            "set" => Set(scenePath, doc, rest),
            "add-entity" => AddEntity(scenePath, doc, rest),
            "add-component" => AddComponent(scenePath, doc, rest),
            "remove-entity" => RemoveEntity(scenePath, doc, rest),
            "remove-component" => RemoveComponent(scenePath, doc, rest),
            "find" => Find(doc, rest),
            _ => throw new CliUsageException($"unknown action '{action}'"),
        };
    }

    // ---- get ----------------------------------------------------------------

    static int Get(SceneDocument doc, string[] args) {
        if (args.Length > 1) throw new CliUsageException("get takes at most one entity");
        SceneFile.NormalizeDocument(doc); // numbers as numbers in the JSON output

        if (args.Length == 0) {
            var byId = EntitiesById(doc);
            Json.Write(new {
                name = doc.Name,
                entities = (doc.Entities ?? []).Select(e => new {
                    id = e.Id,
                    name = e.Name,
                    parent = ParentName(byId, e),
                    components = (e.Components ?? []).Select(c => c.Type).ToList(),
                }).ToList(),
            });
            return 0;
        }

        EntityDocument entity = SceneFile.ResolveEntity(doc, args[0]);
        Json.Write(EntityJson(doc, entity));
        return 0;
    }

    static object EntityJson(SceneDocument doc, EntityDocument e) {
        var byId = EntitiesById(doc);
        return new {
            id = e.Id,
            name = e.Name,
            active = e.IsActive,
            tag = e.Tag,
            layer = e.Layer,
            transform = new {
                position = SceneFile.ToJsonValue(e.Transform?.Position ?? Vector3.Zero),
                rotationEulerDegrees = SceneFile.ToJsonValue(
                    SceneFile.QuaternionToEulerDegrees(e.Transform?.Rotation ?? Quaternion.Identity)),
                scale = SceneFile.ToJsonValue(e.Transform?.Scale ?? Vector3.One),
                parent = e.Transform?.Parent,
                parentName = ParentName(byId, e),
            },
            components = (e.Components ?? []).Select(c => new {
                type = c.Type,
                enabled = c.Enabled,
                members = (c.Members ?? new()).ToDictionary(kv => kv.Key, kv => SceneFile.ToJsonValue(kv.Value)),
            }).ToList(),
        };
    }

    // ---- set ----------------------------------------------------------------

    static int Set(string scenePath, SceneDocument doc, string[] args) {
        if (args.Length < 3) throw new CliUsageException("set needs <entity> <target> <value>");
        EntityDocument entity = SceneFile.ResolveEntity(doc, args[0]);
        string target = args[1];
        string value = string.Join(" ", args[2..]); // vectors may arrive as "1, 2, 3"

        object? written;
        switch (target.ToLowerInvariant()) {
            case "name":
                entity.Name = value; written = value; break;
            case "active":
                entity.IsActive = SceneFile.ParseBool(value); written = entity.IsActive; break;
            case "tag":
                entity.Tag = value is "none" or "null" ? null : value; written = entity.Tag; break;
            case "layer":
                entity.Layer = int.TryParse(value, out int layer) ? layer : throw new Exception($"'{value}' is not a layer index");
                written = layer; break;
            case "transform.position":
                (entity.Transform ??= new()).Position = SceneFile.ParseVec3(value);
                written = SceneFile.ToJsonValue(entity.Transform.Position); break;
            case "transform.rotation":
                (entity.Transform ??= new()).Rotation = SceneFile.EulerDegreesToQuaternion(SceneFile.ParseVec3(value));
                written = SceneFile.ToJsonValue(SceneFile.ParseVec3(value)); break;
            case "transform.scale":
                (entity.Transform ??= new()).Scale = SceneFile.ParseVec3(value);
                written = SceneFile.ToJsonValue(entity.Transform.Scale); break;
            case "transform.parent":
                written = SetParent(doc, entity, value); break;
            default:
                written = SetComponentMember(scenePath, entity, target, value); break;
        }

        SceneFile.Save(scenePath, doc);
        Json.Write(new { ok = true, entity = new { id = entity.Id, name = entity.Name }, target, value = written });
        return 0;
    }

    static object? SetParent(SceneDocument doc, EntityDocument entity, string value) {
        entity.Transform ??= new();
        if (value is "none" or "null") {
            entity.Transform.Parent = null;
            return null;
        }
        EntityDocument parent = SceneFile.ResolveEntity(doc, value);
        if (ReferenceEquals(parent, entity))
            throw new Exception("an entity can't be its own parent");
        // Reparenting under one's own descendant would orphan the subtree into a cycle.
        var byId = EntitiesById(doc);
        for (string? p = parent.Id; p is not null; p = byId.TryGetValue(p, out EntityDocument? up) ? up.Transform?.Parent : null)
            if (p == entity.Id)
                throw new Exception($"'{parent.Name}' is a descendant of '{entity.Name}' — reparenting would create a cycle");
        entity.Transform.Parent = parent.Id;
        return new { id = parent.Id, name = parent.Name };
    }

    static object? SetComponentMember(string scenePath, EntityDocument entity, string target, string value) {
        int dot = target.IndexOf('.');
        if (dot <= 0 || dot == target.Length - 1)
            throw new CliUsageException(
                $"unknown target '{target}' — expected name|active|tag|layer|transform.*|<Component>.<Member>");
        string componentSpec = target[..dot];
        string memberName = target[(dot + 1)..];

        (ComponentDocument comp, Type? compType) = FindComponent(entity, componentSpec);

        object? parsed;
        string key;
        if (compType is not null) {
            var members = ComponentReflection.SerializableMembers(compType).ToList();
            var member = members.FirstOrDefault(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (member is null) {
                string? hint = Suggest.Closest(memberName, members.Select(m => m.Name));
                throw new Exception($"{comp.Type} has no serializable member '{memberName}'"
                    + (hint is null ? "" : $" — did you mean '{hint}'?"));
            }
            parsed = SceneFile.ParseMemberValue(ComponentReflection.MemberType(member), value, scenePath);
            key = SceneFile.CamelCase(member.Name);
        }
        else {
            // Game component without a loadable script dll: write a best-effort scalar (the engine
            // coerces on load) and say so.
            Console.Error.WriteLine(
                $"bal scene: '{comp.Type}' is not in the component registry (game scripts not compiled?) — writing the value untyped");
            parsed = SceneFile.ParseLoose(value);
            key = SceneFile.CamelCase(memberName);
        }

        comp.Members ??= new();
        if (parsed is null) comp.Members.Remove(key);
        else comp.Members[key] = parsed;
        return SceneFile.ToJsonValue(parsed);
    }

    // ---- add ----------------------------------------------------------------

    static int AddEntity(string scenePath, SceneDocument doc, string[] args) {
        string? name = null, parent = null, position = null, rotation = null, scale = null;
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--parent": parent = Next(args, ref i, "--parent"); break;
                case "--position": position = Next(args, ref i, "--position"); break;
                case "--rotation": rotation = Next(args, ref i, "--rotation"); break;
                case "--scale": scale = Next(args, ref i, "--scale"); break;
                default:
                    if (name is null) name = args[i];
                    else throw new CliUsageException($"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(name)) throw new CliUsageException("add-entity needs a name");

        var entity = new EntityDocument {
            Id = Guid.NewGuid().ToString("N"), // ids are minted by the tool, never by the agent
            Name = name,
            Transform = new TransformDocument {
                Position = position is null ? Vector3.Zero : SceneFile.ParseVec3(position),
                Rotation = rotation is null ? Quaternion.Identity : SceneFile.EulerDegreesToQuaternion(SceneFile.ParseVec3(rotation)),
                Scale = scale is null ? Vector3.One : SceneFile.ParseVec3(scale),
                Parent = parent is null ? null : SceneFile.ResolveEntity(doc, parent).Id,
            },
        };
        (doc.Entities ??= new()).Add(entity);

        SceneFile.Save(scenePath, doc);
        Json.Write(new { ok = true, id = entity.Id, name = entity.Name, parent = entity.Transform.Parent });
        return 0;
    }

    static int AddComponent(string scenePath, SceneDocument doc, string[] args) {
        string? entityQuery = null, typeName = null;
        bool sceneWide = false;
        var sets = new List<string>();
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--scene": sceneWide = true; break;
                case "--set": sets.Add(Next(args, ref i, "--set")); break;
                default:
                    if (!sceneWide && entityQuery is null) entityQuery = args[i];
                    else if (typeName is null) typeName = args[i];
                    else throw new CliUsageException($"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (typeName is null) throw new CliUsageException("add-component needs a component type");
        if (!sceneWide && entityQuery is null) throw new CliUsageException("add-component needs an entity (or --scene)");

        Type? type = sceneWide
            ? ResolveFlexible(typeName, ComponentRegistry.SceneMenu, ComponentRegistry.ResolveScene, out string canonical)
            : ResolveFlexible(typeName, ComponentRegistry.Menu, ComponentRegistry.Resolve, out canonical);
        if (type is null) {
            var menu = sceneWide ? ComponentRegistry.SceneMenu : ComponentRegistry.Menu;
            string? hint = Suggest.Closest(typeName, menu.Select(e => e.Type.Name));
            throw new Exception($"unknown {(sceneWide ? "scene " : "")}component type '{typeName}'"
                + (hint is null ? "" : $" — did you mean '{hint}'?"));
        }

        var comp = new ComponentDocument { Type = canonical, Id = Guid.NewGuid().ToString("N"), Enabled = true };
        foreach (string pair in sets) {
            int eq = pair.IndexOf('=');
            if (eq <= 0) throw new CliUsageException($"--set expects Member=value (got '{pair}')");
            string memberName = pair[..eq];
            var members = ComponentReflection.SerializableMembers(type).ToList();
            var member = members.FirstOrDefault(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (member is null) {
                string? hint = Suggest.Closest(memberName, members.Select(m => m.Name));
                throw new Exception($"{canonical} has no serializable member '{memberName}'"
                    + (hint is null ? "" : $" — did you mean '{hint}'?"));
            }
            object? value = SceneFile.ParseMemberValue(ComponentReflection.MemberType(member), pair[(eq + 1)..], scenePath);
            if (value is not null)
                comp.Members[SceneFile.CamelCase(member.Name)] = value;
        }

        object owner;
        if (sceneWide) {
            (doc.SceneComponents ??= new()).Add(comp);
            owner = new { scene = doc.Name };
        }
        else {
            EntityDocument entity = SceneFile.ResolveEntity(doc, entityQuery!);
            (entity.Components ??= new()).Add(comp);
            owner = new { id = entity.Id, name = entity.Name };
        }

        SceneFile.Save(scenePath, doc);
        Json.Write(new { ok = true, component = canonical, id = comp.Id, owner });
        return 0;
    }

    // ---- remove -------------------------------------------------------------

    static int RemoveEntity(string scenePath, SceneDocument doc, string[] args) {
        if (args.Length != 1) throw new CliUsageException("remove-entity needs exactly one entity");
        EntityDocument entity = SceneFile.ResolveEntity(doc, args[0]);

        // Editor semantics: deleting an entity deletes its subtree (a dangling parent id would
        // silently flatten the hierarchy on load).
        var doomedIds = new HashSet<string>(StringComparer.Ordinal) { entity.Id! };
        bool grew = true;
        while (grew) {
            grew = false;
            foreach (EntityDocument e in doc.Entities ?? []) {
                string? p = e.Transform?.Parent;
                if (e.Id is not null && p is not null && doomedIds.Contains(p) && doomedIds.Add(e.Id))
                    grew = true;
            }
        }

        var removed = (doc.Entities ?? []).Where(e => e.Id is not null && doomedIds.Contains(e.Id)).ToList();
        doc.Entities?.RemoveAll(e => e.Id is not null && doomedIds.Contains(e.Id));

        SceneFile.Save(scenePath, doc);
        Json.Write(new { ok = true, removed = removed.Select(e => new { id = e.Id, name = e.Name }).ToList() });
        return 0;
    }

    static int RemoveComponent(string scenePath, SceneDocument doc, string[] args) {
        if (args.Length != 2) throw new CliUsageException("remove-component needs <entity> <Type[@n]>");
        EntityDocument entity = SceneFile.ResolveEntity(doc, args[0]);
        (ComponentDocument comp, _) = FindComponent(entity, args[1]);
        entity.Components!.Remove(comp);

        SceneFile.Save(scenePath, doc);
        Json.Write(new { ok = true, removed = comp.Type, entity = new { id = entity.Id, name = entity.Name } });
        return 0;
    }

    // ---- find ---------------------------------------------------------------

    static int Find(SceneDocument doc, string[] args) {
        string? byType = null, byName = null;
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--type": byType = Next(args, ref i, "--type"); break;
                case "--name": byName = Next(args, ref i, "--name"); break;
                default: throw new CliUsageException($"unexpected argument '{args[i]}'");
            }
        }
        if ((byType is null) == (byName is null))
            throw new CliUsageException("find needs exactly one of --type or --name");

        var matches = (doc.Entities ?? []).Where(e => byType is not null
                ? (e.Components ?? []).Any(c => string.Equals(c.Type, byType, StringComparison.OrdinalIgnoreCase))
                : e.Name?.Contains(byName!, StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => new {
                id = e.Id,
                name = e.Name,
                components = (e.Components ?? []).Select(c => c.Type).ToList(),
            })
            .ToList();

        Json.Write(new { count = matches.Count, matches });
        return 0;
    }

    // ---- shared -------------------------------------------------------------

    // Components repeated on one entity (e.g. two BoxColliders) are addressed as Type@0, Type@1...
    static (ComponentDocument comp, Type? type) FindComponent(EntityDocument entity, string spec) {
        string typeName = spec;
        int? index = null;
        int at = spec.IndexOf('@');
        if (at >= 0) {
            typeName = spec[..at];
            if (!int.TryParse(spec[(at + 1)..], out int n) || n < 0)
                throw new CliUsageException($"bad component index in '{spec}'");
            index = n;
        }

        var matches = (entity.Components ?? [])
            .Where(c => string.Equals(c.Type, typeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) {
            string? hint = Suggest.Closest(typeName, (entity.Components ?? []).Select(c => c.Type));
            throw new Exception($"entity '{entity.Name}' has no {typeName} component"
                + (hint is null ? "" : $" — did you mean '{hint}'?"));
        }
        if (matches.Count > 1 && index is null)
            throw new Exception($"entity '{entity.Name}' has {matches.Count} {typeName} components — " +
                                $"address one as {typeName}@0..{matches.Count - 1}");
        if (index is int idx && idx >= matches.Count)
            throw new Exception($"entity '{entity.Name}' has only {matches.Count} {typeName} component(s)");

        ComponentDocument comp = matches[index ?? 0];
        return (comp, comp.Type is null ? null : ComponentRegistry.Resolve(comp.Type));
    }

    static Type? ResolveFlexible(string name, IReadOnlyList<ComponentEntry> menu,
        Func<string, Type?> resolve, out string canonical) {
        Type? t = resolve(name);
        if (t is null)
            foreach (ComponentEntry e in menu)
                if (string.Equals(e.Type.Name, name, StringComparison.OrdinalIgnoreCase)) { t = e.Type; break; }
        canonical = t?.Name ?? name;
        return t;
    }

    static Dictionary<string, EntityDocument> EntitiesById(SceneDocument doc) {
        var byId = new Dictionary<string, EntityDocument>(StringComparer.Ordinal);
        foreach (EntityDocument e in doc.Entities ?? [])
            if (!string.IsNullOrEmpty(e.Id))
                byId[e.Id] = e;
        return byId;
    }

    static string? ParentName(Dictionary<string, EntityDocument> byId, EntityDocument e) {
        string? p = e.Transform?.Parent;
        return p is not null && byId.TryGetValue(p, out EntityDocument? parent) ? parent.Name : null;
    }

    static string Next(string[] args, ref int i, string flag) =>
        ++i < args.Length ? args[i] : throw new CliUsageException($"{flag} needs a value");
}
