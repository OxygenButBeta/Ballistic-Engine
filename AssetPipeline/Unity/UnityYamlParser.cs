using System.Globalization;

namespace BallisticEngine.AssetPipeline.Unity;

public static class UnityYamlParser {
    public static UnityYamlScene Parse(string text) {
        var scene = new UnityYamlScene();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');

        var i = 0;
        while (i < lines.Length) {
            var line = lines[i];
            if (!line.StartsWith("--- !u!", StringComparison.Ordinal)) {
                i++;
                continue;
            }

            (int classId, long fileId) = ParseHeader(line);
            i++;

            var start = i;
            while (i < lines.Length && !lines[i].StartsWith("--- ", StringComparison.Ordinal))
                i++;
            ArraySegment<string> body = new(lines, start, i - start);

            switch (classId) {
                case 1: ParseGameObject(scene, fileId, body); break;
                case 4: case 224: ParseTransform(scene, fileId, body); break;
                case 33: ParseMeshFilter(scene, fileId, body); break;
                case 23: ParseMeshRenderer(scene, fileId, body); break;
                case 1001: ParsePrefabInstance(scene, fileId, body); break;
                case 205: ParseLodGroup(scene, fileId, body); break;
            }
        }

        ResolvePrefabRoot(scene);
        return scene;
    }

    static void ParseGameObject(UnityYamlScene scene, long fileId, ArraySegment<string> body) {
        var go = new UnityGameObject { FileId = fileId };
        var inComponents = false;

        foreach (var raw in body) {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("m_Name:", StringComparison.Ordinal))
                go.Name = ValueAfter(trimmed, "m_Name:").Trim().Trim('\'', '"');
            else if (trimmed.StartsWith("m_IsActive:", StringComparison.Ordinal))
                go.Active = ValueAfter(trimmed, "m_IsActive:").Trim() != "0";
            else if (trimmed.StartsWith("m_Component:", StringComparison.Ordinal))
                inComponents = true;
            else if (inComponents && trimmed.Contains("component:", StringComparison.Ordinal)) {
                UnityRef comp = ParseRef(trimmed);
                if (comp.FileId != 0)
                    go.ComponentIds.Add(comp.FileId);
            }
            else if (inComponents && line.Length > 0 && !char.IsWhiteSpace(line[0]))
                inComponents = false;
        }

        scene.GameObjects[fileId] = go;
    }

    static void ParseTransform(UnityYamlScene scene, long fileId, ArraySegment<string> body) {
        var t = new UnityTransform { FileId = fileId };
        var inChildren = false;

        foreach (var raw in body) {
            var trimmed = raw.Trim();

            if (trimmed.StartsWith("m_GameObject:", StringComparison.Ordinal))
                t.GameObjectId = ParseRef(trimmed).FileId;
            else if (trimmed.StartsWith("m_Father:", StringComparison.Ordinal))
                t.FatherId = ParseRef(trimmed).FileId;
            else if (trimmed.StartsWith("m_LocalPosition:", StringComparison.Ordinal))
                t.LocalPosition = ParseVector3(trimmed);
            else if (trimmed.StartsWith("m_LocalRotation:", StringComparison.Ordinal))
                t.LocalRotation = ParseQuaternion(trimmed);
            else if (trimmed.StartsWith("m_LocalScale:", StringComparison.Ordinal))
                t.LocalScale = ParseVector3(trimmed);
            else if (trimmed.StartsWith("m_Children:", StringComparison.Ordinal))
                inChildren = true;
            else if (inChildren && trimmed.StartsWith("- {fileID:", StringComparison.Ordinal)) {
                long child = ParseRef(trimmed).FileId;
                if (child != 0) t.ChildIds.Add(child);
            }
            else if (inChildren && !trimmed.StartsWith("-", StringComparison.Ordinal)
                     && trimmed.Length > 0 && !trimmed.StartsWith("m_Children", StringComparison.Ordinal))
                inChildren = false;
        }

        scene.Transforms[fileId] = t;
    }

    static void ParseMeshFilter(UnityYamlScene scene, long fileId, ArraySegment<string> body) {
        var mf = new UnityMeshFilter { FileId = fileId };
        foreach (var raw in body) {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("m_GameObject:", StringComparison.Ordinal))
                mf.GameObjectId = ParseRef(trimmed).FileId;
            else if (trimmed.StartsWith("m_Mesh:", StringComparison.Ordinal))
                mf.Mesh = ParseRef(trimmed);
        }
        scene.MeshFilters[fileId] = mf;
    }

    static void ParseMeshRenderer(UnityYamlScene scene, long fileId, ArraySegment<string> body) {
        var mr = new UnityMeshRenderer { FileId = fileId };
        var inMaterials = false;

        foreach (var raw in body) {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("m_GameObject:", StringComparison.Ordinal))
                mr.GameObjectId = ParseRef(trimmed).FileId;
            else if (trimmed.StartsWith("m_Enabled:", StringComparison.Ordinal))
                mr.Enabled = ValueAfter(trimmed, "m_Enabled:").Trim() != "0";
            else if (trimmed.StartsWith("m_Materials:", StringComparison.Ordinal))
                inMaterials = true;
            else if (inMaterials && trimmed.StartsWith("- {fileID:", StringComparison.Ordinal))
                mr.Materials.Add(ParseRef(trimmed));
            else if (inMaterials && !trimmed.StartsWith("-", StringComparison.Ordinal)
                     && trimmed.Length > 0)
                inMaterials = false;
        }

        scene.MeshRenderers[fileId] = mr;
    }

    static void ParsePrefabInstance(UnityYamlScene scene, long fileId, ArraySegment<string> body) {
        var pi = new UnityPrefabInstance { FileId = fileId };
        float px = 0, py = 0, pz = 0;
        float rx = 0, ry = 0, rz = 0, rw = 1;
        float sx = 1, sy = 1, sz = 1;
        var sawPos = false; var sawRot = false; var sawScale = false;

        string pendingPath = null;

        foreach (var raw in body) {
            var trimmed = raw.Trim();

            if (trimmed.StartsWith("m_TransformParent:", StringComparison.Ordinal))
                pi.TransformParentId = ParseRef(trimmed).FileId;
            else if (trimmed.StartsWith("m_SourcePrefab:", StringComparison.Ordinal))
                pi.SourcePrefabGuid = ParseRef(trimmed).Guid;
            else if (trimmed.StartsWith("propertyPath:", StringComparison.Ordinal))
                pendingPath = ValueAfter(trimmed, "propertyPath:").Trim();
            else if (trimmed.StartsWith("value:", StringComparison.Ordinal) && pendingPath is not null) {
                var v = ValueAfter(trimmed, "value:").Trim();
                switch (pendingPath) {
                    case "m_Name": pi.Name = v.Trim('\'', '"'); break;
                    case "m_IsActive": pi.Active = v != "0"; break;
                    case "m_LocalPosition.x": px = ParseFloat(v); sawPos = true; break;
                    case "m_LocalPosition.y": py = ParseFloat(v); sawPos = true; break;
                    case "m_LocalPosition.z": pz = ParseFloat(v); sawPos = true; break;
                    case "m_LocalRotation.x": rx = ParseFloat(v); sawRot = true; break;
                    case "m_LocalRotation.y": ry = ParseFloat(v); sawRot = true; break;
                    case "m_LocalRotation.z": rz = ParseFloat(v); sawRot = true; break;
                    case "m_LocalRotation.w": rw = ParseFloat(v); sawRot = true; break;
                    case "m_LocalScale.x": sx = ParseFloat(v); sawScale = true; break;
                    case "m_LocalScale.y": sy = ParseFloat(v); sawScale = true; break;
                    case "m_LocalScale.z": sz = ParseFloat(v); sawScale = true; break;
                }
                pendingPath = null;
            }
        }

        if (sawPos) pi.LocalPosition = new Vector3(px, py, pz);
        if (sawRot) pi.LocalRotation = new Quaternion(rx, ry, rz, rw);
        if (sawScale) pi.LocalScale = new Vector3(sx, sy, sz);

        if (pi.SourcePrefabGuid is not null)
            scene.PrefabInstances[fileId] = pi;
    }

    static void ParseLodGroup(UnityYamlScene scene, long fileId, ArraySegment<string> body) { }

    static (int classId, long fileId) ParseHeader(string line) {
        var tagStart = line.IndexOf("!u!", StringComparison.Ordinal) + 3;
        var amp = line.IndexOf('&', tagStart);
        var classStr = amp > 0 ? line[tagStart..amp].Trim() : line[tagStart..].Trim();
        int.TryParse(classStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int classId);

        long fileId = 0;
        if (amp > 0) {
            var idStr = line[(amp + 1)..].Trim();
            var space = idStr.IndexOf(' ');
            if (space > 0) idStr = idStr[..space];
            long.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);
        }
        return (classId, fileId);
    }

    static UnityRef ParseRef(string text) {
        var brace = text.IndexOf('{');
        if (brace < 0) return default;
        var end = text.IndexOf('}', brace);
        if (end < 0) end = text.Length;
        var inner = text[(brace + 1)..end];

        long fileId = 0;
        string guid = null;
        foreach (var part in inner.Split(',')) {
            var kv = part.Split(':', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            var val = kv[1].Trim();
            if (key == "fileID") long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);
            else if (key == "guid") guid = val;
        }
        return new UnityRef(fileId, guid);
    }

    static Vector3 ParseVector3(string line) {
        (float x, float y, float z, float _) = ParseFloatBraces(line);
        return new Vector3(x, y, z);
    }

    static Quaternion ParseQuaternion(string line) {
        (float x, float y, float z, float w) = ParseFloatBraces(line);
        return new Quaternion(x, y, z, w);
    }

    static (float x, float y, float z, float w) ParseFloatBraces(string line) {
        var brace = line.IndexOf('{');
        var end = brace >= 0 ? line.IndexOf('}', brace) : -1;
        if (brace < 0 || end < 0) return (0, 0, 0, 0);
        float x = 0, y = 0, z = 0, w = 0;
        foreach (var part in line[(brace + 1)..end].Split(',')) {
            var kv = part.Split(':', 2);
            if (kv.Length != 2) continue;
            float v = ParseFloat(kv[1]);
            switch (kv[0].Trim()) {
                case "x": x = v; break;
                case "y": y = v; break;
                case "z": z = v; break;
                case "w": w = v; break;
            }
        }
        return (x, y, z, w);
    }

    static float ParseFloat(string s) =>
        float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

    static string ValueAfter(string line, string key) {
        var idx = line.IndexOf(key, StringComparison.Ordinal);
        return idx < 0 ? "" : line[(idx + key.Length)..];
    }

    static void ResolvePrefabRoot(UnityYamlScene scene) {
        foreach (UnityTransform t in scene.Transforms.Values) {
            if (t.FatherId == 0) {
                scene.PrefabRootGameObjectId = t.GameObjectId;
                return;
            }
        }
    }
}
