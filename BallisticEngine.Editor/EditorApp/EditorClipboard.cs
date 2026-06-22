
namespace BallisticEngine.Editor;

internal static class EditorClipboard {
    static Entity copied;

    public static bool HasCopy => copied is not null;

    public static void Copy(Entity entity) => copied = entity;

    public static Entity Paste(Scene scene) {
        if (copied is null || !scene.Entities.Contains(copied))
            return null;

        Entity copy = EntityClone.Duplicate(scene, copied);
        copy.transform.Position += new Vector3(0.5f, 0f, 0.5f);
        return copy;
    }
}
