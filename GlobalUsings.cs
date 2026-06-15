// DX12 migration ENDGAME 3, step 4: the engine math types are System.Numerics now (was OpenTK.Mathematics).
// `Matrix4` is aliased to System.Numerics.Matrix4x4 so the pervasive `Matrix4` spelling stays unchanged.
// The OpenTK affordances System.Numerics lacks live in OpenTkCompat.cs (global namespace).
global using System.Numerics;
global using Matrix4 = System.Numerics.Matrix4x4;
