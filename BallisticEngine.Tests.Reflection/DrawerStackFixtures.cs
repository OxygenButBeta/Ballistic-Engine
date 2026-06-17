using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// Sample types exercising the B0 drawer-stack RESOLUTION (the pure, headless half of the Odin-style stack —
// the editor's runtime steps live in the host assembly and can't be referenced here, but WHICH steps apply
// and IN WHAT ORDER is engine-side logic, fully testable). Each member names the attribute combination it
// proves; a failed check points at the exact rule.
public sealed class DrawerStackSample {
    // No cross-cutting attributes: the stack has only its terminal-less default — zero non-terminal steps.
    public int Plain { get; set; }

    // [ReadOnly] alone → exactly one Enable step.
    [ReadOnly] public int ReadOnlyOnly { get; set; }

    // [ShowIf] alone → exactly one Visibility step.
    [ShowIf("Plain")] public int VisibleIf { get; set; }

    // [Header] alone → exactly one Chrome step.
    [Header("Section")] public int Headed { get; set; }

    // The COMPOSING case the whole chunk exists for: [ShowIf] + [ReadOnly] on ONE member must produce BOTH a
    // Visibility step (outermost) and an Enable step (inner) — they don't fight, they nest. Add [Header] for a
    // Chrome step in between to lock the full outer→inner order Visibility → Chrome → Enable.
    [ShowIf("Plain")]
    [Header("Combo")]
    [ReadOnly]
    public float Combo { get; set; }

    // [DisableIf] is an Enable-stage condition (not Visibility) — proves the kind split (Show/Hide → Visibility,
    // Enable/Disable → Enable).
    [DisableIf("Plain")] public int DisabledIf { get; set; }

    // [Space] also triggers the Chrome step (Header OR Space).
    [Space(8)] public int Spaced { get; set; }
}
