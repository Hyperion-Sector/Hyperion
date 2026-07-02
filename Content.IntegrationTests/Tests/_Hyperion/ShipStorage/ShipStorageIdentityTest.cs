// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 4 identity: the ShipId rides the grid-side deed through the blob, so a
    /// store in a LATER round (active-ship registry empty) still resolves to the same
    /// DB row instead of forking a duplicate. The registry wipe simulates the round
    /// boundary via the internal test seam.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageIdentityTest
    {
        [Test]
        public async Task ReStoreAcrossRoundsLandsOnSameRow()
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

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out _);
            });

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var (firstResult, firstId) = await storeTask;
            Assert.That(firstResult, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(firstId!.Value, ownerId, station));
            var retrieved = await retrieveTask;
            Assert.That(retrieved, Is.Not.Null);

            server.RunTicks(2);
            await server.WaitIdleAsync();

            // Simulate the next round: the registry is round-scoped and wiped, but the
            // grid-side deed (which rode the blob) still carries the ShipId.
            await server.WaitPost(() => shipStorage.ClearActiveShipRegistry());

            Task<(ShipStorageResult Result, Guid? ShipId)> reStoreTask = null!;
            await server.WaitPost(() => reStoreTask = shipStorage.TryStoreShip(retrieved!.Value, ownerId));
            var (secondResult, secondId) = await reStoreTask;

            Assert.That(secondResult, Is.EqualTo(ShipStorageResult.Success));
            Assert.That(secondId, Is.EqualTo(firstId),
                "Re-store with an empty registry must resolve the ShipId from the deed, not mint a fork.");

            var ships = await shipStorage.GetStoredShips(ownerId);
            Assert.That(ships.Count, Is.EqualTo(1),
                "Cross-round re-store must land on the same DB row, not create a second ship.");

            await pair.CleanReturnAsync();
        }
    }
}
