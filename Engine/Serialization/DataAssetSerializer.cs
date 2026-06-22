using System.Globalization;
using System.Reflection;
using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Serialization;

public static class DataAssetSerializer {
    public sealed class Document {
        public int Version { get; set; } = 1;
        public string Type { get; set; }
        public Dictionary<string, object> Members { get; set; } = new();
    }

    public static string Serialize(DataAsset asset) {
        var doc = new Document { Type = ComponentRegistry.DataAssetNameOf(asset.GetType()) };

        foreach (MemberInfo member in ComponentReflection.SerializableMembers(asset.GetType())) {
            object value = ComponentReflection.GetValue(member, asset);
            object serialized = SerializeValue(value);
            if (serialized is not null)
                doc.Members[CamelCase(member.Name)] = serialized;
        }

        return SceneYaml.Serializer.Serialize(doc);
    }

    static object SerializeValue(object value) {
        if (value is null)
            return null;
        if (value is AnimationCurve curve)
            return curve.ToCompactString();
        if (value is ColorGradient gradient)
            return gradient.ToCompactString();
        if (value is BObject asset)
            return AssetDatabase.TryGetAssetGuid(asset, out Guid guid) ? AssetRef.FromGuid(guid) : null;
        return value;
    }

    public static DataAsset Deserialize(string yaml, Type expectedType) {
        Document doc = SceneYaml.Deserializer.Deserialize<Document>(yaml);
        if (doc is null)
            return null;

        Type type = ComponentRegistry.ResolveDataAsset(doc.Type);
        if (type is null) {
            Debugging.LogError($"Unknown DataAsset type '{doc.Type}'. Is the script compiled?");
            return null;
        }
        if (expectedType is not null && !expectedType.IsAssignableFrom(type)) {
            Debugging.LogError($"DataAsset is a {type.Name} but {expectedType.Name} was requested.");
            return null;
        }

        var asset = (DataAsset)Activator.CreateInstance(type);
        ApplyMembers(asset, type, doc.Members);
        asset.OnLoaded();
        return asset;
    }

    static void ApplyMembers(object target, Type type, Dictionary<string, object> members) {
        if (members is null)
            return;

        var byName = ComponentReflection.SerializableMembers(type)
            .ToDictionary(m => CamelCase(m.Name), StringComparer.OrdinalIgnoreCase);

        foreach ((string name, object raw) in members) {
            if (!byName.TryGetValue(name, out MemberInfo member))
                continue;
            object value = DeserializeValue(raw, ComponentReflection.MemberType(member));
            if (value is not null)
                ComponentReflection.SetValue(member, target, value);
        }
    }

    static object DeserializeValue(object raw, Type targetType) {
        if (raw is null)
            return null;

        if (typeof(BObject).IsAssignableFrom(targetType))
            return raw is string reference ? LoadAsset(reference, targetType) : null;

        if (targetType == typeof(AnimationCurve))
            return raw is string curveStr ? AnimationCurve.Parse(curveStr) : null;

        if (targetType == typeof(ColorGradient))
            return raw is string gradientStr ? ColorGradient.Parse(gradientStr) : null;

        if (targetType.IsInstanceOfType(raw))
            return raw;

        return Coerce(raw, targetType);
    }

    static object LoadAsset(string reference, Type targetType) {
        MethodInfo loadRef = typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.LoadRef))!
            .MakeGenericMethod(targetType);
        return loadRef.Invoke(null, [reference]);
    }

    static object Coerce(object raw, Type targetType) {
        try {
            if (targetType.IsEnum)
                return Enum.Parse(targetType, raw.ToString()!, ignoreCase: true);
            return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
        }
        catch {
            return null;
        }
    }

    static string CamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];
}
