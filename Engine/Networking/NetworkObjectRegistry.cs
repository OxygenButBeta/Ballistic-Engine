namespace BallisticEngine;

// The netId -> NetworkObject table, as a GENERATIONAL SLOT ARRAY (plan §8.4 / §14 item 2).
//
// Why a slot array, not a Dictionary: NetworkRef<T>.Value resolves on every deref, and a deref inside
// NetworkTick must not pay a dictionary hash (the standing no-hot-path-overhead rule). A slot is an
// O(1) array index + an int generation compare. Despawn bumps the slot's generation, so any NetworkRef
// captured before that resolves to null (generation mismatch) instead of a dangling object — the
// standard generational-handle pattern that makes "null on despawn" real (a plain C# ref would dangle).
//
// netId encodes (slot, generation): the low SlotBits are the slot index, the high bits the generation.
// So a netId alone is enough to both index the slot AND validate the generation — the wire only carries
// one int, and a stale handle is detected without a separate lookup.
internal sealed class NetworkObjectRegistry {
    const int SlotBits = 20;                 // up to ~1M concurrent slots — far past any real scene
    const int SlotMask = (1 << SlotBits) - 1;

    struct Slot {
        public NetworkObject Object;          // null when free
        public int Generation;                // bumped on each despawn; odd = live is NOT required, just monotonic
    }

    Slot[] slots = new Slot[64];
    readonly Stack<int> freeSlots = new();    // recycled indices (LIFO) so the array stays compact
    int highWater = 1;                        // slot 0 reserved ("unspawned" / netId 0)
    int count;

    public int Count => count;

    // Insert an object, returning its packed netId (slot | generation<<SlotBits). The object's NetId is
    // the caller's to stamp; this just allocates the slot.
    public int Add(NetworkObject obj) {
        int slot = freeSlots.Count > 0 ? freeSlots.Pop() : NextHighWater();
        ref Slot s = ref slots[slot];
        s.Object = obj;
        // generation stays as-is (it was bumped on the previous despawn of this slot); a fresh slot
        // starts at generation 1 so netId is never 0 for a live object.
        if (s.Generation == 0)
            s.Generation = 1;
        count++;
        return Pack(slot, s.Generation);
    }

    // Remove by netId. Bumps the slot's generation so every NetworkRef to the old identity now reads
    // null. The slot is recycled for a future Add (possibly a different object — the generation keeps
    // the two identities distinct, the §8.5.4 pooling invariant).
    public void Remove(int netId) {
        int slot = netId & SlotMask;
        if (slot <= 0 || slot >= highWater)
            return;
        ref Slot s = ref slots[slot];
        if (s.Object is null || s.Generation != Generation(netId))
            return;   // already removed / stale
        s.Object = null;
        s.Generation++;            // INVALIDATE every captured NetworkRef to this identity
        if (s.Generation == 0) s.Generation = 1;   // skip 0 (reserved for "never allocated")
        freeSlots.Push(slot);
        count--;
    }

    // Resolve a netId to its object, or null if the generation no longer matches (despawned) or the
    // slot is free. The hot path NetworkRef<T>.Value calls this — array index + int compare, no hash.
    public NetworkObject Resolve(int netId) {
        int slot = netId & SlotMask;
        if (slot <= 0 || slot >= highWater)
            return null;
        ref Slot s = ref slots[slot];
        return s.Generation == Generation(netId) ? s.Object : null;
    }

    public void Clear() {
        Array.Clear(slots);
        freeSlots.Clear();
        highWater = 1;
        count = 0;
    }

    // Enumerate live objects (the network tick / observer walks this; not a hot per-deref path).
    public IEnumerable<NetworkObject> All() {
        for (int i = 1; i < highWater; i++)
            if (slots[i].Object is { } o)
                yield return o;
    }

    int NextHighWater() {
        if (highWater >= slots.Length)
            Array.Resize(ref slots, slots.Length * 2);
        return highWater++;
    }

    static int Pack(int slot, int generation) => (generation << SlotBits) | slot;
    static int Generation(int netId) => netId >> SlotBits;
}
