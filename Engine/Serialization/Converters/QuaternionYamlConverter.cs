using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace BallisticEngine.Serialization;

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
