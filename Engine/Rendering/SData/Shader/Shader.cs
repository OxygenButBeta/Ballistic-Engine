using OpenTK.Mathematics;

namespace BallisticEngine;

public abstract class Shader : BObject, IDisposable, ISharedResource {
    public abstract ResourceIdentity Identity { get; }
    public abstract int UID { get; }
    static Shader ActiveShader;
    protected Shader() => SharedResources<Shader>.AddResource(this);

    public void Dispose() {
        SharedResources<Shader>.RemoveResource(this);
        OnDispose();
    }

    protected abstract void OnDispose();
    public void Activate() {
        if (Equals(ActiveShader))
            return;

        ActivateShader();
        ActiveShader = this;
    }

    public void Deactivate() {
        if (!Equals(ActiveShader))
            return;

        ActiveShader = null;
        DeactivateShader();
    }

    public abstract void SetBool(string name, bool value);
    public abstract void SetInt(string name, int value);
    public abstract void SetFloat(string name, float value);
    public abstract void SetFloat2(string name, Vector2 value);
    public abstract void SetFloat3(string name, Vector3 value);
    public abstract void SetFloat4(string name, Vector4 value);
    public abstract void SetMatrix4(string name, ref Matrix4 value, bool transpose = false);
    protected abstract void ActivateShader();
    protected abstract void DeactivateShader();
}