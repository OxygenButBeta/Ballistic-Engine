public readonly record struct ResourceIdentity {
    public static readonly ResourceIdentity Empty = new(0);
    readonly int ResourceID;

    public override string ToString() {
        return $"Shared Resource Identity: {ResourceID}";
    }

    public ResourceIdentity(int ResourceId) {
        ResourceID = ResourceId;
    }


    public static ResourceIdentity Combine(params ResourceIdentity[] identities) {
        int combinedHash = identities.Aggregate(0, (current, identity) => current ^ identity.ResourceID);
        return new ResourceIdentity(combinedHash);
    }

    public static implicit operator ResourceIdentity(string path) => new(FNV1a.HashStr(path));
}