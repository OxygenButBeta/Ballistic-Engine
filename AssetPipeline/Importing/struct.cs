using System.Diagnostics;

namespace BallisticEngine.AssetPipeline;

public readonly record struct RefreshResult(int Scanned, int Imported, int UpToDate, int Failed, long ElapsedMs) {
    public override string ToString() =>
        $"Asset refresh: {Scanned} scanned, {Imported} imported, {UpToDate} up to date, {Failed} failed ({ElapsedMs} ms)";
}
