using BallisticEngine.GI;

namespace BallisticEngine.AssetPipeline;

// Resolves a mesh's baked SDF, baking on first use and caching to a sibling .bsdf next to the
// mesh's .bmesh artifact. The GI system asks for an SDF by (artifact path, MeshData) and gets a
// ready MeshSdf without caring whether it came from disk or a fresh bake.
//
// Lives in AssetPipeline (the only layer allowed to touch artifact files); the GL/GI layer calls
// GetOrBake and uploads the returned field. Baking is CPU work (MeshSdfBaker) — callers should run
// it off the GL thread for big meshes.
public static class SdfCache {
    // Derives the .bsdf path from the mesh's .bmesh artifact path (same stem).
    public static string SdfPathFor(string bmeshArtifactPath) =>
        Path.ChangeExtension(bmeshArtifactPath, ".bsdf");

    // Returns the mesh's SDF: the cached .bsdf when it exists and matches the mesh fingerprint,
    // otherwise a fresh bake that is then written to the cache. `bmeshArtifactPath` is the absolute
    // path of the mesh's .bmesh (TryGetArtifactPath); the .bsdf is its sibling. Returns null only if
    // the mesh has no bakeable geometry.
    public static MeshSdf GetOrBake(string bmeshArtifactPath, in MeshData mesh,
        MeshSdfBaker.Settings settings) {
        if (!mesh.IsValid)
            return null;

        long stamp = SdfArtifact.StampFor(mesh);
        string sdfPath = SdfPathFor(bmeshArtifactPath);

        MeshSdf cached = SdfArtifact.Read(sdfPath, stamp);
        if (cached is not null)
            return cached;

        MeshSdf baked = MeshSdfBaker.Bake(mesh, settings);
        if (baked is null)
            return null;

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(sdfPath)!);
            SdfArtifact.Write(sdfPath, baked, stamp);
        }
        catch {
            // A write failure (locked file, full disk) is non-fatal — we still return the field we
            // just baked; it just won't be cached this run.
        }
        return baked;
    }
}
