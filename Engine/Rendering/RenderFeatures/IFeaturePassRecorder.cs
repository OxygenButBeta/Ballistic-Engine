namespace BallisticEngine;

// The backend-agnostic recording surface a RenderFeature.Record() drives — URP's CommandBuffer role,
// reduced to the verbs phase-3 actually needs. The engine NEVER sees a DX12 type: the DX12 backend
// implements this interface (chunk 20) and the adapter hands a feature its concrete recorder, so an
// authored feature stays portable and a game can reference it from GameScripts.dll (engine library only).
//
// Resources are addressed by canonical STRING handle name (e.g. "SceneColor") — the same names the
// feature declared reads/writes against (RenderFeature.Declare) and the names the backend maps to its
// concrete targets / graph handles. Keeping the surface string-keyed (not a backend handle type) is what
// keeps it engine-side.
//
// DELIBERATELY MINIMAL (design §3 / D4): just enough for the chunk-20 proof feature (tint/invert
// SceneColor). EVERY new verb is added on a concrete feature's demand and LOGGED in the phase-3 design
// doc §5 (D4) — never speculatively (subtract-complexity doctrine). Today's verb set:
//   - SceneColor : the canonical name of the live HDR scene-color handle (so a feature reads/writes it
//                  without hardcoding the string).
//   - SetRenderTarget(name) : bind a handle as the active color target for subsequent draws.
//   - BlitFullscreen(src, dst, shaderOrMaterial) : full-screen pass sampling `src`, writing `dst`,
//                  using the named built-in shader / material asset (null = a plain copy).
public interface IFeaturePassRecorder {
    // The canonical handle name of the current scene-color target (the renderer's SceneColor, which can
    // change mid-frame — FSR/back-copy). A feature reads/writes through THIS rather than a literal so it
    // follows the live target.
    string SceneColor { get; }

    // Bind the named handle as the active render target for subsequent draws.
    void SetRenderTarget(string handleName);

    // Full-screen pass: sample `sourceHandle`, write `destHandle`, using the named shader/material
    // (null = a straight copy). Source and dest may be the same handle only if the backend can alias-copy;
    // otherwise the feature uses a scratch handle (declared in Declare). The backend resolves the name.
    void BlitFullscreen(string sourceHandle, string destHandle, string shaderOrMaterial = null);
}
