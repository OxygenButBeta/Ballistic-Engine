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

    // Published maps the render thread reads every frame (asset browser, scene/inspector lookups).
    // Refresh() builds fresh copies on its worker thread and swaps them in atomically at the end, so
    // a reader on another thread always sees a complete, self-consistent snapshot — never a map
    // that's being cleared/repopulated mid-pass. The fields are replaced by reference, never mutated
    // in place once published; the in-progress build dictionaries are separate locals.
    volatile Dictionary<Guid, string> guidToPath = new();
    volatile Dictionary<string, Guid> pathToGuid = new(StringComparer.OrdinalIgnoreCase);
    volatile Dictionary<Guid, MetaFile> metaByGuid = new();

    // Same atomic-publish discipline as the maps above: Refresh builds into a local ArtifactDatabase
    // and swaps it in at the end, so render-thread reads (TryGetArtifactPath for thumbnails) always
    // see a consistent database, never one whose Entries are being added/removed mid-refresh.
    volatile ArtifactDatabase database = new();

    public IReadOnlyDictionary<Guid, string> GuidToPath => guidToPath;
    public IReadOnlyDictionary<string, Guid> PathToGuid => pathToGuid;
    public BallisticProject Project => project;

    // Raised with the file name just before each asset is import-checked, so a UI (the editor's
    // busy overlay) can show what's currently being processed. Called on whatever thread Refresh
    // runs on — the editor marshals it to the render thread itself.
    public Action<string> Progress { get; set; }

    public AssetImportPipeline(BallisticProject project, IReadOnlyList<IAssetImporter> customImporters = null) {
        this.project = project;
        importers = customImporters is not null
            ? [.. customImporters]
            : [
                new ModelImporter(), new TextureImporter(), new FalcorSceneImporter(),
                new BlendImporter(), new TerrainImporter(), new NativeAssetImporter(), new DefaultImporter()
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

    // The artifact's logical pack path (forward-slash, project-root-relative), e.g.
    // "Library/Artifacts/<guid>.bmesh". Null when the guid has no artifact record.
    public string ArtifactLogicalPath(Guid guid) =>
        database.Entries.TryGetValue(guid, out ArtifactRecord record) && record.Artifact is not null
            ? "Library/" + record.Artifact.Replace('\\', '/')
            : null;

    // Pack-aware artifact read: returns the artifact's raw bytes from a mounted content pack if one
    // has it (shipped player), else from the loose Library file (editor/dev). False when the guid has
    // no artifact, or neither source has the bytes. The loaders decode these via *Artifact.Read(Stream).
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

    // forceAll = true reimports every asset regardless of the up-to-date checks (Unity's
    // "Reimport All"): use after importer bugfixes or a corrupted Library.
    public RefreshResult Refresh(bool forceAll = false) {
        var stopwatch = Stopwatch.StartNew();

        // Build into fresh LOCAL maps + database; the live fields keep serving the old snapshot until
        // we swap the finished ones in at the end. Refresh can run on a worker thread while the render
        // thread reads the published state every frame, so we must never mutate what's being read.
        var workingDb = ArtifactDatabase.Load(project.ArtifactDatabasePath);
        var buildGuidToPath = new Dictionary<Guid, string>();
        var buildPathToGuid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var buildMetaByGuid = new Dictionary<Guid, MetaFile>();

        // Importers can write new source assets during a pass (the model importer generates .mat
        // files, the Falcor importer a sibling .scene). We sweep again ONLY when such an importer
        // actually ran, so those generated files get registered. A refresh that imported only leaf
        // assets (textures, meshes) finishes in one pass. Crucially, later passes do NOT re-scan or
        // re-parse the metas of files already mapped — they only pick up files that newly appeared,
        // so a second pass is cheap even with thousands of assets.
        const int maxPasses = 4;
        var scanned = 0;
        var imported = 0;
        var upToDate = 0;
        var failed = 0;

        for (var pass = 0; pass < maxPasses; pass++) {
            // Only the first pass force-reimports; later passes just register the source assets
            // pass 1's importers generated (those are freshly imported anyway).
            (var passScanned, var passImported, var passUpToDate, var passFailed, var generatedSources) =
                RefreshPass(workingDb, buildGuidToPath, buildPathToGuid, buildMetaByGuid, forceAll && pass == 0);
            scanned += passScanned;
            imported += passImported;
            upToDate += passUpToDate;
            failed += passFailed;

            // Nothing new to register unless an importer wrote sibling source assets this pass.
            if (!generatedSources)
                break;
        }

        PruneOrphans(workingDb, buildGuidToPath);

        // Publish the finished snapshot atomically (single reference assignment each). Readers on
        // other threads see either the previous complete snapshot or this one — never a torn one.
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

    // Shipped-player load: populate the GUID maps + artifact database from BAKED build data, WITHOUT
    // running any importer (no Assimp/Stb/Magick, no source re-read, no writes). The build bakes every
    // artifact plus the lookup tables; the player just reads them so AssetDatabase.Load can resolve
    // "Assets/..." -> guid -> artifact.
    //
    // Preferred source is Library\guidmap.json (written at build time) — it lets the build SHIP NO
    // SOURCE FILES OR METAS at all (sources stay private; the folder stays clean). If absent (older
    // builds), falls back to scanning the .meta sidecars under Assets\.
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
            // Reconstruct the runtime metas (texture type etc.) the build baked into the guidmap, so
            // TryGetMeta works without shipped .meta files — else every texture loads as Diffuse.
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

    // Fallback path: rebuild the maps by reading every .meta sidecar under Assets\ (older builds that
    // still ship sources + metas). Read-only — never creates or rewrites a meta, unlike EnsureMeta.
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

    // Writes the complete path -> guid table to Library\guidmap.json (build time). Call after a
    // Refresh so the live maps are fully populated; the player reads this instead of the metas.
    public void WriteGuidMap() {
        var map = new GuidMap();
        foreach ((var path, var guid) in pathToGuid)
            map.Entries[path] = guid.ToString();

        // Ship the runtime-relevant import settings (e.g. texture type) per asset, since the build
        // strips the .meta sidecars — without this the player has no way to know a texture is a
        // normal/spec map and binds it through the wrong sampler.
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

    // One sweep over Assets\. Files already mapped this refresh (by an earlier pass) are skipped
    // entirely — no FileInfo, no meta read, no import check — so re-passes only cost the walk plus
    // work on genuinely-new files. Returns whether any source-generating importer ran (the only
    // reason to sweep again).
    // A dirty asset queued for the parallel import phase. Everything it needs is precomputed in the
    // sequential scan so the parallel work touches no shared pipeline state.
    sealed class ImportJob {
        public Guid Guid;
        public MetaFile Meta;
        public IAssetImporter Importer;
        public string SourceAbsolute;
        public string AssetPath;
        public string ArtifactRelative;     // null when the importer has no Library artifact
        public string ArtifactAbsolute;
        public string SettingsHash;
        public long FileSize;
        public DateTime MtimeUtc;
        public string KnownContentHash;     // set when the scan already hashed the file
    }

    // A single sweep over Assets\ in three stages:
    //   1. Scan (sequential)   — walk files, ensure metas, fill the path/guid maps, and decide which
    //                            assets are dirty. Touches all shared pipeline state; cheap per file.
    //   2. Import (PARALLEL)   — decode + write each dirty asset's own artifact across all cores.
    //                            Pure CPU/file work on disjoint outputs; no shared state.
    //   3. Commit (sequential) — fold the produced ArtifactRecords back into the database.
    // Files already mapped by an earlier pass this refresh are skipped entirely.
    (int Scanned, int Imported, int UpToDate, int Failed, bool GeneratedSources) RefreshPass(
        ArtifactDatabase workingDb,
        Dictionary<Guid, string> buildGuidToPath,
        Dictionary<string, Guid> buildPathToGuid,
        Dictionary<Guid, MetaFile> buildMetaByGuid,
        bool forceAll = false) {
        var scanned = 0;
        var upToDate = 0;
        var jobs = new List<ImportJob>();

        // ---- Stage 1: scan (sequential) ----
        foreach (var sourceAbsolute in Directory.EnumerateFiles(project.AssetsPath, "*", SearchOption.AllDirectories)) {
            if (sourceAbsolute.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                continue;

            var assetPath = project.ToAssetPath(sourceAbsolute);

            // Already handled by an earlier pass this refresh — don't touch it again.
            if (buildPathToGuid.ContainsKey(assetPath))
                continue;

            scanned++;
            MetaFile meta = EnsureMeta(sourceAbsolute, assetPath, buildGuidToPath);

            buildGuidToPath[meta.Guid] = assetPath;
            buildPathToGuid[assetPath] = meta.Guid;
            buildMetaByGuid[meta.Guid] = meta;

            IAssetImporter importer = ResolveImporter(meta, sourceAbsolute);

            // Heal stale .meta settings (e.g. a normal map left tagged Diffuse by an old importer).
            // Persist the corrected meta so the fix sticks and the next load reads the right type.
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

        // ---- Stage 2: import (parallel) ----
        var results = new ArtifactRecord[jobs.Count];
        var failedFlags = new bool[jobs.Count];
        var generatedSources = 0;
        var done = 0;

        var options = new ParallelOptions {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };

        Parallel.For(0, jobs.Count, options, i => {
            ImportJob job = jobs[i];
            // Progress is best-effort (last writer wins) — fine for a "currently importing X" label.
            Progress?.Invoke($"{Path.GetFileName(job.AssetPath)} ({Interlocked.Increment(ref done)}/{jobs.Count})");

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

        // ---- Stage 3: commit (sequential) ----
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

    // Sequential dirty-check. Returns null when the asset's artifact is already current (and
    // refreshes its fast-path stamp in the DB as a side effect), or an ImportJob describing the
    // work when it must be (re)imported. Reads/updates database.Entries, so it runs in the scan
    // stage — never in parallel.
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
                // Touched but unchanged; remember the new stamp so the next run takes the fast path.
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
            KnownContentHash = contentHash, // null = scan didn't hash; RunImport hashes off-thread
        };
    }

    // Parallel-safe import: decodes the source and writes its OWN artifact file, then returns the
    // record to commit. Touches no shared pipeline state (the importer writes to a guid-named output
    // and, for source-generating importers, distinct sibling files), so it runs across cores.
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

    // Runs before publish, on the unpublished workingDb — safe to mutate its Entries here.
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

    // A .meta is a sidecar for exactly one file (folders get none — the scan walks files only),
    // so a meta whose source file is gone was stranded by an EXTERNAL delete (Explorer/IDE; the
    // in-editor delete removes both together). Clean it up like Unity does on refresh.
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
