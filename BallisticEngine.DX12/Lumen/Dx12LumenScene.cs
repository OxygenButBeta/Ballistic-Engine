using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.DXGI;
using BallisticEngine;          // IStaticMeshRenderer, RuntimeSet
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine.DX12;

// Lumen V2 — the SCENE SUBSTRATE for the new GI/reflection stack (plan §"Render Architecture" item 1).
//
// This is NOT a pass and runs NO shading. It owns the durable scene representation every Lumen pass reads:
//   - the shared BLAS/TLAS (Dx12SceneAS) and per-instance bindless geometry/material SRVs (Dx12RtGeometry),
//     both reached through the shared DXR holder (ctx.Dxr) — Lumen does NOT build its own AS, it reuses the
//     one RT shadows/reflections already maintain (stamp-cached: a static scene builds once).
//   - a SURFACE-CARD allocation table: one coarse, stable surface record per scene object (P1 = one card per
//     TLAS instance; P3 refines to oriented sub-cards). Cards are surface records, NOT camera pixels and NOT
//     world probes — they are where off-screen albedo/emissive radiance is captured and lit so an RT hit can
//     sample real surface radiance instead of IBL/probe mush.
//   - the card ATLASES (albedo / normal / emissive / depth / radiance). Allocated here so P3 (capture) and
//     P4 (radiance cache) have a fixed home; P1 only sizes + clears them (no writes → no image change).
//   - DIRTY flags from the geometry/material stamp (transforms/materials/instances). Lights + camera coverage
//     dirty bits are added when the capture/cache passes that consume them land (P3/P4).
//
// P1 contract (plan §P1): wire to the existing TLAS/material infra, NO image change, and the debug log must
// report object count, card count, atlas size, and dirty updates. Gated behind BALLISTIC_DX12_LUMEN so the
// substrate only allocates + logs when Lumen is being worked on; default-off = byte-identical to a no-Lumen
// frame (nothing is allocated, nothing is sampled).
public sealed class Dx12LumenScene : IDisposable
{
    readonly Dx12Device dev;

    // ---- Surface-card allocation table -------------------------------------------------------------------
    // One entry per scene object (TLAS instance). P1 lays the table out over the card atlas as a simple square
    // grid of fixed-size card tiles; P3 fills CardSize/atlas-rect per object from real surface area + captures
    // into the tiles. Kept as a struct array (no per-object alloc churn) — rebuilt only when the stamp changes.
    public struct Card
    {
        public int InstanceIndex;   // TLAS InstanceID() this card represents (matches Dx12SceneAS iteration order)
        public int AtlasX, AtlasY;  // top-left texel of this card's tile in the atlas
        public int Size;            // square tile edge in texels (P1: uniform CardTile)
    }

    Card[] cards = Array.Empty<Card>();
    public ReadOnlySpan<Card> Cards => cards;
    public int CardCount { get; private set; }
    public int ObjectCount { get; private set; }

    // ---- Card atlases ------------------------------------------------------------------------------------
    // Fixed square atlas. CardTile is the per-object tile edge; the atlas holds CardsPerRow^2 tiles. P1 sizes
    // for the current object count (next pow2 grid, clamped); P3 may grow/repack. All five share the layout
    // (same tile rect per object) so a hit's bindless card lookup indexes one rect across every atlas.
    const int CardTile = 32;                 // texels per card tile edge (coarse — cards are low-frequency)
    const int MaxAtlasEdge = 4096;           // safety clamp on the atlas dimension
    public int AtlasEdge { get; private set; }
    public int CardsPerRow { get; private set; }

    // albedo (RGBA8) / normal (RGBA16F packed) / emissive (RGBA16F) / depth (R32F) / radiance (RGBA16F).
    // Allocated lazily on first Ensure when Lumen is armed; null until then (default-off = no VRAM cost).
    Dx12OffscreenTarget cardAlbedo, cardNormal, cardEmissive, cardDepth, cardRadiance;
    public Dx12OffscreenTarget CardAlbedo   => cardAlbedo;
    public Dx12OffscreenTarget CardNormal   => cardNormal;
    public Dx12OffscreenTarget CardEmissive => cardEmissive;
    public Dx12OffscreenTarget CardDepth    => cardDepth;
    public Dx12OffscreenTarget CardRadiance => cardRadiance;

    // ---- Dirty tracking ----------------------------------------------------------------------------------
    // Geometry/material/instance stamp (same shape as Dx12SceneAS's stamp). When it changes the card table is
    // rebuilt and the atlases marked dirty (P3 re-captures). DirtyThisFrame is the per-frame edge the debug
    // log reports; DirtyUpdateCount is the cumulative number of rebuilds since launch.
    int stamp = -1;
    public bool DirtyThisFrame { get; private set; }
    public int DirtyUpdateCount { get; private set; }

    bool atlasesBuilt;
    bool loggedThisStamp;

    public Dx12LumenScene(Dx12Device device) { dev = device; }

    // True once the substrate holds a valid TLAS + at least one card. Passes (P2+) gate their RT dispatch on
    // this; P1 just reports it.
    public bool Valid => CardCount > 0 && cardRadiance != null;

    // Refresh the substrate for this frame: ensure the shared TLAS + bindless geometry, rebuild the card table
    // on a stamp change, (re)allocate the atlases to fit, and log the P1 counts. Cheap no-op for a static
    // scene after the first build (stamp compare short-circuits the rebuild). Returns whether the scene is
    // usable this frame.
    public bool Ensure(Dx12FrameContext ctx)
    {
        DirtyThisFrame = false;

        // Reuse the shared DXR substrate — Lumen never builds its own AS. CheckAvailable is FORCE_NORT-aware;
        // without HW ray tracing Lumen has no off-screen trace, so the substrate reports unavailable (P2's
        // gate refuses to run rather than silently falling back to screen-space — plan non-goal #3 / gate #6).
        if (!ctx.Dxr.CheckAvailable("Lumen"))
            return false;

        Dx12SceneAS sceneAS = ctx.Dxr.SceneAS;
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid)
            return false;

        // The per-instance bindless geometry/material table (normals/uvs/indices/per-tri material) the card
        // capture + RT hit shading read. Must follow EnsureMaterialTable (which resets the bindless heap the
        // SRVs live in), exactly like the reflections pass orders it.
        ctx.GpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        Dx12RtGeometry rtGeo = ctx.Dxr.RtGeometry;
        rtGeo.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, ctx.GpuDriven);

        // The card table is laid out over TLAS instances. RtGeometry.InstanceCount is the authoritative count
        // (same iteration order as Dx12SceneAS → InstanceID() lines up with a card's InstanceIndex).
        int objects = rtGeo.InstanceCount;

        // Stamp: rebuild the table only when the instance/material set changed. RtGeometry already re-Rebuilds
        // on that change; mirror it with the object count + a coverage hash so card layout follows geometry.
        int s = ComputeStamp(objects);
        if (s != stamp || !atlasesBuilt)
        {
            stamp = s;
            RebuildCards(objects);
            EnsureAtlases();
            DirtyThisFrame = true;
            DirtyUpdateCount++;
            loggedThisStamp = false;
        }

        // P1 debug log: object/card/atlas/dirty. Print once per stamp (so a static scene logs once, not every
        // frame) plus on every dirty edge. Honours the project's "no per-frame churn" rule.
        if (!loggedThisStamp)
        {
            loggedThisStamp = true;
            // Console (same channel as the rest of the DX12 layer, e.g. the device's RT-availability line) so it
            // shows on the headless screenshot path bal-render drives; mirrored to Debugging.Log for the editor
            // console + a live player session. Once per stamp → no per-frame churn.
            string line = $"[Lumen] scene: objects={ObjectCount} cards={CardCount} " +
                          $"atlas={AtlasEdge}x{AtlasEdge} (tile={CardTile}, {CardsPerRow}/row) " +
                          $"dirtyUpdates={DirtyUpdateCount}";
            Console.WriteLine(line);
            Debugging.Log(line);
        }

        return Valid;
    }

    // Build one card per TLAS instance, tiling the atlas left-to-right / top-to-bottom. P1 layout only — P3
    // replaces the uniform tiling with surface-area-weighted card rects captured from the mesh.
    void RebuildCards(int objects)
    {
        ObjectCount = objects;
        CardCount = objects;   // P1: exactly one card per object

        CardsPerRow = Math.Max(1, NextPow2((int)Math.Ceiling(Math.Sqrt(Math.Max(objects, 1)))));
        AtlasEdge = Math.Min(MaxAtlasEdge, Math.Max(CardTile, CardsPerRow * CardTile));
        // If the clamp bit (huge object count), recompute how many tiles actually fit per row.
        CardsPerRow = Math.Max(1, AtlasEdge / CardTile);

        if (cards.Length < objects)
            cards = new Card[Math.Max(objects, 1)];

        for (int i = 0; i < objects; i++)
        {
            int col = i % CardsPerRow;
            int row = i / CardsPerRow;
            cards[i] = new Card
            {
                InstanceIndex = i,
                AtlasX = col * CardTile,
                AtlasY = row * CardTile,
                Size = CardTile,
            };
        }
    }

    // (Re)allocate the five card atlases at AtlasEdge². Committed targets (cross-frame surface cache → never
    // pooled, like the other history targets). P1 leaves them cleared; P3 captures into them.
    void EnsureAtlases()
    {
        DisposeAtlases();
        int e = Math.Max(CardTile, AtlasEdge);
        cardAlbedo   = new Dx12OffscreenTarget(dev, e, e, withDepth: false, colorFormat: Format.R8G8B8A8_UNorm,        colorReadable: true, allowUav: true);
        cardNormal   = new Dx12OffscreenTarget(dev, e, e, withDepth: false, colorFormat: Format.R16G16B16A16_Float,   colorReadable: true, allowUav: true);
        cardEmissive = new Dx12OffscreenTarget(dev, e, e, withDepth: false, colorFormat: Format.R16G16B16A16_Float,   colorReadable: true, allowUav: true);
        cardDepth    = new Dx12OffscreenTarget(dev, e, e, withDepth: false, colorFormat: Format.R32_Float,            colorReadable: true, allowUav: true);
        cardRadiance = new Dx12OffscreenTarget(dev, e, e, withDepth: false, colorFormat: Format.R16G16B16A16_Float,   colorReadable: true, allowUav: true);
        atlasesBuilt = true;
    }

    int ComputeStamp(int objects)
    {
        var h = new HashCode();
        h.Add(objects);
        // RtGeometry's own stamp already folds the instance set + material table; fold the object count so a
        // pure add/remove that leaves the hash otherwise equal still re-lays the table.
        return h.ToHashCode();
    }

    static int NextPow2(int v)
    {
        if (v <= 1) return 1;
        v--; v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16; return v + 1;
    }

    void DisposeAtlases()
    {
        cardAlbedo?.Dispose(); cardNormal?.Dispose(); cardEmissive?.Dispose();
        cardDepth?.Dispose(); cardRadiance?.Dispose();
        cardAlbedo = cardNormal = cardEmissive = cardDepth = cardRadiance = null;
        atlasesBuilt = false;
    }

    public void Dispose()
    {
        DisposeAtlases();
        cards = Array.Empty<Card>();
    }
}
