using System.Text;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

public sealed class Dx12BarrierDeriver {
    public enum Role { GBufferColor, GBufferDepth, SceneColor }

    public sealed class PassPlan {
        public readonly string PassName;
        public readonly List<Dx12ResourceUsage> Usages;
        public readonly Dictionary<Role, ResourceStates> FinalStates = new();
        public PassPlan(string name, List<Dx12ResourceUsage> usages) { PassName = name; Usages = usages; }
    }

    readonly Dictionary<string, PassPlan> plans = new();

    public static (Role role, ResourceStates state) Map(Dx12ResourceUsage u) => u switch {
        Dx12ResourceUsage.GBufferShaderRead =>
            (Role.GBufferColor, ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource),
        Dx12ResourceUsage.GBufferDepthShaderRead => (Role.GBufferDepth, ResourceStates.PixelShaderResource),
        Dx12ResourceUsage.GBufferDepthReadOnly   => (Role.GBufferDepth, ResourceStates.DepthRead),
        Dx12ResourceUsage.SceneColorShaderRead   => (Role.SceneColor,   ResourceStates.PixelShaderResource),
        _ => (Role.GBufferColor, ResourceStates.Common),
    };

    public void Register(string passName, List<Dx12ResourceUsage> usages) {
        var plan = new PassPlan(passName, usages);
        foreach (var u in usages) {
            var (role, state) = Map(u);
            plan.FinalStates[role] = state;
        }
        plans[passName] = plan;
    }

    public bool IsMigrated(string passName) => plans.ContainsKey(passName);

    public void Emit(string passName, Dx12FrameContext ctx) {
        if (!plans.TryGetValue(passName, out var plan)) return;
        var gbuffer = ctx.GBuffer;
        foreach (var u in plan.Usages) {
            switch (u) {
                case Dx12ResourceUsage.GBufferShaderRead:      gbuffer.ToShaderResource(); break;
                case Dx12ResourceUsage.GBufferDepthShaderRead: gbuffer.DepthToShaderResource(); break;
                case Dx12ResourceUsage.GBufferDepthReadOnly:   gbuffer.DepthToReadOnly(); break;
                case Dx12ResourceUsage.SceneColorShaderRead:   ctx.SceneColor.ColorToShaderResource(); break;
            }
        }
    }

    public string CompareToManual(Dictionary<string, Dictionary<Role, ResourceStates>> manual, out bool unsound) {
        unsound = false;
        var sb = new StringBuilder();
        sb.AppendLine("[Dx12BarrierDeriver] V3 manual-vs-derived comparison (derived ⊇ manual + same final state):");
        foreach (var kv in manual) {
            string pass = kv.Key;
            var manualSet = kv.Value;
            if (!plans.TryGetValue(pass, out var plan)) {
                sb.AppendLine($"  {pass}: NOT migrated (manual head transitions still inline) — skipped.");
                continue;
            }
            foreach (var mkv in manualSet) {
                Role role = mkv.Key;
                ResourceStates wantState = mkv.Value;
                if (!plan.FinalStates.TryGetValue(role, out var gotState)) {
                    sb.AppendLine($"  UNSOUND {pass}: manual needs {role}->{wantState} but DERIVED set has NO {role} usage.");
                    unsound = true;
                } else if (gotState != wantState) {
                    sb.AppendLine($"  UNSOUND {pass}: {role} manual final={wantState} but DERIVED final={gotState}.");
                    unsound = true;
                } else {
                    sb.AppendLine($"  OK {pass}: {role} -> {gotState} (derived == manual).");
                }
            }

            foreach (var fkv in plan.FinalStates)
                if (!manualSet.ContainsKey(fkv.Key))
                    sb.AppendLine($"  NOTE {pass}: DERIVED adds {fkv.Key}->{fkv.Value} (superset of manual — allowed).");
        }
        return sb.ToString();
    }

    public static Dictionary<string, Dictionary<Role, ResourceStates>> ManualReference() {
        const ResourceStates GbCombined = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
        return new Dictionary<string, Dictionary<Role, ResourceStates>> {
            ["SSAO"] = new() { [Role.GBufferDepth] = ResourceStates.PixelShaderResource },
            ["AerialPerspective"] = new() { [Role.GBufferDepth] = ResourceStates.PixelShaderResource },
            ["Fog"]               = new() { [Role.GBufferDepth] = ResourceStates.PixelShaderResource },
            ["Sky"]               = new() { [Role.GBufferDepth] = ResourceStates.DepthRead },
            ["Transparents"]      = new() { [Role.GBufferDepth] = ResourceStates.DepthRead },
            ["Deferred"]          = new() { [Role.GBufferColor] = GbCombined },
            ["TAA"]               = new() { [Role.SceneColor] = ResourceStates.PixelShaderResource, [Role.GBufferDepth] = ResourceStates.PixelShaderResource },
            ["FSR"]               = new() {
                [Role.SceneColor] = ResourceStates.PixelShaderResource,
                [Role.GBufferDepth] = ResourceStates.PixelShaderResource,
            },
            ["Composite"]         = new() { [Role.SceneColor] = ResourceStates.PixelShaderResource },
            ["GI"]                = new() {
                [Role.SceneColor] = ResourceStates.PixelShaderResource,
                [Role.GBufferDepth] = ResourceStates.PixelShaderResource,
            },
            ["Reflections"]       = new() {
                [Role.SceneColor] = ResourceStates.PixelShaderResource,
                [Role.GBufferDepth] = ResourceStates.PixelShaderResource,
            },
        };
    }
}
