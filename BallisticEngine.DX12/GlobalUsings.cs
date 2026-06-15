// DX12 migration step 4: the engine's math types are System.Numerics now, matching this project. The
// Matrix4 alias lets shared/engine-interop code use the `Matrix4` spelling here too.
global using System.Numerics;
global using Matrix4 = System.Numerics.Matrix4x4;
