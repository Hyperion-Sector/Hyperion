// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Content.Server._NF.Worldgen.Components.Debris;
using Content.Server.Worldgen.Prototypes;
using Content.Server.Worldgen.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._Hyperion.Worldgen
{
    /// <summary>
    ///     End-to-end smoke test for the belt debris pipeline: applies the round's NFDefault worldgen
    ///     config to a fresh map, then loads a ring of Wall-band chunks through the real chunk-load
    ///     path (GetOrCreateChunk + WorldChunkLoadedEvent, exactly what WorldControllerSystem raises)
    ///     and asserts debris actually spawns. Doubles as the Part 7 smoke benchmark: it prints
    ///     wall-clock per-chunk load cost, which is the before/after number for placer changes.
    /// </summary>
    [TestFixture]
    public sealed class BeltChunkLoadTest
    {
        private static readonly ProtoId<WorldgenConfigPrototype> ConfigId = "NFDefault";

        /// <summary>Chunk-grid radius of the sample ring. 113 chunks × 128 ≈ 14.5k, mid-Wall.</summary>
        private const int RingChunkRadius = 113;

        /// <summary>How many chunks to load around the ring.</summary>
        private const int SampleCount = 96;

        [Test]
        public async Task WallChunkLoad_SpawnsDebrisThroughRealPath()
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

            await server.WaitAssertion(() =>
            {
                // Distinct chunk coords spaced around the ring; ~710 chunks of circumference at
                // this radius, so 96 samples never collide with each other.
                var coords = new HashSet<Vector2i>();
                for (var i = 0; i < SampleCount; i++)
                {
                    var bearing = MathF.Tau * i / SampleCount;
                    coords.Add(new Vector2i(
                        (int) MathF.Round(RingChunkRadius * MathF.Cos(bearing)),
                        (int) MathF.Round(RingChunkRadius * MathF.Sin(bearing))));
                }

                Assert.That(coords, Has.Count.EqualTo(SampleCount));

                var worldController = entMan.System<WorldControllerSystem>();
                var chunks = new List<(EntityUid Uid, Vector2i Coords)>(SampleCount);

                // Chunk creation (biome stamping) happens outside the timed section; the timed
                // section is the load itself, which is where the debris placer runs.
                foreach (var chunkCoords in coords)
                {
                    var chunk = worldController.GetOrCreateChunk(chunkCoords, map.MapUid);
                    Assert.That(chunk, Is.Not.Null, $"Failed to create chunk at {chunkCoords}");
                    chunks.Add((chunk!.Value, chunkCoords));
                }

                var stopwatch = Stopwatch.StartNew();
                foreach (var (chunkUid, chunkCoords) in chunks)
                {
                    // Mirrors WorldControllerSystem.OnChunkLoadedCore: raise at the map, then at
                    // the chunk (broadcast), without paying for the loader-scan tick loop.
                    var ev = new WorldChunkLoadedEvent(chunkUid, chunkCoords);
                    entMan.EventBus.RaiseLocalEvent(map.MapUid, ref ev);
                    entMan.EventBus.RaiseLocalEvent(chunkUid, ref ev, broadcast: true);
                }

                stopwatch.Stop();

                var debris = entMan.Count<SpaceDebrisComponent>();
                Assert.That(debris, Is.GreaterThan(0),
                    "Loading a ring of Wall chunks through the real path spawned no debris");

                TestContext.Out.WriteLine(
                    $"[belt-bench] {chunks.Count} Wall chunks loaded in {stopwatch.Elapsed.TotalMilliseconds:F0} ms "
                    + $"({stopwatch.Elapsed.TotalMilliseconds / chunks.Count:F2} ms/chunk), {debris} debris spawned");
            });

            await server.WaitRunTicks(5);
            await pair.CleanReturnAsync();
        }
    }
}
