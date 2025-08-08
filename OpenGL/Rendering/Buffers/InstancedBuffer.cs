using BallisticEngine;
using OpenTK.Mathematics;

public abstract class InstancedBuffer(RenderContext renderContext) : GPUBuffer<Matrix4>(renderContext)
{
}