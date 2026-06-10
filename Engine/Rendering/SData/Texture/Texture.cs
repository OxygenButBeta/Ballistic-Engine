namespace BallisticEngine;

public abstract class Texture : BObject, IDisposable {
    public TextureType Type { get; protected set; }

    public abstract int UID { get; protected set; }
    public abstract void Activate();
    public abstract void Deactivate();
    public abstract void Dispose();
}
