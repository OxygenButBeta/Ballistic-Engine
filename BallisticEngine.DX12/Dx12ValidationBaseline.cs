using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vortice.Direct3D12.Debug;

namespace BallisticEngine.DX12;

// W2 of the dx12-passgraph plan — the VALIDATION BASELINE.
//
// The pass-graph migration drops the byte-identical gate; the D3D12 debug layer + GPU-Based Validation
// (GBV) replaces it for the BARRIER/STATE bug class. But GBV+debug-layer on TODAY's un-refactored code
// already emits warnings/errors (benign or known). So the gate is NOT "zero validation errors" — it is
// "zero NEW validation errors vs a captured baseline" (R-NEW-4). This class is that baseline:
//
//   1. NORMALIZE each D3D12 message to a SIGNATURE (category|id|normalized-text) — addresses, handles,
//      pointers, hex, and allocation-order-dependent numbers stripped — so the same logical message
//      matches run-to-run despite embedded VAs/handles (raw-string allowlisting drowns the gate).
//   2. Capture the baseline allowlist = the set of signatures the un-refactored renderer emits, PINNED
//      to its substrate (commit + GPU + driver + Graphics-Tools-installed fact) — a driver bump changes
//      the message set and invalidates the allowlist (R-NEW-6).
//   3. At verification time, partition drained messages into KNOWN (in baseline) vs NEW; the gate fails
//      loud on any NEW Corruption/Error (W4 — "fail loud," but baseline-aware, not blanket
//      break-on-severity).
//
// The baseline JSON lives at Docs/Validation/dx12-gbv-baseline.json (committed). Capture it with
// BALLISTIC_DX12_GBV_CAPTURE_BASELINE=<path> on a GBV run; read it back with BALLISTIC_DX12_GBV_BASELINE
// (or the default repo path resolved from BALLISTIC_ENGINE_ROOT).
public static class Dx12ValidationBaseline {
    // ---- Signature normalization ------------------------------------------------------------------

    // Hex literals (0x… addresses/handles/VAs), bracketed object names ([ ID3D12Resource ... ]),
    // and bare large numbers (allocation-order counters, byte sizes) are the run-to-run-variable parts
    // of a D3D12 message. Strip/canonicalize them so two runs of the SAME logical error collapse to one
    // signature. Order matters: hex before bare-number (0x1A is hex, not number 1 followed by A).
    static readonly Regex HexLiteral   = new(@"0x[0-9A-Fa-f]+", RegexOptions.Compiled);
    // Quoted/bracketed object labels often carry an auto-generated suffix or an address — keep the role
    // word but drop a trailing pointer/number. We keep the human name (resource ROLE) and strip only the
    // variable tail; the simplest robust rule is to canonicalize any remaining standalone long number.
    static readonly Regex BareNumber   = new(@"(?<![A-Za-z0-9_])\d{3,}(?![A-Za-z0-9_])", RegexOptions.Compiled);
    static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // Canonicalize a raw D3D12 description string: collapse the variable parts to placeholders so the
    // SIGNATURE is stable. (Category + Id already disambiguate the message CLASS; the normalized text
    // preserves the resource ROLE — e.g. which target — while dropping its address.)
    public static string NormalizeDescription(string description) {
        if (string.IsNullOrEmpty(description)) return "";
        // Vortice's Message.Description carries the native C-string's trailing NUL (and occasionally
        // embedded control chars); strip them first so the signature is clean text.
        string s = description.Replace("\0", "").Trim();
        s = HexLiteral.Replace(s, "0x#");        // any address/handle/VA -> 0x#
        s = BareNumber.Replace(s, "#");          // long bare numbers (sizes, alloc order) -> #
        s = WhitespaceRun.Replace(s, " ").Trim();
        return s;
    }

    // The full signature = "CATEGORY|ID|normalized-text". Category+Id give the message class; the
    // normalized text keeps the resource role so two DIFFERENT resources tripping the same ID stay
    // distinct (R-NEW-4: "category + message ID + resource ROLE/name, addresses/handles stripped").
    public static string Signature(MessageCategory category, MessageId id, string description) =>
        $"{category}|{id}|{NormalizeDescription(description)}";

    public static string Signature(in DebugMessage m) => Signature(m.Category, m.Id, m.Description);

    // ---- Baseline file model ----------------------------------------------------------------------

    // The on-disk baseline: the allowlisted signature set + the substrate it was captured on. Phase-2
    // gates read this; a substrate mismatch (driver bump / GPU swap) means REGENERATE before trusting it.
    public sealed class BaselineFile {
        public string Note { get; set; } =
            "DX12 GBV/debug-layer validation baseline (W2). Signatures here are the un-refactored " +
            "renderer's KNOWN messages; the pass-graph gate fails only on signatures NOT in this set. " +
            "Regenerate (BALLISTIC_DX12_GBV_CAPTURE_BASELINE) when Substrate changes — driver/GPU bumps " +
            "alter the message set.";
        public SubstrateInfo Substrate { get; set; } = new();
        // Sorted, de-duplicated signature list (deterministic file → reproducible diffs).
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

    // ---- Default baseline path resolution ---------------------------------------------------------

    // The committed baseline lives at <repo>/Docs/Validation/dx12-gbv-baseline.json. Resolve it from
    // BALLISTIC_DX12_GBV_BASELINE if set, else from BALLISTIC_ENGINE_ROOT, else by walking up from the
    // running exe to the BallisticEngine.slnx marker (same scheme RenderCommand.FindPlayerExe uses).
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

    // ---- The drain-and-print + baseline gate (the W2/W4 entry point) ------------------------------

    // Result of partitioning a drained message set against the baseline. The headless render path prints
    // a Report() and, if NewErrorCount>0 under break-on-error, fails loud.
    public sealed class DrainResult {
        public int Total;
        public int KnownCount;        // signatures present in the baseline allowlist
        public int NewCount;          // signatures NOT in the baseline
        public int NewErrorCount;     // of NewCount, those at Corruption/Error severity (the gate)
        public List<DebugMessage> NewMessages = new();
        public List<DebugMessage> KnownMessages = new();
        public bool BaselineLoaded;
        public string BaselinePath;
    }

    // Partition `messages` into known/new against `baseline` (null baseline → every message is NEW).
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

    // Human-readable report of a drain partition (printed to stderr by the headless render path so the
    // CLI surfaces it — `bal render` discards the player's stdout but forwards stderr).
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
        // Always print the NEW set in full (it's the actionable part); summarize known by signature count.
        foreach (DebugMessage m in r.NewMessages)
            sb.Append("  NEW  ").Append(m.Severity).Append(' ').Append(m.Category).Append(' ')
              .Append(m.Id).Append(": ").Append(m.Description).Append('\n');
        return sb.ToString();
    }

    // The full headless entry point: drain the device's info queue, partition against the resolved
    // baseline, print the report to stderr, optionally CAPTURE a new baseline file, and FAIL LOUD on
    // NEW error-class messages when break-on-error is set. Returns the count of NEW error-class messages
    // (0 = the gate passed). No-op (returns 0) when the device has no info queue (normal `bal render` —
    // keeps it byte-identical and silent). Fully guarded: validation reporting must never crash a render.
    public static int DrainReportAndGate(Dx12Device dev) {
        try {
            if (dev is null || !dev.HasInfoQueue) return 0;   // debug layer / GBV not engaged → silent, unchanged

            IReadOnlyList<DebugMessage> messages = dev.DrainDebugMessagesStructured();

            // Optional capture mode: BALLISTIC_DX12_GBV_CAPTURE_BASELINE=<path> writes the current message
            // set AS the baseline (pinned to this substrate). This is the W2 capture step; it does NOT gate.
            string capturePath = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GBV_CAPTURE_BASELINE");
            if (!string.IsNullOrWhiteSpace(capturePath)) {
                CaptureBaseline(dev, messages, capturePath);
                Console.Error.WriteLine($"[DX12-Validation] captured {messages.Count} message(s) " +
                    $"({messages.Select(m => Signature(m)).Distinct().Count()} unique signatures) to baseline {capturePath}");
                // Still print the report so the operator sees what was captured.
            }

            string baselinePath = ResolveBaselinePath();
            BaselineFile baseline = baselinePath is null ? null : Load(baselinePath);
            DrainResult r = Partition(messages, baseline, baselinePath);

            if (r.Total > 0 || baseline is not null)
                Console.Error.Write(Report(r));

            // W4 — fail loud on NEW error-class messages, but ONLY when break-on-error is requested
            // (BALLISTIC_DX12_BREAK_ON_ERROR=1). Baseline-aware: known errors do NOT trip the gate.
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

    // Write the current message set as a baseline file pinned to the running substrate. The substrate
    // fields (GPU, driver, D3D12SDKLayers version, OS) are filled best-effort; Commit must be set by the
    // operator via BALLISTIC_DX12_GBV_BASELINE_COMMIT (the running build doesn't know its own git hash).
    //
    // The baseline is captured ACROSS THE COVERAGE MATRIX (every scene surfaces a different subset of the
    // pipeline's validation messages). So capture MERGES (unions signatures) into an existing file at the
    // same path rather than overwriting — run SkyTest then CornellBox then BistroInterior then
    // BistroExterior with the same BALLISTIC_DX12_GBV_CAPTURE_BASELINE and the allowlist accumulates. The
    // substrate is re-stamped each run (the last run wins; all matrix runs share one substrate by design).
    static void CaptureBaseline(Dx12Device dev, IReadOnlyList<DebugMessage> messages, string path) {
        BaselineFile bf = Load(path) ?? new BaselineFile();
        bf.Substrate = ProbeSubstrate(dev);
        bf.Signatures.AddRange(messages.Select(m => Signature(m)));   // Save() de-dups + sorts
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

// A plain-data copy of a single drained D3D12 debug message (Vortice's Message is a ref-struct-ish
// interop type we don't want to leak past the device; this is the durable form the baseline logic and
// the drain-and-print consume).
public readonly struct DebugMessage {
    public MessageCategory Category { get; }
    public MessageSeverity Severity { get; }
    public MessageId Id { get; }
    public string Description { get; }
    public DebugMessage(MessageCategory category, MessageSeverity severity, MessageId id, string description) {
        Category = category; Severity = severity; Id = id; Description = description;
    }
    public bool IsErrorClass => Severity == MessageSeverity.Corruption || Severity == MessageSeverity.Error;
}
