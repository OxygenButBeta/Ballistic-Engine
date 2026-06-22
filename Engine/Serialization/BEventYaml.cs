using System.Globalization;
using BallisticEngine.AssetPipeline;

namespace BallisticEngine.Serialization;

public static class BEventYaml {
    public static object Serialize(BEvent evt) {
        if (evt is null || evt.PersistentListeners.Count == 0)
            return null;

        var list = new List<object>(evt.PersistentListeners.Count);
        foreach (PersistentListener listener in evt.PersistentListeners) {
            var map = new Dictionary<string, object> {
                ["target"] = listener.TargetId.ToString("N"),
                ["method"] = listener.MethodName,
                ["mode"] = listener.Mode.ToString(),
            };
            if (listener.Mode == PersistentListener.CallMode.Static && listener.StaticArgumentType is not null) {
                map["argType"] = listener.StaticArgumentType.FullName;
                object serializedArg = SerializeArg(listener.StaticArgument);
                if (serializedArg is not null)
                    map["arg"] = serializedArg;
            }
            list.Add(map);
        }
        return list;
    }

    public static void Deserialize(object raw, BEvent into) {
        if (into is null)
            return;
        into.PersistentListeners.Clear();
        if (raw is not System.Collections.IEnumerable items || raw is string)
            return;

        foreach (object item in items) {
            if (ToStringKeyedMap(item) is not { } map)
                continue;

            var listener = new PersistentListener();

            if (TryGet(map, "target", out object t) && Guid.TryParseExact(AsString(t), "N", out Guid targetId))
                listener.TargetId = targetId;
            if (TryGet(map, "method", out object m))
                listener.MethodName = AsString(m);
            if (TryGet(map, "mode", out object mode) &&
                Enum.TryParse(AsString(mode), ignoreCase: true, out PersistentListener.CallMode parsed))
                listener.Mode = parsed;

            if (listener.Mode == PersistentListener.CallMode.Static &&
                TryGet(map, "argType", out object argTypeName)) {
                Type argType = ResolveArgType(AsString(argTypeName));
                listener.StaticArgumentType = argType;
                if (argType is not null && TryGet(map, "arg", out object arg))
                    listener.StaticArgument = DeserializeArg(arg, argType);
            }

            into.PersistentListeners.Add(listener);
        }
    }

    static object SerializeArg(object arg) {
        if (arg is null)
            return null;
        if (arg is BObject asset)
            return AssetDatabase.TryGetAssetGuid(asset, out Guid g) ? AssetRef.FromGuid(g) : null;
        if (arg is Enum e)
            return e.ToString();
        return arg;
    }

    static object DeserializeArg(object raw, Type argType) {
        if (raw is null)
            return null;
        if (typeof(BObject).IsAssignableFrom(argType))
            return raw is string reference ? LoadAsset(reference, argType) : null;
        if (argType.IsEnum)
            return TryParseEnum(raw, argType);
        if (argType.IsInstanceOfType(raw))
            return raw;
        try { return Convert.ChangeType(raw, argType, CultureInfo.InvariantCulture); }
        catch { return Activator.CreateInstance(argType); }
    }

    static object TryParseEnum(object raw, Type enumType) {
        try { return Enum.Parse(enumType, raw.ToString()!, ignoreCase: true); }
        catch { return Activator.CreateInstance(enumType); }
    }

    static object LoadAsset(string reference, Type assetType) {
        System.Reflection.MethodInfo loadRef =
            typeof(AssetDatabase).GetMethod(nameof(AssetDatabase.LoadRef))!.MakeGenericMethod(assetType);
        return loadRef.Invoke(null, [reference]);
    }

    static Type ResolveArgType(string fullName) {
        if (string.IsNullOrEmpty(fullName))
            return null;
        Type t = Type.GetType(fullName);
        if (t is not null)
            return t;
        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
            t = asm.GetType(fullName);
            if (t is not null)
                return t;
        }
        return null;
    }

    static Dictionary<string, object> ToStringKeyedMap(object item) {
        if (item is Dictionary<string, object> s)
            return s;
        if (item is System.Collections.IDictionary d) {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry kv in d)
                map[kv.Key?.ToString() ?? ""] = kv.Value;
            return map;
        }
        return null;
    }

    static bool TryGet(Dictionary<string, object> map, string key, out object value) {
        foreach ((string k, object v) in map)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) {
                value = v;
                return true;
            }
        value = null;
        return false;
    }

    static string AsString(object o) => o?.ToString();
}
