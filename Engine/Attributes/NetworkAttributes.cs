using BallisticEngine.Networking;

namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class NetworkedAttribute : Attribute {
    public NetworkWriteAuthority Authority { get; }

    public float Min { get; set; }
    public float Max { get; set; }
    public int Bits { get; set; }

    public NetworkedAttribute(NetworkWriteAuthority authority = NetworkWriteAuthority.Server) =>
        Authority = authority;

    public bool IsQuantized => Bits > 0;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RpcAttribute : Attribute {
    public RpcTarget Target { get; }
    public bool Reliable { get; set; } = true;

    public RpcAttribute(RpcTarget target) => Target = target;
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class OnChangedAttribute : Attribute {
    public string Method { get; }
    public OnChangedAttribute(string method) => Method = method;
}
