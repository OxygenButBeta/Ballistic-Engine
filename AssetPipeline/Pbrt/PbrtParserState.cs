using OpenTK.Mathematics;

namespace BallisticEngine.AssetPipeline;

// The pbrt graphics state the parser threads through the directive stream. AttributeBegin/End push
// and pop the WHOLE state (CTM + current material + area-light + reverse-orientation); the v3-only
// TransformBegin/End push only the CTM. ObjectBegin/End records shapes under a name and ObjectInstance
// re-emits them at the current CTM (instancing).
sealed class ParserState {
    public readonly PbrtSceneData Data;
    public readonly Dictionary<string, Matrix4> NamedTransforms = new();

    public Matrix4 Ctm = Matrix4.Identity;
    public string CurrentMaterial;
    public bool ReverseOrientation;
    public bool AreaLightActive;
    public Vector3 AreaLightRadiance = Vector3.One;
    public bool InWorld;
    public int AnonCounter;

    // Object-instance capture: while a name is set, shapes are recorded into the buffer instead of
    // being emitted, and replayed (transformed) by each ObjectInstance.
    string capturingObject;
    readonly Dictionary<string, List<PbrtMesh>> objects = new();
    List<PbrtMesh> captureBuffer;

    readonly Stack<FullFrame> fullStack = new();
    readonly Stack<Matrix4> ctmStack = new();

    public ParserState(PbrtSceneData data, string rootFilePath) { Data = data; }

    public void PushFull() => fullStack.Push(new FullFrame {
        Ctm = Ctm, Material = CurrentMaterial, ReverseOrientation = ReverseOrientation,
        AreaLightActive = AreaLightActive, AreaLightRadiance = AreaLightRadiance,
    });

    public void PopFull() {
        if (fullStack.Count == 0) return;
        var f = fullStack.Pop();
        Ctm = f.Ctm; CurrentMaterial = f.Material; ReverseOrientation = f.ReverseOrientation;
        AreaLightActive = f.AreaLightActive; AreaLightRadiance = f.AreaLightRadiance;
    }

    public void PushTransformOnly() => ctmStack.Push(Ctm);
    public void PopTransformOnly() { if (ctmStack.Count > 0) Ctm = ctmStack.Pop(); }

    public void BeginObject(string name) {
        // ObjectBegin implicitly scopes graphics state (like AttributeBegin).
        PushFull();
        capturingObject = name;
        captureBuffer = new List<PbrtMesh>();
        objects[name] = captureBuffer;
    }

    public void EndObject() {
        capturingObject = null;
        captureBuffer = null;
        PopFull();
    }

    public void InstantiateObject(string name) {
        if (!objects.TryGetValue(name, out var shapes)) return;
        foreach (var proto in shapes) {
            // The prototype's transform was captured in the object's local space; compose with the
            // current CTM at the instance site.
            Data.Meshes.Add(new PbrtMesh {
                PlyFile = proto.PlyFile,
                Positions = proto.Positions, Normals = proto.Normals, Uvs = proto.Uvs, Indices = proto.Indices,
                ObjectToWorld = proto.ObjectToWorld * Ctm,
                MaterialName = proto.MaterialName,
                ReverseOrientation = proto.ReverseOrientation,
                IsEmissive = proto.IsEmissive,
                EmissiveRadiance = proto.EmissiveRadiance,
            });
        }
    }

    // Emit a parsed shape: into the active object capture buffer, or straight into the scene.
    public void AddShape(PbrtMesh mesh) {
        if (capturingObject != null) captureBuffer.Add(mesh);
        else Data.Meshes.Add(mesh);
    }

    struct FullFrame {
        public Matrix4 Ctm;
        public string Material;
        public bool ReverseOrientation;
        public bool AreaLightActive;
        public Vector3 AreaLightRadiance;
    }
}
