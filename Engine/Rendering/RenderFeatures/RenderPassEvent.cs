namespace BallisticEngine;

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
