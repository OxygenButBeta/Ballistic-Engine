namespace BallisticEngine.AssetPipeline;

public static class BlendExportScript {
    public const string Source = """
import bpy, sys, json, os

# Args after the "--" separator: <out.fbx> <out.json>
argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
if len(argv) < 2:
    print("BLEND_EXPORT_ERROR: expected <out.fbx> <out.json>")
    sys.exit(1)

out_fbx, out_json = argv[0], argv[1]

def mat_to_list(m):
    # Blender Matrix is row-indexed m[row][col]; emit 16 floats row-major.
    return [float(m[r][c]) for r in range(4) for c in range(4)]

scene = bpy.context.scene
data = {"meshes": [], "cameras": [], "lights": []}

for obj in scene.objects:
    world = obj.matrix_world
    if obj.type == "MESH":
        data["meshes"].append({"name": obj.name, "matrix": mat_to_list(world)})
    elif obj.type == "CAMERA":
        cam = obj.data
        data["cameras"].append({
            "name": obj.name,
            "matrix": mat_to_list(world),
            # Blender stores vertical OR horizontal sensor fit; angle_y is the vertical FOV in radians.
            "fovY": float(cam.angle_y),
            "near": float(cam.clip_start),
            "far": float(cam.clip_end),
            "isActive": (scene.camera == obj),
        })
    elif obj.type == "LIGHT":
        light = obj.data
        entry = {
            "name": obj.name,
            "matrix": mat_to_list(world),
            "lightType": light.type,                       # SUN / POINT / SPOT / AREA
            "color": [float(c) for c in light.color],      # linear RGB, 0..1
            "energy": float(light.energy),                 # W for point/spot/area, W/m^2 for sun
        }
        if light.type in ("POINT", "SPOT"):
            entry["range"] = float(getattr(light, "cutoff_distance", 0.0)) \
                if getattr(light, "use_custom_distance", False) else 0.0
        if light.type == "SPOT":
            entry["spotSize"] = float(light.spot_size)     # full cone angle, radians
            entry["spotBlend"] = float(light.spot_blend)   # 0..1 inner/outer falloff
        data["lights"].append(entry)

# Export meshes to FBX. FBX (not glTF) because AssimpNet 4.1.0's bundled native Assimp parses
# Blender's modern glTF binary as zero meshes, but reads Blender FBX perfectly. Exported Y-up /
# -Z-forward at unit scale (axis_up='Y', FBX_SCALE_ALL) so the mesh lands in engine space exactly
# as (x,y,z)_blender -> (x,z,-y)_engine — the SAME basis change the C# converter applies to the
# camera/lights, so geometry and lights stay aligned. (Plain apply_unit_scale without FBX_SCALE_ALL
# bakes Blender's 100x FBX unit factor into vertices, blowing the mesh up to 100x.) Only meshes are
# exported; cameras/lights ride in the JSON sidecar so they map to engine components instead of FBX
# nodes the model importer would ignore.
mesh_exists = len(data["meshes"]) > 0
if mesh_exists:
    try:
        bpy.ops.export_scene.fbx(
            filepath=out_fbx,
            use_selection=False,
            apply_unit_scale=True,
            apply_scale_options="FBX_SCALE_ALL",   # keep 1:1 scale (avoid the 100x unit blow-up)
            global_scale=1.0,
            use_mesh_modifiers=True,               # apply modifiers
            axis_up="Y",                           # engine convention; (x,y,z)->(x,z,-y)
            axis_forward="-Z",
            object_types={"MESH"},                 # cameras/lights ride in the JSON sidecar
            mesh_smooth_type="FACE",
        )
    except Exception as e:
        print("BLEND_EXPORT_ERROR: FBX export failed: %s" % e)
        sys.exit(1)

data["hasMesh"] = mesh_exists and os.path.exists(out_fbx)

with open(out_json, "w") as f:
    json.dump(data, f)

print("BLEND_EXPORT_OK")
""";
}
