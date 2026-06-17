using System;
using System.Collections.Generic;
using System.Linq;

namespace BallisticEngine;

// A4 (editor-rework) substrate: a priority-ordered chain of input ACTIONS resolved against the live key state
// ONCE per frame, so editor hotkeys dispatch through ONE declarative table instead of scattered inline
// `if (ImGui.IsKeyPressed(...) && !ctrl && hovered)` checks. This is the input-half analogue of A2's
// OrderedPassList: the SAME "register declaratively, freeze a stable order, run the frozen order" shape, here
// resolving a key chord to at most one matching enabled action per CONTEXT.
//
// Why engine-side + headless (like OrderedPassList, unlike the editor-only MaximizeController): the editor's
// action BODIES are ImGui/EditorApplication-coupled and unreferenceable from the harness, but the RESOLUTION
// contract — "highest-priority matching enabled binding in the active context wins, deterministically, and a
// chord consumed in one context does not leak to another" — is pure logic. Lifting it here lets the harness
// drive it with a fake key probe (the dispatch threads an `isChordActive` delegate, exactly as
// OrderedPassList.Execute threads a `run` delegate), so a hotkey-conflict / context-leak regression is caught
// engine-side. The conflict-CHECK (Ctrl+R rebuild vs gizmo R, F vs Ctrl+Shift+F) is also pure and harnessed.
//
// Determinism rides DeterministicResolver's exact rule: among bindings whose chord is active AND whose context
// is in scope AND that are enabled, the one with the highest Priority wins; ties break by a stable ordinal key
// (the action Id). The winner is a total function of the registered set + the live key/context state, never of
// registration or assembly-load order.

// A modifier-qualified key. Generic over the host's key enum (the editor binds TKey = OpenTK Keys); the
// substrate never names a concrete key, so it stays BCL-only and headless-testable with an int/enum stand-in.
// Equality is value-based so a chord is a dictionary/HashSet key and two equal chords compare equal across
// registration sites. TKey is constrained to `struct` only (NOT IEquatable<TKey>) because plain enums like
// OpenTK's Keys do NOT implement IEquatable<T> — so the comparison goes through EqualityComparer<TKey>.Default,
// which the JIT specializes to a non-boxing path for enums.
public readonly struct KeyChord<TKey> : IEquatable<KeyChord<TKey>> where TKey : struct {
    public KeyChord(TKey key, bool ctrl = false, bool shift = false, bool alt = false) {
        Key = key;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
    }

    public TKey Key { get; }
    public bool Ctrl { get; }
    public bool Shift { get; }
    public bool Alt { get; }

    public bool Equals(KeyChord<TKey> other) =>
        EqualityComparer<TKey>.Default.Equals(Key, other.Key) &&
        Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;

    public override bool Equals(object obj) => obj is KeyChord<TKey> o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Key, Ctrl, Shift, Alt);

    public override string ToString() {
        string m = (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");
        return m + Key;
    }
}

// A registered input action: a chord, the CONTEXT it belongs to, a priority + stable Id for deterministic
// resolution, and the editor-side effect (Invoke). The substrate treats Invoke as opaque — it never inspects
// it, so the engine carries no dependency on what an action DOES (mirrors OrderedPassList carrying no
// dependency on a pass body). Context is an integer mask the host defines (the editor uses Global / SceneView /
// GameView), so "which contexts are live this frame" is one bitwise check, and a SceneView binding can never
// fire while only Global is in scope = the no-leak guarantee, structural rather than by inline guard.
public sealed class InputAction<TKey> where TKey : struct {
    public InputAction(string id, KeyChord<TKey> chord, int context, int priority, Action invoke) {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Chord = chord;
        Context = context;
        Priority = priority;
        Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
    }

    public string Id { get; }
    public KeyChord<TKey> Chord { get; }
    public int Context { get; }     // a single context flag (a power-of-two), matched against the live mask
    public int Priority { get; }    // higher wins among competing active bindings; ties break by Id
    public Action Invoke { get; }
}

// A reported binding conflict: two actions share a chord AND a context AND a priority, so the chord's winner is
// decided only by the Id tie-break — almost always an authoring mistake (one of the two will silently never
// fire). CheckConflicts surfaces these for the harness / an editor diagnostic; the dispatcher itself stays
// deterministic regardless.
public readonly struct InputConflict {
    public InputConflict(string idA, string idB, string chord, int context) {
        IdA = idA; IdB = idB; Chord = chord; Context = context;
    }
    public string IdA { get; }
    public string IdB { get; }
    public string Chord { get; }
    public int Context { get; }
    public override string ToString() => $"{Chord} in ctx {Context}: '{IdA}' vs '{IdB}'";
}

// The priority-resolve chain. Register actions once; Dispatch resolves the live key/context state to at most
// ONE action per frame and invokes it. Pure + deterministic; the host injects how to read the world.
public sealed class InputActionChain<TKey> where TKey : struct {
    readonly List<InputAction<TKey>> actions = new();
    InputAction<TKey>[] ordered = Array.Empty<InputAction<TKey>>();
    bool built;

    // Register an action. Call all Add()s once at init, then Build() (or let Dispatch build lazily). Adding
    // after Build() re-marks dirty so the next Dispatch re-freezes.
    public InputActionChain<TKey> Add(InputAction<TKey> action) {
        if (action is null) throw new ArgumentNullException(nameof(action));
        actions.Add(action);
        built = false;
        return this;
    }

    // Convenience overload mirroring the editor's call site.
    public InputActionChain<TKey> Add(string id, KeyChord<TKey> chord, int context, int priority, Action invoke) =>
        Add(new InputAction<TKey>(id, chord, context, priority, invoke));

    public int Count => actions.Count;

    // Freeze the resolution order: priority DESC then Id ascending (the DeterministicResolver rule). Once built,
    // Dispatch walks this frozen array and the FIRST active+in-scope action it meets is the winner — no per-
    // frame sort. Stable + total: independent of registration/assembly-load order.
    public void Build() {
        ordered = actions
            .OrderByDescending(a => a.Priority)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToArray();
        built = true;
    }

    // Resolve the active context mask + live key state to the single winning action, or null if none matches.
    //   contextMask     — bitwise OR of the contexts live THIS frame (e.g. Global | SceneView).
    //   isChordActive   — the host's edge/modifier probe: true if the chord fired this frame (raw input,
    //                     focus-aware). Injected so the substrate stays headless (a test passes a set membership).
    //   isEnabled       — optional per-action gate (e.g. "Save disabled while playing"); null = always enabled.
    // The FIRST action in frozen order whose Context is in the mask, whose chord is active, and that is enabled
    // wins. Because the order is priority-desc, a Ctrl+R(global, lower-priority) and an R(sceneview) resolve to
    // exactly one even when both could be active — whichever the contexts + chord actually select.
    public InputAction<TKey> Resolve(int contextMask, Func<KeyChord<TKey>, bool> isChordActive,
                                     Func<InputAction<TKey>, bool> isEnabled = null) {
        if (isChordActive is null) throw new ArgumentNullException(nameof(isChordActive));
        if (!built) Build();
        InputAction<TKey>[] list = ordered;
        for (int i = 0; i < list.Length; i++) {
            InputAction<TKey> a = list[i];
            if ((a.Context & contextMask) == 0) continue;          // context not live → cannot leak in
            if (isEnabled != null && !isEnabled(a)) continue;       // host gate (e.g. play-mode save lock)
            if (!isChordActive(a.Chord)) continue;                  // chord not fired this frame
            return a;
        }
        return null;
    }

    // Resolve AND invoke the winner. Returns true if an action fired (so the caller can mark the frame handled).
    public bool Dispatch(int contextMask, Func<KeyChord<TKey>, bool> isChordActive,
                         Func<InputAction<TKey>, bool> isEnabled = null) {
        InputAction<TKey> winner = Resolve(contextMask, isChordActive, isEnabled);
        if (winner is null) return false;
        winner.Invoke();
        return true;
    }

    // Static, pure conflict check: any two registered actions sharing chord + context + priority (so only the Id
    // tie-break separates them — one would silently never win). Independent of live input; the harness asserts
    // the editor's real binding table is conflict-free, and an editor diagnostic can list any it finds.
    public IReadOnlyList<InputConflict> CheckConflicts() {
        var conflicts = new List<InputConflict>();
        for (int i = 0; i < actions.Count; i++)
            for (int j = i + 1; j < actions.Count; j++) {
                InputAction<TKey> a = actions[i], b = actions[j];
                if (a.Priority == b.Priority && a.Context == b.Context && a.Chord.Equals(b.Chord))
                    conflicts.Add(new InputConflict(a.Id, b.Id, a.Chord.ToString(), a.Context));
            }
        return conflicts;
    }

    public void Clear() {
        actions.Clear();
        ordered = Array.Empty<InputAction<TKey>>();
        built = false;
    }
}
