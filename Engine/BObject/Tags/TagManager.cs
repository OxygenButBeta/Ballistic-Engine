namespace BallisticEngine;

public static class TagManager {
    public static readonly string Untagged = "Untagged";

    static readonly List<string> tags = new() {
        "Untagged", "Player", "MainCamera", "Enemy", "Respawn", "Finish",
        "EditorOnly", "GameController",
    };

    public static IReadOnlyList<string> Tags => tags;

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
