using BallisticEngine;

// Per-instance model-matrix buffer. Lives in Abstraction/ (not OpenGL/) because the abstraction itself
// references it (RenderAsset.CreateInstancedBuffer) and the engine holds one per Mesh (Mesh.InstanceBuffer)
// — it has no GL dependency (only OpenTK.Mathematics, which Abstraction is allowed to use), so keeping it
// under OpenGL/ was a layering bug that would break the build the moment the GL backend is deleted.
public abstract class InstancedBuffer(RenderContext renderContext) : GPUBuffer<Matrix4>(renderContext)
{
}
