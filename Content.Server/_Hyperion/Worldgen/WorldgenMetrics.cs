// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Prometheus;

namespace Content.Server._Hyperion.Worldgen;

/// <summary>
///     Phase-0 baseline instrumentation for the current (pre-v2) worldgen.
///     One home for every worldgen Prometheus metric so the upstream systems
///     only carry a one-line <c>// Hyperion:</c> reference each, and the
///     baseline harness can read the handles directly.
/// </summary>
public static class WorldgenMetrics
{
    public static readonly Histogram ChunkLoadBatch = Metrics.CreateHistogram(
        "ss14_worldgen_chunk_load_batch_seconds",
        "Wall-clock duration of one WorldControllerSystem chunk-load pass.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.0001, 2, 14) });

    public static readonly Counter ChunksLoaded = Metrics.CreateCounter(
        "ss14_worldgen_chunks_loaded_total",
        "Total chunks instantiated by the worldgen loader since server start.");

    public static readonly Gauge ResidentChunks = Metrics.CreateGauge(
        "ss14_worldgen_resident_chunks",
        "Currently loaded worldgen chunks (LoadedChunkComponent).");

    public static readonly Gauge ResidentDebris = Metrics.CreateGauge(
        "ss14_worldgen_resident_debris",
        "Currently resident debris grids (SpaceDebrisComponent).");

    public static readonly Gauge GcQueueDepth = Metrics.CreateGauge(
        "ss14_worldgen_gc_queue_depth",
        "Pending entities in a worldgen GC queue.",
        new GaugeConfiguration { LabelNames = new[] { "queue" } });

    public static readonly Histogram LocalityPoll = Metrics.CreateHistogram(
        "ss14_worldgen_locality_poll_seconds",
        "Duration of one LocalityLoaderSystem update poll.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.0001, 2, 14) });

    public static readonly Histogram DebrisDespawnScan = Metrics.CreateHistogram(
        "ss14_worldgen_debris_despawn_scan_seconds",
        "Duration of the salvage-mob scan on a single debris despawn.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.00001, 2, 16) });

    public static readonly Gauge SalvageMobPopulation = Metrics.CreateGauge(
        "ss14_worldgen_salvage_mob_population",
        "Live NFSalvageMobRestrictions entities (the O(n) despawn scan walks this).");

    public static readonly Histogram FloorPlanPopulate = Metrics.CreateHistogram(
        "ss14_worldgen_floorplan_populate_seconds",
        "Duration of one SimpleFloorPlanPopulator OnFloorPlanBuilt pass.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.0001, 2, 14) });

    public static readonly Histogram FloorPlanEntities = Metrics.CreateHistogram(
        "ss14_worldgen_floorplan_entities",
        "Entities spawned by one floor-plan populate pass.",
        new HistogramConfiguration { Buckets = Histogram.LinearBuckets(0, 25, 20) });
}
