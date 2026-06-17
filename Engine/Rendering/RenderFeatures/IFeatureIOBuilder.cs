namespace BallisticEngine;

// The backend-agnostic surface a RenderFeature.Declare() uses to declare its resource reads/writes —
// the engine-side mirror of the DX12 backend's Dx12PassBuilder, reduced to string handle names so a
// feature stays portable (it must not reference BallisticEngine.DX12 — the seam decision, design §3).
// The backend adapter (chunk 20) translates each declared name into a Dx12PassBuilder.Read/Write against
// the canonical graph handle, so the feature's adapter participates in V1 cull / V2 alias / V3
// auto-barriers exactly like a built-in pass.
//
// Resources are addressed by canonical STRING name ("SceneColor", a scratch name the feature minted,
// etc.) — the same names the feature passes to IFeaturePassRecorder at Record time.
public interface IFeatureIOBuilder {
    // Declare a READ of the named handle (the pass samples it).
    void Read(string handleName);

    // Declare a WRITE to the named handle (the pass produces / overwrites it).
    void Write(string handleName);

    // Declare a read-modify-write of the named handle (sample then overwrite in place).
    void ReadWrite(string handleName);

    // Request a transient scratch handle of the given role/name the feature can write then read within
    // its own pass — the backend may pool/alias it (V2). Returns the canonical name to use at Record time
    // (the backend may namespace it to keep it unique). DEFAULT-safe: an un-overridden Declare requests
    // nothing, so the feature is an opaque node.
    string RequestScratch(string roleName);

    // Opt this feature's pass into graph CULLING (default OFF — the safe escape hatch matches an
    // un-migrated built-in). A feature that produces an output nothing consumes is dropped only when it
    // opts in here.
    void AllowCulling(bool allow = true);
}
