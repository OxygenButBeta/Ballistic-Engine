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

    // Resolve one RPC entry (P4) — (typeId, methodId) -> (target, invoker), reflection-free. The generated
    // dispatch table lives on each descriptor; this is the O(1) lookup the receive path uses to deserialize
    // args + invoke the dev method without ever touching reflection (§11). Returns false for an unknown
    // (typeId, methodId) so the receiver drops a stale/garbage RPC frame instead of crashing.
    public static bool TryGetRpc(int typeId, int methodId, out NetworkRpcEntry entry) {
        if (byTypeId.TryGetValue(typeId, out NetworkTypeDescriptor d))
            return d.TryGetRpc(methodId, out entry);
        entry = default;
        return false;
    }

    // THE 0c contract: drop every registered descriptor so no script-ALC type/delegate survives the
    // reload (the ALC can unload). Called from ReloadGameScripts alongside InputRegistry.ClearForReload /
    // VolumeManager.ResetStack / the ComponentRegistry rebuild. The next assembly's registration re-runs.
    public static void ClearForReload() => byTypeId.Clear();
}

// Deserialize the args of one RPC frame and invoke the dev method on `self` (plan §11, P4). `caller` is the
// connection the framework attributes the call to (a To.Server RPC's owner-check already passed by the time
// this runs; the dev method may read `caller` to know WHO acted). A ref BitReader because it is a ref struct
// (the generated invoker advances it past the args). Emitted once per [Rpc] method by the generator — NO
// reflection on the dispatch path. The delegate captures the concrete type, so it is a script-ALC root that
// ClearForReload drops with the rest of the descriptor.
public delegate void NetworkRpcInvoker(NetworkBehaviour self, ref BitReader args, Connection caller);

// One [Rpc] method's dispatch entry (P4): its methodId (FNV of the name), its declared target (To.X — the
// receive-side owner-check needs it), its reliability (Reliable channel default; Rpc.Unreliable opt-in for
// spammy FX), and the reflection-free invoker. The generator fills an array of these per type.
public readonly struct NetworkRpcEntry {
    public readonly int MethodId;
    public readonly RpcTarget Target;
    public readonly bool Reliable;
    public readonly NetworkRpcInvoker Invoke;

    public NetworkRpcEntry(int methodId, RpcTarget target, bool reliable, NetworkRpcInvoker invoke) {
        MethodId = methodId;
        Target = target;
        Reliable = reliable;
        Invoke = invoke;
    }
}

// The per-type wire descriptor the generator fills (plan §11). RPC dispatch is the (typeId, methodId) →
// (target, invoker) table (P2 generated the methodId hashes; P4 fills the full entries + rides the wire).
// The replication side is the layout hash for the handshake guard (gate 0c). The ComponentType builds a
// client-side MIRROR of a spawned object from its typeId (P3 spawn replication) — a `typeof(T)` the
// generator emits (it knows the concrete type), so there is NO reflection on the spawn path. Type handles
// + invoker delegates ARE script-ALC roots, so the whole descriptor is dropped by ClearForReload.
public readonly struct NetworkTypeDescriptor {
    public readonly int TypeId;        // FNV of the full type name — the wire typeId
    public readonly int LayoutHash;    // FNV of the [Networked] field layout — the handshake drift guard
    public readonly string TypeName;   // for diagnostics / the editor net badge (not on the wire)
    public readonly NetworkRpcEntry[] Rpcs;  // the (methodId → target/reliable/invoker) dispatch table (P4)
    public readonly Type ComponentType;  // the concrete NetworkBehaviour type — builds the client mirror (P3)

    public NetworkTypeDescriptor(int typeId, int layoutHash, string typeName, NetworkRpcEntry[] rpcs,
        Type componentType = null) {
        TypeId = typeId;
        LayoutHash = layoutHash;
        TypeName = typeName;
        Rpcs = rpcs ?? Array.Empty<NetworkRpcEntry>();
        ComponentType = componentType;
    }

    // Linear scan over the RPC table (a handful of methods per type — a Dictionary would be heavier for ≤8
    // entries and this is the receive path, not the per-tick hot path). Returns the entry for a methodId.
    public bool TryGetRpc(int methodId, out NetworkRpcEntry entry) {
        for (int i = 0; i < Rpcs.Length; i++) {
            if (Rpcs[i].MethodId == methodId) { entry = Rpcs[i]; return true; }
        }
        entry = default;
        return false;
    }
}
