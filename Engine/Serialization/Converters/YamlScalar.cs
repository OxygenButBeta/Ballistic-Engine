using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace BallisticEngine.Serialization;

static class YamlScalar {
    public static void Pair(IEmitter emitter, string key, float value) {
        emitter.Emit(new Scalar(key));
        emitter.Emit(new Scalar(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
