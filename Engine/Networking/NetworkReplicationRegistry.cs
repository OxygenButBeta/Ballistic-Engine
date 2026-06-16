using BallisticEngine.Networking;

namespace BallisticEngine;

// The load-once network registration table (plan §11) — typeId → replication/RPC metadata, built once at
// load (the ONLY sanctioned reflection-free registration). The generator emits a registration call per
// NetworkBehaviour subtype (via a [ModuleInitializer] in the generated code, or an explicit scan at
// bootstrap), so dispatch is an O(1) dictionary lookup with NO per-tick reflection (the standing rule).
//
// THE GATE-0c ROOT (§8.6.2): this is the SECOND new host-side static that pins the collectible script-ALC
// if not cleared at the reload boundary — game-defined Pawn/PlayerController types register their typeId →
// (layout hash, RPC method ids) here. ClearForReload() joins the existing "clear scene + component
// registry + volume stack + InputRegistry before GameScripts.Unload" list in ReloadGameScripts; without
// it the first hot-reload leaks the old assembly via a registered game type. Mirrors InputRegistry exactly.
public static class NetworkReplicationRegistry {
    // typeId (FNV of the full type name) → its replication descriptor. Ordinal int keys; no Type handles
    // held directly here beyond the descriptor, which the reload clear drops wholesale.
    static readonly Dictionary<int, NetworkTypeDescriptor> byTypeId = new();

    public static int Count => byTypeId.Count;

    // Register one NetworkBehaviour subtype's wire metadata. Called by generated code (one call per type)
    // at load / bootstrap scan. Idempotent on typeId — a hot-reload re-registers the new ALC's type over
    // the old (ClearForReload empties the table first, so in practice it's a fresh insert each reload).
    public static void Register(NetworkTypeDescriptor descriptor) {
        byTypeId[descriptor.TypeId] = descriptor;
    }

    public static bool TryGet(int typeId, out NetworkTypeDescriptor descriptor) =>
        byTypeId.TryGetValue(typeId, out descriptor);

    public static NetworkTypeDescriptor Get(int typeId) =>
        byTypeId.TryGetValue(typeId, out var d) ? d : default;

    public static IReadOnlyCollection<NetworkTypeDescriptor> All => byTypeId.Values;

    // THE 0c contract: drop every registered descriptor so no script-ALC type/delegate survives the
    // reload (the ALC can unload). Called from ReloadGameScripts alongside InputRegistry.ClearForReload /
    // VolumeManager.ResetStack / the ComponentRegistry rebuild. The next assembly's registration re-runs.
    public static void ClearForReload() => byTypeId.Clear();
}

// The per-type wire descriptor the generator fills (plan §11). RPC dispatch is the (typeId, methodId) →
// invoke table (P2 generates the table/stubs; the wire transport for RPCs is P4). The replication side is
// the layout hash for the handshake guard (gate 0c). The Factory builds a client-side MIRROR of a spawned
// object from its typeId (P3 spawn replication) — a `() => new T()` the generator emits (it knows the
// concrete type), so there is NO reflection on the spawn path. The delegate IS a script-ALC root, so it
// is dropped by ClearForReload like everything else here.
public readonly struct NetworkTypeDescriptor {
    public readonly int TypeId;        // FNV of the full type name — the wire typeId
    public readonly int LayoutHash;    // FNV of the [Networked] field layout — the handshake drift guard
    public readonly string TypeName;   // for diagnostics / the editor net badge (not on the wire)
    public readonly int[] RpcMethodIds; // the (typeId, methodId) dispatch keys this type declares (P4 wire)
    public readonly Type ComponentType;  // the concrete NetworkBehaviour type — builds the client mirror (P3)

    public NetworkTypeDescriptor(int typeId, int layoutHash, string typeName, int[] rpcMethodIds,
        Type componentType = null) {
        TypeId = typeId;
        LayoutHash = layoutHash;
        TypeName = typeName;
        RpcMethodIds = rpcMethodIds ?? Array.Empty<int>();
        ComponentType = componentType;
    }
}
