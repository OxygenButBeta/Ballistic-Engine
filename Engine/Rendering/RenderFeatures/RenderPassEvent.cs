namespace BallisticEngine;

// WHEN an authored RenderFeature injects into the frame — the engine-side mirror of the DX12 backend's
// Dx12RenderPassEvent (BallisticEngine.DX12/Resources/Dx12RenderPassEvent.cs). The members, their ORDER,
// and their integer VALUES are IDENTICAL to the backend enum so the backend bridge maps each authored
// feature's event 1:1 onto its own enum with a trivial `(Dx12RenderPassEvent)(int)event` cast — no
// lookup table, no drift. A feature declares its event; the backend slots it among the 14 built-in
// passes ordered by event (stable tiebreak = registration order).
//
// This lives ENGINE-SIDE (a game references the engine library only, never BallisticEngine.DX12 — the
// seam decision, phase-3 design §3) so an authored feature can name its injection point without a
// backend reference. Values spaced by 50 so a feature can slot at `Event + 1` just after a built-in.
//
// INVARIANT: keep this in lock-step with Dx12RenderPassEvent — same members, same order, same values.
public enum RenderPassEvent {
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
