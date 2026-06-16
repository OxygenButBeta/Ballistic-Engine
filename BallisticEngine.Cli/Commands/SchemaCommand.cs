using System.Reflection;
using BallisticEngine.Serialization;

namespace BallisticEngine.Cli.Commands;

// `bal schema` — emits the JSON component catalog: every component type an agent can put in a scene,
// with its registry name (what goes in the .scene file), menu category, and editable members (name +
// type + range/tooltip). This is the agent's "what can I author" reference, generated from reflection
// so it's never out of date. Roadmap layer 1, verb 1.
//
// Categories: "component" (Behaviour — entity components), "scene" (SceneBehaviour — scene-wide),
// "volume" (VolumeComponent — post-process overrides), "data" (DataAsset — .asset types).
internal sealed class SchemaCommand : ICommand {
    public string Name => "schema";
    public string Summary => "Print the JSON catalog of all components and their members.";
    public string Usage =>
        "Usage: bal schema [--type <Name>] [--category component|scene|volume|data]\n" +
        "  --type      only the named component (registry name, e.g. 'Rigidbody')\n" +
        "  --category  only one category";

    public int Run(string[] args) {
        string? onlyType = null;
        string? onlyCategory = null;
        for (var i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--type": onlyType = Next(args, ref i, "--type"); break;
                case "--category": onlyCategory = Next(args, ref i, "--category"); break;
                default: throw new CliUsageException($"unexpected argument '{args[i]}'");
            }
        }

        // Discover every component type from the ENGINE assembly (no game scripts — schema is the
        // engine catalog). GL-free: ComponentRegistry.Build only reflects, never touches OpenGL.
        ComponentRegistry.Build(typeof(SceneManager).Assembly);

        var components = new List<ComponentSchema>();
        if (Include(onlyCategory, "component"))
            foreach (ComponentEntry e in ComponentRegistry.Menu)
                AddIfMatch(components, e, "component", onlyType);
        if (Include(onlyCategory, "scene"))
            foreach (ComponentEntry e in ComponentRegistry.SceneMenu)
                AddIfMatch(components, e, "scene", onlyType);
        if (Include(onlyCategory, "volume"))
            foreach (ComponentEntry e in ComponentRegistry.VolumeMenu)
                AddIfMatch(components, e, "volume", onlyType);
        if (Include(onlyCategory, "data"))
            foreach (ComponentEntry e in ComponentRegistry.DataAssetMenu)
                AddIfMatch(components, e, "data", onlyType);

        if (onlyType is not null && components.Count == 0)
            throw new Exception($"no component named '{onlyType}' (try 'bal schema' for the full list)");

        components.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        Json.Write(new SchemaResult(components.Count, components));
        return 0;
    }

    static void AddIfMatch(List<ComponentSchema> outList, ComponentEntry e, string category, string? onlyType) {
        string regName = e.Type.Name;
        if (onlyType is not null && !string.Equals(regName, onlyType, StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(e.DisplayName, onlyType, StringComparison.OrdinalIgnoreCase))
            return;
        outList.Add(BuildSchema(e, category));
    }

    static ComponentSchema BuildSchema(ComponentEntry entry, string category) {
        var members = new List<MemberSchema>();

        if (category is "volume") {
            // VolumeComponents expose VolumeParameter fields, not the usual serializable members.
            foreach (VolumeComponent.ParameterSlot slot in VolumeParameters(entry.Type))
                members.Add(new MemberSchema(slot.Name, slot.Parameter.GetType().Name.Replace("Parameter", ""), null, null, null));
        }
        else {
            foreach (MemberInfo m in ComponentReflection.SerializableMembers(entry.Type)) {
                Type t = ComponentReflection.MemberType(m);
                var range = m.GetCustomAttribute<RangeAttribute>();
                var tooltip = m.GetCustomAttribute<TooltipAttribute>();
                members.Add(new MemberSchema(
                    m.Name,
                    FriendlyType(t),
                    range is null ? null : range.Min,
                    range is null ? null : range.Max,
                    tooltip?.Text));
            }
        }

        members.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return new ComponentSchema(entry.Type.Name, category,
            string.IsNullOrEmpty(entry.Menu) ? null : entry.Menu, members);
    }

    // Builds a throwaway instance to read its parameter slots (VolumeComponent discovers them by
    // reflection in its constructor). Every registered VolumeComponent has a public parameterless ctor.
    static IReadOnlyList<VolumeComponent.ParameterSlot> VolumeParameters(Type type) {
        var instance = (VolumeComponent)Activator.CreateInstance(type)!;
        return instance.Parameters;
    }

    // A short, agent-friendly type label (the names agents write in YAML thinking): "Vector3",
    // "float", "AssetRef:Texture2D" for asset members, "Enum:LightType" for enums.
    static string FriendlyType(Type t) {
        if (t == typeof(float)) return "float";
        if (t == typeof(int)) return "int";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t.IsEnum) return "enum:" + t.Name + "(" + string.Join("|", Enum.GetNames(t)) + ")";
        if (typeof(BObject).IsAssignableFrom(t)) return "asset:" + t.Name;
        return t.Name;
    }

    static bool Include(string? onlyCategory, string category) =>
        onlyCategory is null || string.Equals(onlyCategory, category, StringComparison.OrdinalIgnoreCase);

    static string Next(string[] args, ref int i, string flag) {
        if (i + 1 >= args.Length) throw new CliUsageException($"{flag} needs a value");
        return args[++i];
    }

    // ---- JSON shapes ----
    record SchemaResult(int count, List<ComponentSchema> components);
    record ComponentSchema(string name, string category, string? menu, List<MemberSchema> members);
    record MemberSchema(string name, string type, float? min, float? max, string? tooltip);
}
