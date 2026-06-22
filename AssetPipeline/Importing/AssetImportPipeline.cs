using System.Diagnostics;

namespace BallisticEngine.AssetPipeline;

public sealed class AssetImportPipeline {
    readonly BallisticProject project;
    readonly List<IAssetImporter> importers;

    volatile Dictionary<Guid, string> guidToPath = new();
    volatile Dictionary<string, Guid> pathToGuid = new(StringComparer.OrdinalIgnoreCase);
    volatile Dictionary<Guid, MetaFile> metaByGuid = new();

    volatile ArtifactDatabase database = new();

    public IReadOnlyDictionary<Guid, string> GuidToPath => guidToPath;
    public IReadOnlyDictionary<string, Guid> PathToGuid => pathToGuid;
    public BallisticProject Project => project;

    public Action<string> Progress { get; set; }

    public Action<int, int> ProgressCount { get; set; }

    public AssetImportPipeline(BallisticProject project, IReadOnlyList<IAssetImporter> customImporters = null) {
        this.project = project;
        importers = customImporters is not null
            ? [.. customImporters]
            : [
                new ModelImporter(), new TextureImporter(), new AudioImporter(), new AnimationImporter(),
                new FalcorSceneImporter(), new BlendImporter(), new TerrainImporter(),
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

    public string ArtifactLogicalPath(Guid guid) =>
        database.Entries.TryGetValue(guid, out ArtifactRecord record) && record.Artifact is not null
            ? "Library/" + record.Artifact.Replace('\\', '/')
            : null;

    public bool TryReadArtifactBytes(Guid guid, out byte[] bytes) {
        bytes = null;

        var logical = ArtifactLogicalPath(guid);
        if (logical is null)
            return false;

        if (ContentMount.HasAny && ContentMount.TryReadBytes(logical, out bytes))
            return true;

        if (TryGetArtifactPath(guid, out var absolutePath)) {
            bytes = File.ReadAllBytes(absolutePath);
            return true;
        }
        return false;
    }

    public RefreshResult Refresh(bool forceAll = false) {
        var stopwatch = Stopwatch.StartNew();

        var workingDb = ArtifactDatabase.Load(project.ArtifactDatabasePath);
        var buildGuidToPath = new Dictionary<Guid, string>();
        var buildPathToGuid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var buildMetaByGuid = new Dictionary<Guid, MetaFile>();

        const int maxPasses = 4;
        var scanned = 0;
        var imported = 0;
        var upToDate = 0;
        var failed = 0;

        for (var pass = 0; pass < maxPasses; pass++) {
            (var passScanned, var passImported, var passUpToDate, var passFailed, var generatedSources) =
                RefreshPass(workingDb, buildGuidToPath, buildPathToGuid, buildMetaByGuid, forceAll && pass == 0);
            scanned += passScanned;
            imported += passImported;
            upToDate += passUpToDate;
            failed += passFailed;

            if (!generatedSources)
                break;
        }

        PruneOrphans(workingDb, buildGuidToPath);

        guidToPath = buildGuidToPath;
        pathToGuid = buildPathToGuid;
        metaByGuid = buildMetaByGuid;
        database = workingDb;

        DeleteOrphanedMetaFiles();
        workingDb.Save(project.ArtifactDatabasePath);

        var result = new RefreshResult(scanned, imported, upToDate, failed, stopwatch.ElapsedMilliseconds);
        Debugging.Log(result.ToString());
        return result;
    }

    public void LoadFromArtifacts() {
        var stopwatch = Stopwatch.StartNew();

        var workingDb = ArtifactDatabase.Load(project.ArtifactDatabasePath);
        var buildGuidToPath = new Dictionary<Guid, string>();
        var buildPathToGuid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var buildMetaByGuid = new Dictionary<Guid, MetaFile>();

        var loaded = 0;
        var source = "guidmap";

        GuidMap map = GuidMap.Load(Path.Combine(project.LibraryPath, GuidMap.FileName));
        if (map is not null) {
            foreach ((var assetPath, var guidText) in map.Entries) {
                if (!Guid.TryParse(guidText, out Guid guid) || guid == Guid.Empty)
                    continue;
                buildGuidToPath[guid] = assetPath;
                buildPathToGuid[assetPath] = guid;
                loaded++;
            }

            foreach ((var guidText, GuidMap.MetaInfo info) in map.Meta) {
                if (!Guid.TryParse(guidText, out Guid guid) || guid == Guid.Empty)
                    continue;
                buildMetaByGuid[guid] = new MetaFile {
                    Guid = guid,
                    Importer = info.Importer,
                    Settings = info.Settings ?? new(),
                };
            }
        }
        else {
            source = "meta scan";
            LoadMapsFromMetas(buildGuidToPath, buildPathToGuid, buildMetaByGuid, ref loaded);
        }

        guidToPath = buildGuidToPath;
        pathToGuid = buildPathToGuid;
        metaByGuid = buildMetaByGuid;
        database = workingDb;

        Debugging.Log($"Player asset load: {loaded} assets registered via {source} " +
                      $"({stopwatch.ElapsedMilliseconds} ms).");
    }

    void LoadMapsFromMetas(Dictionary<Guid, string> buildGuidToPath, Dictionary<string, Guid> buildPathToGuid,
                           Dictionary<Guid, MetaFile> buildMetaByGuid, ref int loaded) {
        if (!Directory.Exists(project.AssetsPath))
            return;

        foreach (var sourceAbsolute in Directory.EnumerateFiles(project.AssetsPath, "*", SearchOption.AllDirectories)) {
            if (sourceAbsolute.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                continue;

            var metaPath = MetaFile.PathFor(sourceAbsolute);
            if (!File.Exists(metaPath))
                continue;

            MetaFile meta;
            try {
                meta = MetaFile.Load(metaPath);
            }
            catch (Exception exception) {
                Debugging.LogWarning($"Player load: meta '{metaPath}' unreadable ({exception.Message}); skipped.");
                continue;
            }

            if (meta is null || meta.Guid == Guid.Empty)
                continue;

            var assetPath = project.ToAssetPath(sourceAbsolute);
            buildGuidToPath[meta.Guid] = assetPath;
            buildPathToGuid[assetPath] = meta.Guid;
            buildMetaByGuid[meta.Guid] = meta;
            loaded++;
        }
    }

    public void WriteGuidMap() {
        var map = new GuidMap();
        foreach ((var path, var guid) in pathToGuid)
            map.Entries[path] = guid.ToString();

        foreach ((var guid, var meta) in metaByGuid) {
            if (meta?.Settings is null || meta.Settings.Count == 0)
                continue;
            map.Meta[guid.ToString()] = new GuidMap.MetaInfo {
                Importer = meta.Importer,
                Settings = (System.Text.Json.Nodes.JsonObject)meta.Settings.DeepClone(),
            };
        }

        map.Save(Path.Combine(project.LibraryPath, GuidMap.FileName));
        Debugging.Log($"Wrote guidmap with {map.Entries.Count} entries ({map.Meta.Count} with settings).");
    }

    sealed class ImportJob {
        public Guid Guid;
        public MetaFile Meta;
        public IAssetImporter Importer;
        public string SourceAbsolute;
        public string AssetPath;
        public string ArtifactRelative;
        public string ArtifactAbsolute;
        public string SettingsHash;
        public long FileSize;
        public DateTime MtimeUtc;
        public string KnownContentHash;
    }

    (int Scanned, int Imported, int UpToDate, int Failed, bool GeneratedSources) RefreshPass(
        ArtifactDatabase workingDb,
        Dictionary<Guid, string> buildGuidToPath,
        Dictionary<string, Guid> buildPathToGuid,
        Dictionary<Guid, MetaFile> buildMetaByGuid,
        bool forceAll = false) {
        var scanned = 0;
        var upToDate = 0;
        var jobs = new List<ImportJob>();

        foreach (var sourceAbsolute in Directory.EnumerateFiles(project.AssetsPath, "*", SearchOption.AllDirectories)) {
            if (sourceAbsolute.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                continue;

            var assetPath = project.ToAssetPath(sourceAbsolute);

            if (buildPathToGuid.ContainsKey(assetPath))
                continue;

            scanned++;
            MetaFile meta = EnsureMeta(sourceAbsolute, assetPath, buildGuidToPath);

            buildGuidToPath[meta.Guid] = assetPath;
            buildPathToGuid[assetPath] = meta.Guid;
            buildMetaByGuid[meta.Guid] = meta;

            IAssetImporter importer = ResolveImporter(meta, sourceAbsolute);

            if (meta.Settings is not null && importer.UpgradeSettings(assetPath, meta.Settings)) {
                meta.Save(MetaFile.PathFor(sourceAbsolute));
                Debugging.Log($"Upgraded import settings for '{assetPath}'.");
            }

            if (importer.ArtifactExtension is null && !importer.RunsWithoutArtifact) {
                upToDate++;
                continue;
            }

            ImportJob job = EvaluateDirty(workingDb, meta, importer, sourceAbsolute, assetPath, forceAll);
            if (job is null)
                upToDate++;
            else
                jobs.Add(job);
        }

        if (jobs.Count == 0)
            return (scanned, 0, upToDate, 0, false);

        var results = new ArtifactRecord[jobs.Count];
        var failedFlags = new bool[jobs.Count];
        var generatedSources = 0;
        var done = 0;

        var options = new ParallelOptions {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };

        Parallel.For(0, jobs.Count, options, i => {
            ImportJob job = jobs[i];
            int completed = Interlocked.Increment(ref done);
            Progress?.Invoke($"{Path.GetFileName(job.AssetPath)} ({completed}/{jobs.Count})");
            ProgressCount?.Invoke(completed, jobs.Count);

            try {
                var importWatch = Stopwatch.StartNew();
                results[i] = RunImport(job);
                importWatch.Stop();

                if (job.Importer.GeneratesSourceAssets)
                    Interlocked.Exchange(ref generatedSources, 1);
                if (importWatch.ElapsedMilliseconds >= 500)
                    Debugging.Log($"  imported '{job.AssetPath}' in {importWatch.ElapsedMilliseconds} ms");
            }
            catch (Exception exception) {
                failedFlags[i] = true;
                Debugging.LogError($"Import failed for '{job.AssetPath}': {exception.Message}");
            }
        });

        var imported = 0;
        var failed = 0;
        for (var i = 0; i < jobs.Count; i++) {
            if (failedFlags[i]) {
                failed++;
            }
            else if (results[i] is not null) {
                workingDb.Entries[jobs[i].Guid] = results[i];
                imported++;
            }
        }

        return (scanned, imported, upToDate, failed, generatedSources == 1);
    }

    MetaFile EnsureMeta(string sourceAbsolute, string assetPath, Dictionary<Guid, string> buildGuidToPath) {
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
        else if (buildGuidToPath.TryGetValue(meta.Guid, out var existingPath)) {
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

    ImportJob EvaluateDirty(ArtifactDatabase workingDb, MetaFile meta, IAssetImporter importer,
        string sourceAbsolute, string assetPath, bool force = false) {
        var settingsHash = meta.SettingsHash();
        var hasArtifact = importer.ArtifactExtension is not null;
        var artifactRelative = hasArtifact ? $"Artifacts/{meta.Guid:N}{importer.ArtifactExtension}" : null;
        var artifactAbsolute = hasArtifact ? Path.Combine(project.LibraryPath, artifactRelative) : null;
        var sourceInfo = new FileInfo(sourceAbsolute);

        workingDb.Entries.TryGetValue(meta.Guid, out ArtifactRecord record);

        var dirty = force
                    || record is null
                    || record.ImporterVersion != importer.Version
                    || record.SettingsHash != settingsHash
                    || (hasArtifact && !File.Exists(artifactAbsolute));

        string contentHash = null;
        if (!dirty) {
            if (record.FileSize == sourceInfo.Length && record.MtimeUtc == sourceInfo.LastWriteTimeUtc) {
                record.SourcePath = assetPath;
                return null;
            }

            contentHash = ContentHash.HashFile(sourceAbsolute);
            if (contentHash == record.ContentHash) {
                record.SourcePath = assetPath;
                record.FileSize = sourceInfo.Length;
                record.MtimeUtc = sourceInfo.LastWriteTimeUtc;
                return null;
            }
        }

        return new ImportJob {
            Guid = meta.Guid,
            Meta = meta,
            Importer = importer,
            SourceAbsolute = sourceAbsolute,
            AssetPath = assetPath,
            ArtifactRelative = artifactRelative,
            ArtifactAbsolute = artifactAbsolute,
            SettingsHash = settingsHash,
            FileSize = sourceInfo.Length,
            MtimeUtc = sourceInfo.LastWriteTimeUtc,
            KnownContentHash = contentHash,
        };
    }

    static ArtifactRecord RunImport(ImportJob job) {
        job.Importer.Import(new AssetImportContext {
            SourceAbsolutePath = job.SourceAbsolute,
            AssetPath = job.AssetPath,
            Guid = job.Guid,
            Settings = job.Meta.Settings,
            ArtifactAbsolutePath = job.ArtifactAbsolute,
        });

        return new ArtifactRecord {
            SourcePath = job.AssetPath,
            ContentHash = job.KnownContentHash ?? ContentHash.HashFile(job.SourceAbsolute),
            SettingsHash = job.SettingsHash,
            ImporterVersion = job.Importer.Version,
            FileSize = job.FileSize,
            MtimeUtc = job.MtimeUtc,
            Artifact = job.ArtifactRelative,
        };
    }

    void PruneOrphans(ArtifactDatabase workingDb, Dictionary<Guid, string> buildGuidToPath) {
        List<Guid> orphans = workingDb.Entries.Keys.Where(guid => !buildGuidToPath.ContainsKey(guid)).ToList();
        foreach (Guid orphan in orphans) {
            ArtifactRecord record = workingDb.Entries[orphan];
            Debugging.Log($"Source of '{record.SourcePath}' is gone; dropping its Library record.");
            workingDb.Entries.Remove(orphan);

            var artifactAbsolute = record.Artifact is null ? null : Path.Combine(project.LibraryPath, record.Artifact);
            if (artifactAbsolute is not null && File.Exists(artifactAbsolute))
                File.Delete(artifactAbsolute);
        }
    }

    void DeleteOrphanedMetaFiles() {
        foreach (var metaAbsolute in Directory.EnumerateFiles(project.AssetsPath, "*.meta", SearchOption.AllDirectories)) {
            var sourceAbsolute = metaAbsolute[..^".meta".Length];
            if (File.Exists(sourceAbsolute))
                continue;
            try {
                File.Delete(metaAbsolute);
                Debugging.Log($"Deleted orphaned meta '{project.ToAssetPath(metaAbsolute)}': its asset no longer exists.");
            }
            catch (Exception exception) {
                Debugging.LogWarning($"Could not delete orphaned meta '{project.ToAssetPath(metaAbsolute)}': {exception.Message}");
            }
        }
    }
}
