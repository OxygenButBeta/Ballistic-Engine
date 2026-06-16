using System.Text;

namespace BallisticEngine.Editor;

// Templates for the asset browser's "New Script" (and the pristine-rename rewrite that keeps the
// class name matching the file name, Unity-style). The engine itself never cares about file/class
// name agreement — ComponentRegistry keys on the type name — this is purely least-surprise.
internal static class ScriptTemplates {
    // "New Script 2" -> "NewScript2"; leading digit gets a '_' prefix; empty -> "NewScript".
    public static string ClassName(string fileStem) {
        var builder = new StringBuilder(fileStem.Length);
        foreach (var c in fileStem)
            if (char.IsLetterOrDigit(c) || c == '_')
                builder.Append(c);

        if (builder.Length == 0)
            return "NewScript";
        if (char.IsDigit(builder[0]))
            builder.Insert(0, '_');
        return builder.ToString();
    }

    // Lifecycle overrides are plain `protected` — game assemblies override the engine's
    // `protected internal` members across an assembly boundary (C# drops the `internal` half).
    public static string Behaviour(string fileStem) =>
        $$"""
        using BallisticEngine;

        namespace Game;

        public class {{ClassName(fileStem)}} : Behaviour {
            protected override void OnBegin() {
            }

            protected override void Tick(in float delta) {
            }
        }

        """;

    // If the renamed script still has its untouched template content, regenerate it for the new
    // name so the class follows the file. Any user edit (even whitespace) disables the rewrite.
    public static void RewriteIfPristine(string oldStem, string newStem, string absolutePath) {
        try {
            if (File.ReadAllText(absolutePath) == Behaviour(oldStem))
                File.WriteAllText(absolutePath, Behaviour(newStem));
        }
        catch (Exception exception) {
            Debugging.LogWarning($"Script rename: could not update class name: {exception.Message}");
        }
    }
}
