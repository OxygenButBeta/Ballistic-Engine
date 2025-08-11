using OpenTK.Mathematics;

namespace BallisticEngine.Rendering;

public class BatchGroup<TDrawable> : IDisposable where TDrawable : IDrawable {
    public TDrawable Drawable { get; private set; }
    public readonly List<Matrix4> Matrix4s;

    public BatchGroup() {
        Matrix4s = new List<Matrix4>(capacity: Service<EngineConfigurationAsset>.Get().DefaultBatchGroupSize);
    }

    public void SetDrawable(TDrawable drawable) {
        Drawable = drawable;
    }

    public void Dispose() {
        Matrix4s.Clear();
        BatchGroupPool<TDrawable>.Return(this);
    }

    public void Add(TDrawable drawable) {
        Matrix4s.Add(drawable.Transform.LocalMatrix);
    }
}