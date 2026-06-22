using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vortice.Direct3D12.Debug;

namespace BallisticEngine.DX12;

public static class Dx12ValidationBaseline {
    static readonly Regex HexLiteral   = new(@"0x[0-9A-Fa-f]+", RegexOptions.Compiled);

    static readonly Regex BareNumber   = new(@"(?<![A-Za-z0-9_])\d{3,}(?![A-Za-z0-9_])", RegexOptions.Compiled);
    static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public static string NormalizeDescription(string description) {
        if (string.IsNullOrEmpty(description)) return "";
        string s = description.Replace("\0", "").Trim();
        s = HexLiteral.Replace(s, "0x#");
        s = BareNumber.Replace(s, "#");
        s = WhitespaceRun.Replace(s, " ").Trim();
        return s;
    }

    public static string Signature(MessageCategory category, MessageId id, string description) =>
        $"{category}|{id}|{NormalizeDescription(description)}";

    public static string Signature(in DebugMessage m) => Signature(m.Category, m.Id, m.Description);

    public sealed class BaselineFile {
        public string Note { get; set; } =
            "DX12 GBV/debug-layer validation baseline (W2). Signatures here are the un-refactored " +
            "renderer's KNOWN messages; the pass-graph gate fails only on signatures NOT in this set. " +
            "Regenerate (BALLISTIC_DX12_GBV_CAPTURE_BASELINE) when Substrate changes — driver/GPU bumps " +
            "alter the message set.";
        public SubstrateInfo Substrate { get; set; } = new();
        public List<string> Signatures { get; set; } = new();
    }

    public sealed class SubstrateInfo {
        public string Commit { get; set; } = "";
        public string Gpu { get; set; } = "";
        public string DriverVersion { get; set; } = "";
        public bool GraphicsToolsInstalled { get; set; }
        public string D3D12SDKLayersVersion { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string CapturedUtc { get; set; } = "";
    }

    static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
    };

    public static BaselineFile Load(string path) {
        if (!File.Exists(path)) return null;
        try {
            return JsonSerializer.Deserialize<BaselineFile>(File.ReadAllText(path), JsonOpts);
        } catch (Exception e) {
            Console.Error.WriteLine($"[DX12] validation baseline at '{path}' failed to parse ({e.Message}); treating as absent.");
            return null;
        }
    }

    public static void Save(string path, BaselineFile baseline) {
        baseline.Signatures = baseline.Signatures.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(baseline, JsonOpts));
    }

    public const string RepoRelativePath = "Docs/Validation/dx12-gbv-baseline.json";

    public static string ResolveBaselinePath() {
        string env = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBV_BASELINE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string root = ResolveEngineRoot();
        return root is null ? null : Path.Combine(root, RepoRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    static string ResolveEngineRoot() {
        string root = Environment.GetEnvironmentVariable("BALLISTIC_ENGINE_ROOT");
        if (!string.IsNullOrWhiteSpace(root)) return root;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BallisticEngine.slnx")))
                return dir.FullName;
        return null;
    }

    public sealed class DrainResult {
        public int Total;
        public int KnownCount;
        public int NewCount;
        public int NewErrorCount;
        public List<DebugMessage> NewMessages = new();
        public List<DebugMessage> KnownMessages = new();
        public bool BaselineLoaded;
        public string BaselinePath;
    }

    public static DrainResult Partition(IReadOnlyList<DebugMessage> messages, BaselineFile baseline, string baselinePath) {
        var known = baseline is null ? new HashSet<string>() : new HashSet<string>(baseline.Signatures, StringComparer.Ordinal);
        var r = new DrainResult { Total = messages.Count, BaselineLoaded = baseline is not null, BaselinePath = baselinePath };
        foreach (DebugMessage m in messages) {
            if (known.Contains(Signature(m))) { r.KnownCount++; r.KnownMessages.Add(m); }
            else {
                r.NewCount++;
                r.NewMessages.Add(m);
                if (m.IsErrorClass) r.NewErrorCount++;
            }
        }
        return r;
    }

    public static string Report(DrainResult r) {
        var sb = new StringBuilder();
        sb.Append("[DX12-Validation] ").Append(r.Total).Append(" message(s): ")
          .Append(r.KnownCount).Append(" known(baseline), ")
          .Append(r.NewCount).Append(" NEW (").Append(r.NewErrorCount).Append(" error-class)");
        if (!r.BaselineLoaded)
            sb.Append("  [no baseline loaded — ALL treated as NEW; capture one with BALLISTIC_DX12_GBV_CAPTURE_BASELINE]");
        else
            sb.Append("  [baseline: ").Append(r.BaselinePath).Append(']');
        sb.Append('\n');
        foreach (DebugMessage m in r.NewMessages)
            sb.Append("  NEW  ").Append(m.Severity).Append(' ').Append(m.Category).Append(' ')
              .Append(m.Id).Append(": ").Append(m.Description).Append('\n');
        return sb.ToString();
    }

    public static int DrainReportAndGate(Dx12Device dev) {
        try {
            if (dev is null || !dev.HasInfoQueue) return 0;

            IReadOnlyList<DebugMessage> messages = dev.DrainDebugMessagesStructured();

            string capturePath = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBV_CAPTURE_BASELINE");
            if (!string.IsNullOrWhiteSpace(capturePath)) {
                CaptureBaseline(dev, messages, capturePath);
                Console.Error.WriteLine($"[DX12-Validation] captured {messages.Count} message(s) " +
                    $"({messages.Select(m => Signature(m)).Distinct().Count()} unique signatures) to baseline {capturePath}");
            }

            string baselinePath = ResolveBaselinePath();
            BaselineFile baseline = baselinePath is null ? null : Load(baselinePath);
            DrainResult r = Partition(messages, baseline, baselinePath);

            if (r.Total > 0 || baseline is not null)
                Console.Error.Write(Report(r));

            bool breakOnError = Environment.GetEnvironmentVariable("BALLISTIC_DX12_BREAK_ON_ERROR") == "1";
            if (breakOnError && r.NewErrorCount > 0) {
                Console.Error.WriteLine($"[DX12-Validation] FAIL: {r.NewErrorCount} NEW error-class validation message(s) " +
                    "not in the baseline (BALLISTIC_DX12_BREAK_ON_ERROR=1). The render path will report failure.");
            }
            return breakOnError ? r.NewErrorCount : 0;
        } catch (Exception e) {
            Console.Error.WriteLine("[DX12-Validation] drain/gate failed (continuing): " + e.Message);
            return 0;
        }
    }

    static void CaptureBaseline(Dx12Device dev, IReadOnlyList<DebugMessage> messages, string path) {
        BaselineFile bf = Load(path) ?? new BaselineFile();
        bf.Substrate = ProbeSubstrate(dev);
        bf.Signatures.AddRange(messages.Select(m => Signature(m)));
        Save(path, bf);
    }

    static SubstrateInfo ProbeSubstrate(Dx12Device dev) {
        var s = new SubstrateInfo {
            Commit = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBV_BASELINE_COMMIT") ?? "(set BALLISTIC_DX12_GBV_BASELINE_COMMIT)",
            OsVersion = Environment.OSVersion.Version.ToString(),
            CapturedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        try { s.Gpu = dev.AdapterDescription ?? ""; } catch { }
        try { s.DriverVersion = dev.AdapterDriverVersion ?? ""; } catch { }
        try {
            string layer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "D3D12SDKLayers.dll");
            if (File.Exists(layer)) {
                s.GraphicsToolsInstalled = true;
                s.D3D12SDKLayersVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(layer).FileVersion ?? "";
            }
        } catch { }
        return s;
    }
}
