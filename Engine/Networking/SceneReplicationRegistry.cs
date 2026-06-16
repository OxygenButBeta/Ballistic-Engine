using BallisticEngine.Networking;

namespace BallisticEngine;

// P7: the load-once registration table for the ENTITY-LESS replication path (plan §2/§10) — the GameState
// carve-out. Mirrors NetworkReplicationRegistry exactly, but for IReplicated SceneBehaviours (GameState),
// which replicate WITHOUT a NetworkObject/netId. The source generator emits a registration call per
// IReplicated SceneBehaviour subtype with [Networked] members (via a [ModuleInitializer]); the handshake
// layout digest folds these hashes in too, so a drifted GameState layout is an explicit error (§8.6.1).
//
// A THIRD host-side static root that pins the collectible script-ALC (gate 0c, §8.6.2) — a game-defined
// GameState type registers here. ClearForReload() joins the existing "clear before GameScripts.Unload"
// list in ReloadGameScripts alongside NetworkReplicationRegistry / InputRegistry, or the first hot-reload
// leaks the old assembly via a registered GameState type.
public static class SceneReplicationRegistry {
    static readonly Dictionary<int, SceneReplDescriptor> byTypeId = new();

    public static int Count => byTypeId.Count;

    // Register one IReplicated SceneBehaviour subtype's wire metadata (generated, one call per type at
    // load). Idempotent on typeId — a hot-reload re-registers the new ALC's type (ClearForReload empties
    // the table first, so in practice a fresh insert each reload).
    public static void Register(SceneReplDescriptor descriptor) {
        byTypeId[descriptor.TypeId] = descriptor;
    }

    public static bool TryGet(int typeId, out SceneReplDescriptor descriptor) =>
        byTypeId.TryGetValue(typeId, out descriptor);

    public static IReadOnlyCollection<SceneReplDescriptor> All => byTypeId.Values;

    // THE 0c contract: drop every registered descriptor so no script-ALC type survives the reload (the
    // ALC can unload). Called from ReloadGameScripts alongside NetworkReplicationRegistry.ClearForReload.
    public static void ClearForReload() => byTypeId.Clear();
}

// The per-type wire descriptor the generator fills for an IReplicated SceneBehaviour (plan §11, entity-less
// path). TypeId addresses the type on the handshake digest; LayoutHash is the drift guard. The ComponentType
// is NOT needed for a client mirror (GameState is scene-placed, not spawned over the wire — both ends
// already have it from the scene), so this descriptor is lighter than NetworkTypeDescriptor.
public readonly struct SceneReplDescriptor {
    public readonly int TypeId;        // FNV of the full type name — folds into the handshake digest
    public readonly int LayoutHash;    // FNV of the [Networked] field layout — the drift guard
    public readonly string TypeName;   // diagnostics / the editor net badge (not on the wire)

    public SceneReplDescriptor(int typeId, int layoutHash, string typeName) {
        TypeId = typeId;
        LayoutHash = layoutHash;
        TypeName = typeName;
    }
}
