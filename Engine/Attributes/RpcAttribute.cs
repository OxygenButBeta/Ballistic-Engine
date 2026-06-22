using BallisticEngine.Networking;

namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RpcAttribute : Attribute {
    public RpcTarget Target { get; }
    public bool Reliable { get; set; } = true;

    public RpcAttribute(RpcTarget target) => Target = target;
}
