using System.Linq;
using BallisticEngine;
using BallisticEngine.Serialization;

namespace BallisticEngine.Tests.Reflection;

// Per-submesh material overrides (Unity's renderer.sharedMaterials, 2026-06-18): a multi-material mesh's
// inspector now shows an EDITABLE material slot per submesh, backed by a serialized override array on the
// concrete renderer (StaticMeshRenderer / SkinnedMeshRenderer expose SharedMaterials; the base Renderer
// owns the array + Get/SetMaterialOverride + the MaterialFor resolution). This suite locks the engine
// contract the editor UI relies on, without needing a live GPU Material:
//   - SharedMaterials is a SERIALIZED member of the concrete renderer (so overrides round-trip in .scene)
//   - it is HIDDEN from the attribute inspector (the RendererPreview draws the per-slot list itself, so the
//     default Material[] collection drawer must NOT also draw it — no double UI)
//   - SetMaterialOverride grows the array to address the requested slot, GetMaterialOverride reads it back,
//     and clearing (null) leaves the slot resolvable again
//   - an untouched renderer has a null/empty override array (byte-identical to before — MaterialFor falls
//     through to the baked ref / SharedMaterial)
internal static class MaterialOverrideTests {
    public static int Run() {
        var h = new Harness();

        // SharedMaterials is serialized (declared on the concrete type, not the excluded Renderer base).
        foreach (var t in new[] { typeof(StaticMeshRenderer), typeof(SkinnedMeshRenderer) }) {
            var ser = ComponentReflection.SerializableMembers(t).Select(m => m.Name).ToHashSet();
            var insp = ComponentReflection.InspectorMembers(t).Select(m => m.Name).ToHashSet();
            h.Check($"{t.Name}.SharedMaterials is serialized", ser.Contains("SharedMaterials"));
            h.Check($"{t.Name}.SharedMaterials is hidden from the attribute inspector",
                !insp.Contains("SharedMaterials"));
            // It must classify as a Material collection (the serializer/element pipeline path).
            var member = ComponentReflection.SerializableMembers(t).First(m => m.Name == "SharedMaterials");
            h.Check($"{t.Name}.SharedMaterials → Material[] collection",
                ComponentReflection.MemberType(member) == typeof(Material[]) &&
                PropertyCategories.Classify(typeof(Material[])) == PropertyCategory.Collection);
        }

        // Override array behaviour (no Material instance needed — null overrides exercise the array plumbing).
        var r = new StaticMeshRenderer();
        h.Check("fresh renderer has no override array", r.SharedMaterials is null);
        h.Check("GetMaterialOverride on empty → null", r.GetMaterialOverride(0) is null);

        // Setting a slot grows the array to fit; a negative index is ignored.
        r.SetMaterialOverride(-1, null);
        h.Check("negative index ignored (still no array)", r.SharedMaterials is null);
        r.SetMaterialOverride(3, null);
        h.Check("SetMaterialOverride(3) grows the array to >= 4", r.SharedMaterials is { Length: >= 4 });
        h.Check("GetMaterialOverride(3) reads back null (cleared slot)", r.GetMaterialOverride(3) is null);

        // The override array assigned wholesale (the deserialize path) reads back through GetMaterialOverride.
        r.SharedMaterials = new Material[2];   // both null = "no override", resolvable via the baked path
        h.Check("assigned 2-length array reads back", r.SharedMaterials is { Length: 2 });
        h.Check("GetMaterialOverride(1) null on all-null array", r.GetMaterialOverride(1) is null);
        h.Check("GetMaterialOverride(5) out-of-range → null", r.GetMaterialOverride(5) is null);

        return h.Report("Material overrides (sharedMaterials)");
    }
}
