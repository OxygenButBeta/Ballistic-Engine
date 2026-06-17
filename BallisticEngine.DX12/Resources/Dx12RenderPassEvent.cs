namespace BallisticEngine.DX12;

// WHEN a render pass injects into the DX12 frame — the URP `renderPassEvent` model adapted to THIS
// engine's real frame sequence. A pass declares its event; Dx12RenderGraph orders the registered passes
// by it (stable tiebreak = registration order). Values are spaced by 50 so a feature/custom pass can
// slot at `Event + 1` (just after a built-in) without renumbering, and the gaps leave room for future
// built-ins between two existing ones. The ORDER of the members is the frame order; the spacing is only
// to leave injection room.
//
// Phase 1 (the pass LIST) sorts by these once at graph build. Phase 2 (the true graph) keeps them as the
// stable topological-sort tiebreak so a derived order reproduces this one for independent passes (R1).
//
// The mapping to the current DX12HDRenderer.BeginRender sequence:
//   BeforeShadows .. Shadows ........... RenderShadows (own upload list, before the frame list)
//   BeforeGBuffer .. GBuffer ........... geometry into the fat G-buffer (stays inline core in phase 1)
//   AfterGBuffer ..................... Hi-Z / punctual-light gather sit around here (inline core)
//   BeforeOpaqueLighting .. OpaqueLighting . DrawDeferredLighting
//   Sky .............................. DrawProcSky / DrawSkybox
//   AerialPerspective ................ DrawAerialPerspective
//   Transparents ..................... DrawTransparents
//   GlobalIllumination ............... DrawSsgi / DrawRtGi
//   Fog .............................. DrawFog
//   Reflections ...................... DrawSsr / DrawRtReflections
//   PostProcess ...................... SSAO / TAA / FSR
//   Composite ........................ DrawComposite (bloom + exposure metering are private sub-steps)
//   AfterRendering ................... frame tail (left inline in the orchestrator)
public enum Dx12RenderPassEvent {
    BeforeShadows         = 0,
    Shadows               = 50,
    BeforeGBuffer         = 100,
    GBuffer               = 150,
    AfterGBuffer          = 200,
    BeforeOpaqueLighting  = 250,
    OpaqueLighting        = 300,
    Sky                   = 350,
    AerialPerspective     = 400,
    Transparents          = 450,
    GlobalIllumination    = 500,
    Fog                   = 550,
    Reflections           = 600,
    PostProcess           = 650,
    Composite             = 700,
    AfterRendering        = 750,
}
