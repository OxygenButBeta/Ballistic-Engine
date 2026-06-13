using System.Globalization;
using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline;

// A parsed, engine-agnostic view of a pbrt scene (.pbrt). Unlike the Falcor importer (which regex-
// scans a Python builder), pbrt is a free-form, nested, stack-based scene-description language, so
// this is a real tokenizer + directive-dispatch parser maintaining a graphics-state stack.
//
// One tolerant parser covers BOTH pbrt-v3 and pbrt-v4:
//   - v3-only `WorldEnd` and `TransformBegin`/`TransformEnd` are accepted (the latter as CTM-only
//     scope); v4 ends the world at EOF and dropped those.
//   - parameter type tokens accept v3 aliases (point/vector/normal/color) AND v4 strict
//     (point3/vector3/normal3/rgb).
//   - both the v3 env-light param `mapname` and the v4 `filename` are read.
// See PbrtSceneConverter for the DTO -> Ballistic .scene translation (coordinate fix, materials).
public sealed class PbrtSceneData {
    public PbrtCamera Camera { get; set; }
    public List<PbrtMesh> Meshes { get; } = new();
    public Dictionary<string, PbrtMaterial> Materials { get; } = new();
    public Dictionary<string, PbrtTexture> Textures { get; } = new();
    public List<PbrtLight> Lights { get; } = new();
    public string EnvMapPath { get; set; }       // infinite light image, relative to the root .pbrt
    public bool EnvMapEqualArea { get; set; }    // v4 equal-area mapping vs v3 equirect (informational)
}

public sealed class PbrtCamera {
    public Matrix4 CameraToWorld = Matrix4.Identity; // inverse of the pre-WorldBegin CTM
    public float FovYDegrees = 45f;                  // pbrt `fov` is along the SHORTER image axis
    public int XResolution = 1280;
    public int YResolution = 720;
}

// A single shape placed in the world. Either references an external .ply (PlyFile set) or carries
// inline triangle-mesh data (Positions/Indices set) that the converter writes out as a sibling .ply.
public sealed class PbrtMesh {
    public string PlyFile;                 // absolute path to an external .ply, or null
    public List<float> Positions;          // inline trianglemesh: flat x y z, or null
    public List<float> Normals;            // flat x y z (optional)
    public List<float> Uvs;                // flat u v (optional)
    public List<int> Indices;              // flat triangle indices
    public Matrix4 ObjectToWorld = Matrix4.Identity;
    public string MaterialName;            // key into Materials, or null
    public bool ReverseOrientation;
    public bool IsEmissive;                // had an AreaLightSource in scope
    public Vector3 EmissiveRadiance = Vector3.Zero;
}

public sealed class PbrtMaterial {
    public string Type = "diffuse";        // diffuse/coateddiffuse/conductor/dielectric/...
    public Vector3? Reflectance;           // constant rgb albedo
    public string ReflectanceTexture;      // texture name bound to reflectance, or null
    public float? Roughness;
    public string NormalMap;               // direct file path (v4 "string normalmap"), or null
    public float? Eta;                     // IOR for dielectric/conductor
    public bool IsMetal;
    public bool IsTransparent;
}

public sealed class PbrtTexture {
    public string Class = "imagemap";      // imagemap/scale/constant/...
    public string FileName;                // image path (imagemap), relative to the .pbrt
    public string InnerTexture;            // for "scale": the wrapped texture name
}

public sealed class PbrtLight {
    public string Type;                    // distant/point/spot/infinite
    public Matrix4 LightToWorld = Matrix4.Identity;
    public Vector3 Color = Vector3.One;    // L or I, already separated from intensity
    public float Intensity = 1f;
    public Vector3 From = Vector3.Zero;    // distant/spot `from`
    public Vector3 To = new(0, 0, 1);      // distant/spot `to`
    public float ConeAngleDegrees = 30f;   // spot
    public float ConeDeltaDegrees = 5f;    // spot falloff
}

public static class PbrtSceneParser {
    const int MaxIncludeDepth = 32;

    // Parses the root .pbrt file (following Include/Import relative to each file's directory).
    public static PbrtSceneData Parse(string rootFilePath) {
        var data = new PbrtSceneData();
        var state = new ParserState(data, rootFilePath);
        ParseFile(rootFilePath, state, 0);
        return data;
    }

    static void ParseFile(string filePath, ParserState state, int depth) {
        if (depth > MaxIncludeDepth) {
            Debugging.LogWarning($"pbrt: include depth limit hit at '{filePath}'; skipping.");
            return;
        }
        if (!File.Exists(filePath)) {
            Debugging.LogWarning($"pbrt: referenced file not found: '{filePath}'.");
            return;
        }

        var tokens = new Tokenizer(File.ReadAllText(filePath));
        var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;

        while (tokens.TryNext(out Token tok)) {
            if (tok.Kind != TokenKind.Identifier) continue; // stray brackets/values between directives: skip
            DispatchDirective(tok.Text, tokens, state, fileDir, depth);
        }
    }

    static void DispatchDirective(string directive, Tokenizer t, ParserState s, string fileDir, int depth) {
        switch (directive) {
            // ---- transforms ----
            case "Identity": s.Ctm = Matrix4.Identity; break;
            case "Translate": {
                var v = t.ReadFloats(3);
                s.Ctm = Matrix4.CreateTranslation(v[0], v[1], v[2]) * s.Ctm;
                break;
            }
            case "Scale": {
                var v = t.ReadFloats(3);
                s.Ctm = Matrix4.CreateScale(v[0], v[1], v[2]) * s.Ctm;
                break;
            }
            case "Rotate": {
                var v = t.ReadFloats(4); // angle(deg) x y z
                var axis = new Vector3(v[1], v[2], v[3]);
                if (axis.LengthSquared > 1e-12f)
                    s.Ctm = Matrix4.CreateFromAxisAngle(axis.Normalized(), MathHelper.DegreesToRadians(v[0])) * s.Ctm;
                break;
            }
            case "LookAt": {
                var v = t.ReadFloats(9);
                var eye = new Vector3(v[0], v[1], v[2]);
                var look = new Vector3(v[3], v[4], v[5]);
                var up = new Vector3(v[6], v[7], v[8]);
                // pbrt LookAt yields camera->world; pbrt then uses its INVERSE as the CTM (world->camera).
                s.Ctm = LookAtWorldToCamera(eye, look, up) * s.Ctm;
                break;
            }
            case "Transform": {
                var m = t.ReadFloatArray(16);
                s.Ctm = ColumnMajor(m);
                break;
            }
            case "ConcatTransform": {
                var m = t.ReadFloatArray(16);
                s.Ctm = ColumnMajor(m) * s.Ctm;
                break;
            }
            case "CoordinateSystem": s.NamedTransforms[t.ReadQuotedString()] = s.Ctm; break;
            case "CoordSysTransform": {
                var name = t.ReadQuotedString();
                if (s.NamedTransforms.TryGetValue(name, out var m)) s.Ctm = m;
                break;
            }

            // ---- scoping ----
            case "AttributeBegin": s.PushFull(); break;
            case "AttributeEnd": s.PopFull(); break;
            case "TransformBegin": s.PushTransformOnly(); break;   // v3 only
            case "TransformEnd": s.PopTransformOnly(); break;      // v3 only
            case "ObjectBegin": s.BeginObject(t.ReadQuotedString()); break;
            case "ObjectEnd": s.EndObject(); break;
            case "ObjectInstance": s.InstantiateObject(t.ReadQuotedString()); break;

            case "WorldBegin": s.Ctm = Matrix4.Identity; s.InWorld = true; break;
            case "WorldEnd": break; // v3 terminator; v4 has none — no-op

            // ---- rendering options (camera section) ----
            case "Camera": ParseCamera(t, s); break;
            case "Film": ParseFilm(t, s); break;
            case "Sampler":
            case "Integrator":
            case "PixelFilter":
            case "Filter":
            case "Accelerator":
            case "ColorSpace":
            case "Option":
            case "MakeNamedMedium":
            case "MediumInterface":
            case "TransformTimes":
            case "ActiveTransform":
                t.SkipImplAndParams(); // type token (if any) + param list, discarded
                break;

            // ---- world contents ----
            case "Material": ParseMaterial(t, s, anonymous: true); break;
            case "MakeNamedMaterial": ParseMaterial(t, s, anonymous: false); break;
            case "NamedMaterial": s.CurrentMaterial = t.ReadQuotedString(); break;
            case "Texture": ParseTexture(t, s); break;
            case "Shape": ParseShape(t, s, fileDir); break;
            case "LightSource": ParseLightSource(t, s, fileDir); break;
            case "AreaLightSource": ParseAreaLight(t, s); break;
            case "ReverseOrientation": s.ReverseOrientation = !s.ReverseOrientation; break;
            case "Attribute": ParseAttribute(t, s); break;

            // ---- includes ----
            case "Include":
            case "Import": {
                var rel = t.ReadQuotedString();
                var inc = ResolvePath(fileDir, rel);
                ParseFile(inc, s, depth + 1);
                break;
            }

            default:
                // Unknown directive: it may be followed by params we must consume so we don't
                // mis-read its values as directives. Skip an optional impl token + param list.
                t.SkipImplAndParams();
                break;
        }
    }

    // ---- directive parsers ----

    static void ParseCamera(Tokenizer t, ParserState s) {
        t.TryReadImplName(out _); // "perspective"/"realistic"/...
        var p = t.ReadParams();
        // Before WorldBegin the CTM is world->camera; the camera's world transform is its inverse.
        var worldToCamera = s.Ctm;
        s.Data.Camera ??= new PbrtCamera();
        if (worldToCamera.Determinant != 0f) {
            var inv = worldToCamera;
            inv.Invert();
            s.Data.Camera.CameraToWorld = inv;
        }
        if (p.TryFloat("fov", out var fov)) s.Data.Camera.FovYDegrees = fov;
    }

    static void ParseFilm(Tokenizer t, ParserState s) {
        t.TryReadImplName(out _); // "rgb"/"image"/...
        var p = t.ReadParams();
        s.Data.Camera ??= new PbrtCamera();
        if (p.TryInt("xresolution", out var x)) s.Data.Camera.XResolution = x;
        if (p.TryInt("yresolution", out var y)) s.Data.Camera.YResolution = y;
    }

    static void ParseMaterial(Tokenizer t, ParserState s, bool anonymous) {
        // Material "<type>" <params>   |   MakeNamedMaterial "<name>" "string type" "<type>" <params>
        string name, type;
        if (anonymous) {
            t.TryReadImplName(out type);
            type ??= "diffuse";
            name = $"__anon_{s.AnonCounter++}";
        }
        else {
            name = t.ReadQuotedString();
            type = null; // filled from the "string type" param below
        }

        var p = t.ReadParams();
        type ??= p.GetString("type") ?? "diffuse";

        var mat = new PbrtMaterial { Type = type.ToLowerInvariant() };
        ApplyMaterialParams(mat, p);
        s.Data.Materials[name] = mat;
        if (anonymous) s.CurrentMaterial = name;
    }

    static void ApplyMaterialParams(PbrtMaterial mat, ParamSet p) {
        if (p.TryRgb("reflectance", out var refl)) mat.Reflectance = refl;
        else if (p.TryRgb("Kd", out var kd)) mat.Reflectance = kd;          // v3 matte/uber
        if (p.GetTextureRef("reflectance", out var rtex)) mat.ReflectanceTexture = rtex;
        else if (p.GetTextureRef("Kd", out var kdtex)) mat.ReflectanceTexture = kdtex;
        if (p.TryFloat("roughness", out var rough)) mat.Roughness = rough;
        if (p.TryFloat("uroughness", out var ur)) mat.Roughness = ur;        // anisotropic: take one axis
        if (p.TryFloat("eta", out var eta)) mat.Eta = eta;
        var nm = p.GetString("normalmap");
        if (nm != null) mat.NormalMap = nm;

        switch (mat.Type) {
            case "conductor":
            case "metal":
            case "mirror":
            case "coatedconductor":
                mat.IsMetal = true;
                break;
            case "dielectric":
            case "glass":
            case "thindielectric":
                mat.IsTransparent = true;
                break;
        }
    }

    static void ParseTexture(Tokenizer t, ParserState s) {
        // Texture "<name>" "<float|spectrum>" "<class>" <params>
        var name = t.ReadQuotedString();
        t.ReadQuotedString();                  // output type (float/spectrum) — not needed in v1
        t.TryReadImplName(out var cls);        // class
        var p = t.ReadParams();

        var tex = new PbrtTexture { Class = (cls ?? "imagemap").ToLowerInvariant() };
        tex.FileName = p.GetString("filename");
        if (tex.Class == "scale" || tex.Class == "mix")
            p.GetTextureRef("tex", out tex.InnerTexture);
        s.Data.Textures[name] = tex;
    }

    static void ParseShape(Tokenizer t, ParserState s, string fileDir) {
        t.TryReadImplName(out var type);
        var p = t.ReadParams();
        type = (type ?? "").ToLowerInvariant();

        var mesh = new PbrtMesh {
            ObjectToWorld = s.Ctm,
            MaterialName = s.CurrentMaterial,
            ReverseOrientation = s.ReverseOrientation,
            IsEmissive = s.AreaLightActive,
            EmissiveRadiance = s.AreaLightRadiance,
        };

        if (type == "plymesh") {
            var rel = p.GetString("filename");
            if (rel == null) return;
            mesh.PlyFile = ResolvePath(fileDir, rel);
        }
        else if (type == "trianglemesh" || type == "loopsubdiv") {
            // loopsubdiv has the same P/indices layout as trianglemesh — it's the CONTROL CAGE of a
            // Loop subdivision surface. We import the cage directly (a faceted low-poly version);
            // doing real Loop subdivision is out of scope, but importing the cage beats dropping the
            // hero asset entirely (many pbrt scenes use loopsubdiv for characters).
            mesh.Positions = p.GetFloatList("P");
            mesh.Indices = p.GetIntList("indices");
            mesh.Normals = p.GetFloatList("N");
            mesh.Uvs = p.GetFloatList("uv") ?? p.GetFloatList("st"); // v3 used st/float uv
            if (mesh.Positions == null || mesh.Indices == null) return;
        }
        else {
            // sphere/disk/curve/cylinder/bilinearmesh/... — no triangle data to reference. Skipped in
            // v1 (an emissive sphere area-light still has no mesh to place).
            return;
        }

        s.AddShape(mesh);
    }

    static void ParseLightSource(Tokenizer t, ParserState s, string fileDir) {
        t.TryReadImplName(out var type);
        var p = t.ReadParams();
        type = (type ?? "").ToLowerInvariant();

        if (type == "infinite") {
            var file = p.GetString("filename") ?? p.GetString("mapname"); // v4 / v3
            if (file != null) {
                s.Data.EnvMapPath = ResolvePath(fileDir, file);
                s.Data.EnvMapEqualArea = p.GetString("filename") != null;
            }
            return;
        }

        var light = new PbrtLight { Type = type, LightToWorld = s.Ctm };
        if (p.TryRgb("L", out var L)) Separate(L, out light.Color, out light.Intensity);
        else if (p.TryRgb("I", out var I)) Separate(I, out light.Color, out light.Intensity);
        if (p.TryFloat("scale", out var sc)) light.Intensity *= sc;
        if (p.TryPoint("from", out var from)) light.From = from;
        if (p.TryPoint("to", out var to)) light.To = to;
        if (p.TryFloat("coneangle", out var ca)) light.ConeAngleDegrees = ca;
        if (p.TryFloat("conedeltaangle", out var cd)) light.ConeDeltaDegrees = cd;
        s.Data.Lights.Add(light);
    }

    static void ParseAreaLight(Tokenizer t, ParserState s) {
        t.TryReadImplName(out _); // "diffuse"
        var p = t.ReadParams();
        s.AreaLightActive = true;
        s.AreaLightRadiance = p.TryRgb("L", out var L) ? L : Vector3.One;
    }

    static void ParseAttribute(Tokenizer t, ParserState s) {
        // v4: Attribute "target" <params> — sets inheritable defaults. v1 reads it only for
        // "light"/"material" reverseorientation; otherwise consume + ignore.
        t.ReadQuotedString();
        t.ReadParams();
    }

    // ---- helpers ----

    static void Separate(Vector3 v, out Vector3 color, out float intensity) {
        intensity = MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        color = intensity > 1e-6f ? v / intensity : Vector3.One;
    }

    static string ResolvePath(string fileDir, string rel) =>
        Path.GetFullPath(rel.Replace('\\', '/').StartsWith('/') || Path.IsPathRooted(rel)
            ? rel
            : Path.Combine(fileDir, rel));

    // pbrt Transform/ConcatTransform are column-major 4x4 (translation in the last 4 values). OpenTK's
    // Matrix4 is row-major with row-vector convention (v * M); transpose the column-major data into it.
    static Matrix4 ColumnMajor(float[] m) => new Matrix4(
        m[0], m[1], m[2], m[3],
        m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11],
        m[12], m[13], m[14], m[15]);

    // World->camera matrix (the pbrt CTM before WorldBegin) for a LookAt. pbrt is left-handed with the
    // view direction down +z. We build camera->world from the basis then invert.
    static Matrix4 LookAtWorldToCamera(Vector3 eye, Vector3 look, Vector3 up) {
        Vector3 dir = (look - eye);
        dir = dir.LengthSquared > 1e-12f ? dir.Normalized() : new Vector3(0, 0, 1);
        Vector3 upN = up.LengthSquared > 1e-12f ? up.Normalized() : Vector3.UnitY;
        Vector3 right = Vector3.Cross(upN, dir);              // left-handed: up x dir
        if (right.LengthSquared < 1e-12f) { upN = Vector3.UnitX; right = Vector3.Cross(upN, dir); }
        right = right.Normalized();
        Vector3 newUp = Vector3.Cross(dir, right);

        // camera->world: columns are right, newUp, dir, eye. In OpenTK row-vector form those are rows.
        var cameraToWorld = new Matrix4(
            right.X, right.Y, right.Z, 0,
            newUp.X, newUp.Y, newUp.Z, 0,
            dir.X, dir.Y, dir.Z, 0,
            eye.X, eye.Y, eye.Z, 1);
        cameraToWorld.Invert();
        return cameraToWorld; // = world->camera (the CTM)
    }
}
