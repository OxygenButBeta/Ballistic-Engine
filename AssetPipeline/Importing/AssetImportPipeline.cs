using System.Diagnostics;

namespace BallisticEngine.AssetPipeline;

public readonly record struct RefreshResult(int Scanned, int Imported, int UpToDate, int Failed, long ElapsedMs) {
    public override string ToString() =>
        $"Asset refresh: {Scanned} scanned, {Imported} imported, {UpToDate} up to date, {Failed} failed ({ElapsedMs} ms)";
}

// Walks Assets\, ensures every asset has a .meta (stable GUID), and (re)imports
// sources whose content, settings, or importer version changed since the last run.
public sealed class AssetImportPipeline {
    readonly BallisticProject project;
    readonly List<IAssetImporter> importers;

    readonly Dictionary<Guid, string> guidToPath = new();
    readonly Dictionary<string, Guid> pathToGuid = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Guid, MetaFile> metaByGuid = new();

    ArtifactDatabase database = new();

    public IReadOnlyDictionary<Guid, string> GuidToPath => guidToPath;
    public IReadOnlyDictionary<string, Guid> PathToGuid => pathToGuid;

    public AssetImportPipeline(BallisticProject project, IReadOnlyList<IAssetImporter> customImporters = null) {
        this.project = project;
        importers = customImporters is not null
            ? [.. customImporters]
            : [
                new ModelImporter(), new TextureImporter(), new FalcorSceneImporter(),
                new NativeAssetImporter(), new DefaultImporter()
            ];
    }

    public bool TryGetMeta(Guid guid, out MetaFile meta) => metaByGuid.TryGetValue(guid, out meta);

    public bool TryGetArtifactPath(Guid guid, out string absolutePath) {
        absolutePath = null;
        if (!database.Entries.TryGetValue(guid, out ArtifactRecord record) || record.Artifact is null)
            return false;

        absolutePath = Path.Combine(project.LibraryPath, record.Artifact);
        return File.Exists(absolutePath);
    }

    public RefreshResult Refresh() {
        var stopwatch = Stopwatch.StartNew();
        database = ArtifactDatabase.Load(project.ArtifactDatabasePath);

        // Importers can write new source assets during a pass (the model importer generates
        // .mat files, the Falcor importer a sibling .scene). Sweep again until a pass imports
        // nothing, so everything produced this run is registered before we return. Later passes
        // are cheap: unchanged assets take the size+mtime fast path.
        const int maxPasses = 4;
        var scanned = 0;
        var imported = 0;
        var upToDate = 0;
        var failed = 0;

        for (var pass = 0; pass < maxPasses; pass++) {
            (scanned, var passImported, upToDate, failed) = RefreshPass();
            imported += passImported;

            if (passImported == 0)
                break;
        }

        PruneOrphans();
        WarnOrphanedMetaFiles();
        database.Save(project.ArtifactDatabasePath);

        var result = new RefreshResult(scanned, imported, upToDate, failed, stopwatch.ElapsedMilliseconds);
        Debugging.Log(result.ToString());
        return result;
    }

    (int Scanned, int Imported, int UpToDate, int Failed) RefreshPass() {
        guidToPath.Clear();
        pathToGuid.Clear();
        metaByGuid.Clear();

        var scanned = 0;
        var imported = 0;
        var upToDate = 0;
        var failed = 0;

        foreach (var sourceAbsolute in Directory.EnumerateFiles(project.AssetsPath, "*", SearchOption.AllDirectories)) {
            if (sourceAbsolute.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                continue;

            scanned++;
            var assetPath = project.ToAssetPath(sourceAbsolute);
            MetaFile meta = EnsureMeta(sourceAbsolute, assetPath);

            guidToPath[meta.Guid] = assetPath;
            pathToGuid[assetPath] = meta.Guid;
            metaByGuid[meta.Guid] = meta;

            IAssetImporter importer = ResolveImporter(meta, sourceAbsolute);

            if (importer.ArtifactExtension is null && !importer.RunsWithoutArtifact) {
                upToDate++;
                continue;
            }

            try {
                if (ImportIfDirty(meta, importer, sourceAbsolute, assetPath))
                    imported++;
                else
                    upToDate++;
            }
            catch (Exception exception) {
                failed++;
                Debugging.LogError($"Import failed for '{assetPath}': {exception.Message}");
            }
        }

        return (scanned, imported, upToDate, failed);
    }

    MetaFile EnsureMeta(string sourceAbsolute, string assetPath) {
        var metaPath = MetaFile.PathFor(sourceAbsolute);

        MetaFile meta = null;
        if (File.Exists(metaPath)) {
            try {
                meta = MetaFile.Load(metaPath);
            }
            catch (Exception exception) {
                Debugging.LogWarning($"Meta file '{assetPath}.meta' is unreadable ({exception.Message}); recreating it.");
            }
        }

        if (meta is null || meta.Guid == Guid.Empty) {
            IAssetImporter importer = SelectImporterByExtension(sourceAbsolute);
            meta = new MetaFile {
                Guid = Guid.NewGuid(),
                Importer = importer.Name,
                Settings = importer.CreateDefaultSettings(assetPath),
            };
            meta.Save(metaPath);
            Debugging.Log($"Created meta for '{assetPath}' ({meta.Guid:N}, {importer.Name}).");
        }
        else if (guidToPath.TryGetValue(meta.Guid, out var existingPath)) {
            Debugging.LogWarning(
                $"Duplicate GUID {meta.Guid:N} on '{assetPath}' (already used by '{existingPath}'); regenerating.");
            meta.Guid = Guid.NewGuid();
            meta.Save(metaPath);
        }

        return meta;
    }

    IAssetImporter ResolveImporter(MetaFile meta, string sourceAbsolute) {
        IAssetImporter importer = importers.FirstOrDefault(candidate => candidate.Name == meta.Importer);
        if (importer is not null)
            return importer;

        importer = SelectImporterByExtension(sourceAbsolute);
        Debugging.LogWarning(
            $"Unknown importer '{meta.Importer}' in meta of '{project.ToAssetPath(sourceAbsolute)}'; using {importer.Name}.");
        return importer;
    }

    IAssetImporter SelectImporterByExtension(string sourceAbsolute) {
        var extension = Path.GetExtension(sourceAbsolute).ToLowerInvariant();
        return importers.First(candidate => candidate.CanImport(extension));
    }

    // Returns true when the asset was (re)imported, false when its output is already current.
    bool ImportIfDirty(MetaFile meta, IAssetImporter importer, string sourceAbsolute, string assetPath) {
        var settingsHash = meta.SettingsHash();
        var hasArtifact = importer.ArtifactExtension is not null;
        var artifactRelative = hasArtifact ? $"Artifacts/{meta.Guid:N}{importer.ArtifactExtension}" : null;
        var artifactAbsolute = hasArtifact ? Path.Combine(project.LibraryPath, artifactRelative) : null;
        var sourceInfo = new FileInfo(sourceAbsolute);

        database.Entries.TryGetValue(meta.Guid, out ArtifactRecord record);

        var dirty = record is null
                    || record.ImporterVersion != importer.Version
                    || record.SettingsHash != settingsHash
                    || (hasArtifact && !File.Exists(artifactAbsolute));

        string contentHash = null;
        if (!dirty) {
            if (record.FileSize == sourceInfo.Length && record.MtimeUtc == sourceInfo.LastWriteTimeUtc) {
                record.SourcePath = assetPath;
                return false;
            }

            contentHash = ContentHash.HashFile(sourceAbsolute);
            if (contentHash == record.ContentHash) {
                // Touched but unchanged; remember the new stamp so the next run takes the fast path.
                record.SourcePath = assetPath;
                record.FileSize = sourceInfo.Length;
                record.MtimeUtc = sourceInfo.LastWriteTimeUtc;
                return false;
            }
        }

        importer.Import(new AssetImportContext {
            SourceAbsolutePath = sourceAbsolute,
            AssetPath = assetPath,
            Guid = meta.Guid,
            Settings = meta.Settings,
            ArtifactAbsolutePath = artifactAbsolute,
        });

        database.Entries[meta.Guid] = new ArtifactRecord {
            SourcePath = assetPath,
            ContentHash = contentHash ?? ContentHash.HashFile(sourceAbsolute),
            SettingsHash = settingsHash,
            ImporterVersion = importer.Version,
            FileSize = sourceInfo.Length,
            MtimeUtc = sourceInfo.LastWriteTimeUtc,
            Artifact = artifactRelative,
        };
        return true;
    }

    void PruneOrphans() {
        List<Guid> orphans = database.Entries.Keys.Where(guid => !guidToPath.ContainsKey(guid)).ToList();
        foreach (Guid orphan in orphans) {
            ArtifactRecord record = database.Entries[orphan];
            Debugging.Log($"Source of '{record.SourcePath}' is gone; dropping its Library record.");
            database.Entries.Remove(orphan);

            var artifactAbsolute = record.Artifact is null ? null : Path.Combine(project.LibraryPath, record.Artifact);
            if (artifactAbsolute is not null && File.Exists(artifactAbsolute))
                File.Delete(artifactAbsolute);
        }
    }

    void WarnOrphanedMetaFiles() {
        foreach (var metaAbsolute in Directory.EnumerateFiles(project.AssetsPath, "*.meta", SearchOption.AllDirectories)) {
            var sourceAbsolute = metaAbsolute[..^".meta".Length];
            if (!File.Exists(sourceAbsolute))
                Debugging.LogWarning($"Orphaned meta '{project.ToAssetPath(metaAbsolute)}': its asset no longer exists.");
        }
    }
}
