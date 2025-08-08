using OpenTK.Mathematics;

namespace BallisticEngine.Rendering;

public class BatchGroup<TDrawable> : IDisposable where TDrawable : IDrawable {
    public IReadOnlyList<Matrix4> Matrix4s => matrix4s;
    public TDrawable Drawable { get; private set; }
    readonly List<Matrix4> matrix4s;

    public BatchGroup() {
        matrix4s = new List<Matrix4>(capacity: Service<EngineConfigurationAsset>.Get().DefaultBatchGroupSize);
    }

    public void SetDrawable(TDrawable drawable) {
        Drawable = drawable;
    }

    public void Dispose() {
        matrix4s.Clear();
        BatchGroupPool<TDrawable>.Return(this);
    }

    public void Add(TDrawable drawable) {
        matrix4s.Add(drawable.Transform.LocalMatrix);
    }
}