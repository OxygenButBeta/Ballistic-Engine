using BallisticEngine.Networking;

namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class OnChangedAttribute : Attribute {
    public string Method { get; }
    public OnChangedAttribute(string method) => Method = method;
}
