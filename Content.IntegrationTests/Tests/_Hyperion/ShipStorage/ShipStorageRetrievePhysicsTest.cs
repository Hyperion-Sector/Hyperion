// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Retrieve-side PHYSICS reproduction: the toy one-tile round-trip test can't hit the real
    /// bug because it has no fixture complexity. This loads an ACTUAL roster vessel
    /// map-initialized (real hull fixtures, machinery, the works), stores it, retrieves it, and
    /// then TICKS the physics for a while. A grid whose broadphase/contacts came back inconsistent
    /// throws inside <c>SharedPhysicsSystem.SimulateWorld</c> every tick (duplicate contact pair /
    /// corrupt contact list) — an error log the pool turns into a hard failure on
    /// <see cref="PairAsserts"/>/CleanReturnAsync. So this test being GREEN is the assertion that
    /// the reborn hull is physically alive: physics simulates without throwing.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageRetrievePhysicsTest
    {
        // A real, mid-size vessel: enough fixtures/contacts to exercise the broadphase, small
        // enough to load fast. Deterministic (not "first in the roster") so the repro is stable.
        private const string VesselId = "Olympus";

        [Test]
        public async Task RetrievedVesselPhysicsStaysAlive()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var mapLoader = entManager.System<MapLoaderSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            Assert.That(protoMan.TryIndex<VesselPrototype>(VesselId, out var vessel), Is.True,
                $"Vessel prototype '{VesselId}' not found.");

            var ownerId = Guid.NewGuid();

            // Load the real vessel grid, map-initialized (so its machinery/fixtures are live).
            EntityUid gridUid = default;
            await server.WaitPost(() =>
            {
                var opts = new DeserializationOptions { InitializeMaps = true };
                Assert.That(mapLoader.TryLoadGrid(vessel!.ShuttlePath, out _, out var grid, opts), Is.True,
                    $"Failed to load vessel grid from {vessel.ShuttlePath}.");
                gridUid = grid!.Value.Owner;
            });

            server.RunTicks(2);
            await server.WaitIdleAsync();

            EntityUid station = default;
            await server.WaitPost(() =>
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out _));

            // Store.
            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var storeResult = await storeTask;

            Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.Success),
                $"Store of real vessel '{VesselId}' should succeed (got {storeResult.Result}).");
            Assert.That(storeResult.ShipId, Is.Not.Null);

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // Retrieve.
            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(storeResult.ShipId!.Value, ownerId, station));
            var retrievedGrid = await retrieveTask;

            Assert.That(retrievedGrid, Is.Not.Null, "TryRetrieveShip returned no grid.");

            // The crux: tick physics HARD after retrieve. A corrupt broadphase/contact graph on the
            // reborn grid throws in SimulateWorld on these ticks; the pool fails the test on that
            // error log. Green here == physics is alive on the retrieved hull.
            server.RunTicks(30);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(entManager.EntityExists(retrievedGrid!.Value), Is.True,
                    "The retrieved grid should still exist after ticking physics.");
                Assert.That(entManager.HasComponent<MapGridComponent>(retrievedGrid.Value), Is.True,
                    "The retrieved grid should still be a grid after ticking physics.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
