namespace BallisticEngine;

// Project-wide tag registry (Unity's Tags & Layers > Tags). Tags are free-form strings an entity
// carries; gameplay queries them via Entity.CompareTag / BObjects.FindWithTag. The registry is the
// editable list shown in project settings — entities may also carry an unregistered tag (set from a
// script), which still compares fine; the registry just drives the editor dropdown.
//
// Defaults mirror Unity's built-in tags so ported code/tutorials line up. Populated from the
// project's layer/tag settings at bootstrap (LayerSettings); falls back to the defaults if absent.
public static class TagManager {
    // "Untagged" is the implicit default (Entity.Tag defaults to it). Kept first so it's index 0.
    public static readonly string Untagged = "Untagged";

    static readonly List<string> tags = new() {
        "Untagged", "Player", "MainCamera", "Enemy", "Respawn", "Finish",
        "EditorOnly", "GameController",
    };

    public static IReadOnlyList<string> Tags => tags;

    // Replaces the registry (editor settings load). Always keeps "Untagged" at index 0.
    public static void SetTags(IEnumerable<string> values) {
        tags.Clear();
        tags.Add(Untagged);
        foreach (string tag in values) {
            if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag))
                tags.Add(tag);
        }
    }

    public static void AddTag(string tag) {
        if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag))
            tags.Add(tag);
    }

    public static void RemoveTag(string tag) {
        if (tag != Untagged)
            tags.Remove(tag);
    }

    public static bool IsDefined(string tag) => tag is not null && tags.Contains(tag);
}
