using OpenTK.Mathematics;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace BallisticEngine.Serialization;

// YamlDotNet doesn't know OpenTK's math structs; emit/parse them as flow maps,
// e.g. position: {x: 0, y: 1, z: 2}.

public sealed class Vector2YamlConverter : IYamlTypeConverter {
    public bool Accepts(Type type) => type == typeof(Vector2);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) {
        parser.Consume<MappingStart>();
        var v = new Vector2();
        while (!parser.TryConsume<MappingEnd>(out _)) {
            var key = parser.Consume<Scalar>().Value;
            var value = float.Parse(parser.Consume<Scalar>().Value, System.Globalization.CultureInfo.InvariantCulture);
            switch (key) { case "x": v.X = value; break; case "y": v.Y = value; break; }
        }
        return v;
    }

    public void WriteYaml(IEmitter emitter, object value, Type type, ObjectSerializer serializer) {
        var v = (Vector2)value;
        emitter.Emit(new MappingStart(null, null, true, MappingStyle.Flow));
        YamlScalar.Pair(emitter, "x", v.X); YamlScalar.Pair(emitter, "y", v.Y);
        emitter.Emit(new MappingEnd());
    }
}

public sealed class Vector3YamlConverter : IYamlTypeConverter {
    public bool Accepts(Type type) => type == typeof(Vector3);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) {
        parser.Consume<MappingStart>();
        var v = new Vector3();
        while (!parser.TryConsume<MappingEnd>(out _)) {
            var key = parser.Consume<Scalar>().Value;
            var value = float.Parse(parser.Consume<Scalar>().Value, System.Globalization.CultureInfo.InvariantCulture);
            switch (key) { case "x": v.X = value; break; case "y": v.Y = value; break; case "z": v.Z = value; break; }
        }
        return v;
    }

    public void WriteYaml(IEmitter emitter, object value, Type type, ObjectSerializer serializer) {
        var v = (Vector3)value;
        emitter.Emit(new MappingStart(null, null, true, MappingStyle.Flow));
        YamlScalar.Pair(emitter, "x", v.X); YamlScalar.Pair(emitter, "y", v.Y); YamlScalar.Pair(emitter, "z", v.Z);
        emitter.Emit(new MappingEnd());
    }
}

public sealed class QuaternionYamlConverter : IYamlTypeConverter {
    public bool Accepts(Type type) => type == typeof(Quaternion);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) {
        parser.Consume<MappingStart>();
        var q = Quaternion.Identity;
        while (!parser.TryConsume<MappingEnd>(out _)) {
            var key = parser.Consume<Scalar>().Value;
            var value = float.Parse(parser.Consume<Scalar>().Value, System.Globalization.CultureInfo.InvariantCulture);
            switch (key) {
                case "x": q.X = value; break; case "y": q.Y = value; break;
                case "z": q.Z = value; break; case "w": q.W = value; break;
            }
        }
        return q;
    }

    public void WriteYaml(IEmitter emitter, object value, Type type, ObjectSerializer serializer) {
        var q = (Quaternion)value;
        emitter.Emit(new MappingStart(null, null, true, MappingStyle.Flow));
        YamlScalar.Pair(emitter, "x", q.X); YamlScalar.Pair(emitter, "y", q.Y);
        YamlScalar.Pair(emitter, "z", q.Z); YamlScalar.Pair(emitter, "w", q.W);
        emitter.Emit(new MappingEnd());
    }
}

static class YamlScalar {
    public static void Pair(IEmitter emitter, string key, float value) {
        emitter.Emit(new Scalar(key));
        emitter.Emit(new Scalar(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
