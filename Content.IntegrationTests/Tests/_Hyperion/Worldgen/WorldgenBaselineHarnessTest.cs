// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.Worldgen;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Prototypes;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._Hyperion.Worldgen;

[TestFixture]
public sealed class WorldgenBaselineHarnessTest
{
    // Applies the live NFDefault worldgen config to a test map, drops a stationary
    // world loader at origin, ticks, and asserts the chunk-load counter advanced —
    // i.e. the metric plumbing actually observes real worldgen work.
    [Test]
    public async Task ChunkLoadCounterAdvances()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        await server.WaitIdleAsync();

        var entManager = server.ResolveDependency<IEntityManager>();
        var protoManager = server.ResolveDependency<IPrototypeManager>();
        var serManager = server.ResolveDependency<ISerializationManager>();
        var mapSystem = entManager.System<SharedMapSystem>();

        var before = WorldgenMetrics.ChunksLoaded.Value;

        EntityUid loader = default;
        await server.WaitPost(() =>
        {
            var mapUid = mapSystem.CreateMap(out var mapId);
            // Apply the same config WorldgenConfigSystem applies at round start.
            var cfg = protoManager.Index<WorldgenConfigPrototype>("NFDefault");
            cfg.Apply(mapUid, serManager, entManager);

            // A stationary loader forces chunk generation around origin.
            loader = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
            entManager.EnsureComponent<WorldLoaderComponent>(loader);
        });

        // WorldControllerSystem updates at 1 Hz; give it several real seconds of ticks.
        await server.WaitRunTicks(200);
        await server.WaitIdleAsync();

        var after = WorldgenMetrics.ChunksLoaded.Value;
        Assert.That(after, Is.GreaterThan(before),
            "worldgen chunk-load counter did not advance; instrumentation is not observing the load pass");

        await pair.CleanReturnAsync();
    }
}
