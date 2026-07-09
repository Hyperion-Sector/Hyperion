// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.IO;
using System.Linq;
using Content.Server._Hyperion.Worldgen.Components;
using Content.Server._Hyperion.Worldgen.Systems;
using Content.Server.Worldgen;
using Content.Server.Worldgen.Prototypes;
using Content.Server.Worldgen.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Vector2 = System.Numerics.Vector2;

namespace Content.IntegrationTests.Tests._Hyperion.Worldgen
{
    /// <summary>
    ///     Renders a square block of belt chunks to a PNG so lane legibility can be judged by eye.
    ///     There is no renderer in an integration test, so this rasterises grid tiles directly:
    ///     one pixel per world tile, with the fissure carve region tinted underneath the rock.
    ///     That tint is the whole point. If the field inside the tint looks like the field outside
    ///     it, the lane does not read, whatever the density numbers say.
    ///
    ///     Explicit: it writes a file and costs a couple of seconds, so it stays out of CI.
    ///     Run it with:
    ///       dotnet test Content.IntegrationTests --filter "FullyQualifiedName~BeltRenderTest"
    /// </summary>
    [TestFixture]
    public sealed class BeltRenderTest
    {
        private static readonly ProtoId<WorldgenConfigPrototype> ConfigId = "NFDefault";

        /// <summary>Side length of the chunk block, in chunks. 8 x 8 = 64 chunks = 1024 x 1024 tiles.</summary>
        private const int BlockChunks = 8;

        /// <summary>World radius to sample at. Mid-belt, well inside the 40k edge.</summary>
        private const float SampleRadius = 15000f;

        private const int ImageSize = BlockChunks * WorldGen.ChunkSize;

        private static readonly Rgba32 ColorEmpty = new(10, 10, 12);
        private static readonly Rgba32 ColorCarved = new(18, 30, 58);
        private static readonly Rgba32 ColorRock = new(190, 190, 195);

        /// <summary>Rock that landed inside the carve. Means the overhang pad is under-sized.</summary>
        private static readonly Rgba32 ColorRockInCarve = new(220, 60, 60);

        [Test]
        [Explicit("Writes a PNG; run by hand when tuning lane visibility.")]
        public async Task RenderBeltBlock_WritesLaneVisibilityPng()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entMan = server.EntMan;
            var map = await pair.CreateTestMap();

            await server.WaitAssertion(() =>
            {
                var protoMan = server.ResolveDependency<IPrototypeManager>();
                var serMan = server.ResolveDependency<ISerializationManager>();
                protoMan.Index(ConfigId).Apply(map.MapUid, serMan, entMan);
            });

            Vector2i minChunk = default;

            await server.WaitAssertion(() =>
            {
                if (!entMan.TryGetComponent<FissureFieldComponent>(map.MapUid, out var field) || !field.Rolled)
                    Assert.Ignore("No rolled FissureField on the worldgen map; enable it in worldgen_default.yml.");

                minChunk = FindBlockAroundWidestLane(field!);

                var worldController = entMan.System<WorldControllerSystem>();
                for (var cx = 0; cx < BlockChunks; cx++)
                for (var cy = 0; cy < BlockChunks; cy++)
                {
                    var coords = minChunk + new Vector2i(cx, cy);
                    var chunk = worldController.GetOrCreateChunk(coords, map.MapUid);
                    Assert.That(chunk, Is.Not.Null, $"Failed to create chunk at {coords}");

                    var ev = new WorldChunkLoadedEvent(chunk!.Value, coords);
                    entMan.EventBus.RaiseLocalEvent(map.MapUid, ref ev);
                    entMan.EventBus.RaiseLocalEvent(chunk.Value, ref ev, broadcast: true);
                }
            });

            // Let the blob builders finish populating the debris grids before we read their tiles.
            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                var field = entMan.GetComponent<FissureFieldComponent>(map.MapUid);
                var mapSys = entMan.System<SharedMapSystem>();
                var origin = WorldGen.ChunkToWorldCoords(minChunk);

                using var image = new Image<Rgba32>(ImageSize, ImageSize);

                // Pass 1: the carve region, so rock draws on top of its own tint.
                var carvedPixels = 0;
                for (var px = 0; px < ImageSize; px++)
                for (var py = 0; py < ImageSize; py++)
                {
                    var carved = FissureCarverSystem.IsCarved(field, WorldOf(origin, px, py));
                    if (carved)
                        carvedPixels++;

                    image[px, FlipY(py)] = carved ? ColorCarved : ColorEmpty;
                }

                // Pass 2: every debris tile on the map that falls inside the window.
                var rockTiles = 0;
                var rockInCarve = 0;
                var rockGrids = 0;

                var grids = entMan.EntityQueryEnumerator<MapGridComponent, TransformComponent>();
                while (grids.MoveNext(out var gridUid, out var grid, out var xform))
                {
                    if (xform.MapUid != map.MapUid)
                        continue;

                    var tilesThisGrid = 0;

                    foreach (var tile in mapSys.GetAllTiles(gridUid, grid))
                    {
                        tilesThisGrid++;
                        var world = mapSys.GridTileToWorldPos(gridUid, grid, tile.GridIndices);
                        var px = (int) MathF.Floor(world.X - origin.X);
                        var py = (int) MathF.Floor(world.Y - origin.Y);

                        if (px < 0 || px >= ImageSize || py < 0 || py >= ImageSize)
                            continue;

                        rockTiles++;

                        var inCarve = FissureCarverSystem.IsCarved(field, world);
                        if (inCarve)
                            rockInCarve++;

                        image[px, FlipY(py)] = inCarve ? ColorRockInCarve : ColorRock;
                    }

                    if (tilesThisGrid > 0)
                        rockGrids++;
                }

                Assert.That(rockTiles, Is.GreaterThan(0), "Rendered block contains no rock at all");

                var dir = Path.Combine(Path.GetTempPath(), "hyperion-worldgen");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"belt-r{SampleRadius:F0}-{BlockChunks}x{BlockChunks}.png");
                image.SaveAsPng(path);

                var totalPixels = (long) ImageSize * ImageSize;
                var openPixels = totalPixels - carvedPixels;

                // The legibility number: rock coverage outside the lane versus inside it. If these
                // two are close, the carve is invisible against the ambient field.
                var outsideCoverage = openPixels > 0 ? (rockTiles - rockInCarve) / (double) openPixels : 0d;
                var insideCoverage = carvedPixels > 0 ? rockInCarve / (double) carvedPixels : 0d;

                const int chunks = BlockChunks * BlockChunks;

                TestContext.Out.WriteLine($"[belt-render] wrote {path}");
                TestContext.Out.WriteLine(
                    $"[belt-render] {chunks} chunks at r={SampleRadius:F0}, "
                    + $"{rockTiles} rock tiles, {carvedPixels} carved tiles ({carvedPixels / (double) totalPixels:P1} of frame)");
                TestContext.Out.WriteLine(
                    $"[belt-render] {rockGrids} rocks ({rockGrids / (double) chunks:F2}/chunk), "
                    + $"mean {(rockGrids > 0 ? rockTiles / (double) rockGrids : 0):F0} tiles/rock");
                TestContext.Out.WriteLine(
                    $"[belt-render] rock coverage outside lane {outsideCoverage:P1}, inside lane {insideCoverage:P1}"
                    + (rockInCarve > 0 ? $"  <- {rockInCarve} tiles bulged into the carve, overhang pad is under-sized" : ""));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        ///     Image row 0 is the top, but +Y is north. Flip so the PNG reads like the radar.
        /// </summary>
        private static int FlipY(int py) => ImageSize - 1 - py;

        private static Vector2 WorldOf(Vector2 origin, int px, int py)
            => new(origin.X + px + 0.5f, origin.Y + py + 0.5f);

        /// <summary>
        ///     Picks the chunk block to render. A fissure drifts by up to its DriftAmplitude (0.22 rad,
        ///     ~3300 tiles at r=15k), so aiming the window straight down the rolled bearing would miss
        ///     the lane entirely. Instead sweep <see cref="FissureCarverSystem.IsCarved"/> across the arc
        ///     at the sample radius and centre the window on the carved span we find.
        /// </summary>
        private static Vector2i FindBlockAroundWidestLane(FissureFieldComponent field)
        {
            Assert.That(field.Fissures, Is.Not.Empty, "FissureField rolled no fissures");

            // Widest lane first; it is the easiest to eyeball and the most damning if it fails to read.
            var bearing = field.Fissures.OrderByDescending(f => f.HalfWidthTiles).First().Bearing;

            // ~7.5 tiles per step at r=15k, comfortably finer than the narrowest lane.
            const float sweep = 0.4f;
            const float step = 0.0005f;

            // Take the widest CONTIGUOUS carved span, not the whole carved extent. Several
            // fissures can fall inside the sweep, and spanning first-to-last across them centres
            // the window on the rock between two lanes instead of on a lane.
            float? runStart = null;
            float bestStart = 0f, bestEnd = 0f, bestWidth = 0f;

            for (var theta = bearing - sweep; theta <= bearing + sweep; theta += step)
            {
                var probe = new Vector2(SampleRadius * MathF.Cos(theta), SampleRadius * MathF.Sin(theta));
                if (FissureCarverSystem.IsCarved(field, probe))
                {
                    runStart ??= theta;
                    continue;
                }

                if (runStart is null)
                    continue;

                var width = theta - runStart.Value;
                if (width > bestWidth)
                    (bestStart, bestEnd, bestWidth) = (runStart.Value, theta, width);

                runStart = null;
            }

            // A run still open at the end of the sweep.
            if (runStart is not null && bearing + sweep - runStart.Value > bestWidth)
                (bestStart, bestEnd, bestWidth) = (runStart.Value, bearing + sweep, bearing + sweep - runStart.Value);

            if (bestWidth <= 0f)
                Assert.Ignore($"No carved span found within {sweep} rad of bearing {bearing} at r={SampleRadius}");

            var mid = (bestStart + bestEnd) / 2f;
            var centre = new Vector2(SampleRadius * MathF.Cos(mid), SampleRadius * MathF.Sin(mid));

            var centreChunk = WorldGen.WorldToChunkCoords(
                new Vector2i((int) MathF.Round(centre.X), (int) MathF.Round(centre.Y)));

            return centreChunk - new Vector2i(BlockChunks / 2, BlockChunks / 2);
        }
    }
}
