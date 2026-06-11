using System.Text.Json;

namespace BallisticEngine;

// Small key/value persistence (Unity's PlayerPrefs) for settings and light game state — volume,
// last level, high score, "seen the intro". Backed by a single JSON file in the project's save
// directory (set at bootstrap, see SaveSystem). int/float/string/bool values; auto-loaded on first
// access, written on Save() (and, for safety, on each Set — see AutoSave).
//
// For larger structured saves (inventories, world state) use SaveData<T> instead — PlayerPrefs is
// for flat scalars.
public static class PlayerPrefs {
    static readonly Dictionary<string, object> values = new(StringComparer.Ordinal);
    static bool loaded;
    static bool dirty;

    // When true, every Set immediately flushes to disk (simpler but more I/O). Default false: call
    // Save() at sensible points (level end, quit). Unity flushes on Set; we let you choose.
    public static bool AutoSave { get; set; }

    static string FilePath => Path.Combine(SaveSystem.SaveDirectory, "PlayerPrefs.json");

    // ---- Setters ------------------------------------------------------------
    public static void SetInt(string key, int value) => Set(key, value);
    public static void SetFloat(string key, float value) => Set(key, value);
    public static void SetString(string key, string value) => Set(key, value);
    public static void SetBool(string key, bool value) => Set(key, value);

    // ---- Getters (with defaults) --------------------------------------------
    public static int GetInt(string key, int defaultValue = 0) => Get(key, defaultValue);
    public static float GetFloat(string key, float defaultValue = 0f) => Get(key, defaultValue);
    public static string GetString(string key, string defaultValue = "") => Get(key, defaultValue);
    public static bool GetBool(string key, bool defaultValue = false) => Get(key, defaultValue);

    public static bool HasKey(string key) {
        EnsureLoaded();
        return values.ContainsKey(key);
    }

    public static void DeleteKey(string key) {
        EnsureLoaded();
        if (values.Remove(key))
            MarkDirty();
    }

    public static void DeleteAll() {
        EnsureLoaded();
        values.Clear();
        MarkDirty();
    }

    // Flushes pending changes to disk. No-op if nothing changed.
    public static void Save() {
        if (!dirty)
            return;
        try {
            Directory.CreateDirectory(SaveSystem.SaveDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(values, SaveSystem.JsonOptions));
            dirty = false;
        }
        catch (Exception e) {
            Debugging.LogError($"PlayerPrefs.Save failed: {e.Message}");
        }
    }

    // ---- Internals ----------------------------------------------------------

    static void Set(string key, object value) {
        EnsureLoaded();
        values[key] = value;
        MarkDirty();
    }

    static T Get<T>(string key, T defaultValue) {
        EnsureLoaded();
        if (!values.TryGetValue(key, out object raw) || raw is null)
            return defaultValue;
        try {
            // Values may come back from JSON as JsonElement; coerce to the requested scalar.
            if (raw is JsonElement element)
                return element.Deserialize<T>(SaveSystem.JsonOptions);
            if (raw is T typed)
                return typed;
            return (T)Convert.ChangeType(raw, typeof(T));
        }
        catch {
            return defaultValue;
        }
    }

    static void MarkDirty() {
        dirty = true;
        if (AutoSave)
            Save();
    }

    static void EnsureLoaded() {
        if (loaded)
            return;
        loaded = true;
        try {
            if (File.Exists(FilePath)) {
                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(FilePath), SaveSystem.JsonOptions);
                if (data is not null)
                    foreach ((string k, object v) in data)
                        values[k] = v;
            }
        }
        catch (Exception e) {
            Debugging.LogError($"PlayerPrefs load failed: {e.Message}");
        }
    }

    // Reloads from disk on next access (after the save directory is (re)assigned at bootstrap).
    internal static void Reset() {
        values.Clear();
        loaded = false;
        dirty = false;
    }
}
