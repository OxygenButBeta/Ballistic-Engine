#version 460 core
// Voxelization geometry stage. For each triangle, choose the axis (X/Y/Z) along which the triangle
// has the LARGEST projected area and emit it orthographically onto that plane — this maximizes the
// number of rasterized fragments, so the triangle is captured into the voxel grid with the fewest
// holes (standard single-pass voxelization, Crassin/OpenGL Insights). The fragment stage maps the
// rasterized position back to a 3D voxel coordinate from the world position we pass through.

layout(triangles) in;
layout(triangle_strip, max_vertices = 3) out;

in VsOut {
    vec3 worldPos;
    vec3 normal;
    vec2 uv;
    flat uint materialId;
} vs[];

out GsOut {
    vec3 worldPos;
    vec3 normal;
    vec2 uv;
    flat uint materialId;
} gs;

uniform vec3 VolumeMin;
uniform vec3 VolumeInvSize;  // 1 / volume world size
uniform int  VoxelRes;

void main() {
    // Triangle normal magnitude per axis = projected area on the plane perpendicular to that axis.
    vec3 e0 = vs[1].worldPos - vs[0].worldPos;
    vec3 e1 = vs[2].worldPos - vs[0].worldPos;
    vec3 n = abs(cross(e0, e1));
    int axis = (n.x >= n.y && n.x >= n.z) ? 0 : (n.y >= n.z ? 1 : 2);

    for (int i = 0; i < 3; ++i) {
        // World -> [0,1] grid coords.
        vec3 g = (vs[i].worldPos - VolumeMin) * VolumeInvSize;
        // Project onto the dominant axis: drop that axis, keep the other two as clip xy in [-1,1].
        vec2 p;
        if (axis == 0)      p = g.yz;
        else if (axis == 1) p = g.xz;
        else                p = g.xy;
        gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);

        gs.worldPos = vs[i].worldPos;
        gs.normal = vs[i].normal;
        gs.uv = vs[i].uv;
        gs.materialId = vs[i].materialId;
        EmitVertex();
    }
    EndPrimitive();
}
