// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server.Station.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 4 presentation: a retrieved ship must materialize on the requesting
    /// station's map (FTL-docked or proximity-placed by TryFTLDock), not on a bare
    /// throwaway map; and a retrieve against an invalid station must refuse cheaply.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageRetrievePresentationTest
    {
        [Test]
        public async Task RetrievePresentsAtRequestingStation()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;
            EntityUid station = default;
            EntityUid dockGrid = default;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out dockGrid);
            });

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var (storeResult, shipId) = await storeTask;
            Assert.That(storeResult, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(shipId!.Value, ownerId, station));
            var retrieved = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(retrieved, Is.Not.Null, "TryRetrieveShip returned no grid.");
                var shipXform = entManager.GetComponent<TransformComponent>(retrieved!.Value);
                var dockXform = entManager.GetComponent<TransformComponent>(dockGrid);
                Assert.That(shipXform.MapUid, Is.EqualTo(dockXform.MapUid),
                    "The retrieved ship should present on the requesting station's map, not a staging map.");
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task RetrieveRefusesInvalidStation()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;

            await server.WaitPost(() =>
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _));

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var (storeResult, shipId) = await storeTask;
            Assert.That(storeResult, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // A non-station entity is not a valid dock target; retrieve must refuse
            // and leave the ship retrievable (no stuck reservation).
            EntityUid bogus = default;
            await server.WaitPost(() => bogus = entManager.SpawnEntity(null, MapCoordinates.Nullspace));

            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(shipId!.Value, ownerId, bogus));
            Assert.That(await retrieveTask, Is.Null, "Retrieve should refuse an invalid requesting station.");

            await pair.CleanReturnAsync();
        }
    }
}
