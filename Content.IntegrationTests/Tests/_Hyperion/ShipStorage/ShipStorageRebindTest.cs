// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server.Shuttles.Systems;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 4 identity rebind: the blob's deed carries a stale ShuttleUid and a
    /// DeedHolder that died with its round, and console locks carry the OLD grid uid
    /// as a raw string — every one must be re-stamped to the new uid or the ship comes
    /// back with bricked consoles and a dangling deed.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageRebindTest
    {
        [Test]
        public async Task RetrieveRebindsDeedAndConsoleLocks()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();
            var lockSystem = entManager.System<ShuttleConsoleLockSystem>();

            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;
            EntityUid station = default;
            EntityUid console = default;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out _);

                // A locked shuttle console stamped with the live grid uid, as the NF
                // purchase path leaves it. Spawned at tile CENTER (0.5, 0.5), not the
                // corner (0,0) — a corner-spawned entity can get ejected to the map by
                // the FTL move during retrieve (Task 4 lesson).
                console = entManager.SpawnEntity(null, new EntityCoordinates(gridUid, new System.Numerics.Vector2(0.5f, 0.5f)));
                entManager.EnsureComponent<ShuttleConsoleLockComponent>(console);
                lockSystem.SetShuttleId(console, gridUid.ToString());
            });

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var (result, shipId) = await storeTask;
            Assert.That(result, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(shipId!.Value, ownerId, station));
            var retrieved = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(retrieved, Is.Not.Null);
                var newGrid = retrieved!.Value;

                Assert.That(entManager.TryGetComponent<ShuttleDeedComponent>(newGrid, out var deed), Is.True,
                    "The grid-side deed must survive the blob.");
                Assert.That(deed.ShuttleUid, Is.EqualTo(newGrid), "Deed must rebind to the new grid uid.");
                Assert.That(deed.DeedHolder, Is.Null, "The old card deed-holder died with its round.");
                Assert.That(deed.ShipId, Is.EqualTo(shipId), "Deed keeps the persistent ShipId.");

                var found = false;
                var query = entManager.AllEntityQueryEnumerator<ShuttleConsoleLockComponent, TransformComponent>();
                while (query.MoveNext(out _, out var lockComp, out var xform))
                {
                    if (xform.GridUid != newGrid)
                        continue;

                    found = true;
                    Assert.That(lockComp.ShuttleId, Is.EqualTo(newGrid.ToString()),
                        "Console locks must be re-stamped to the NEW grid uid string.");
                }

                Assert.That(found, Is.True, "The locked console should have survived the round-trip.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
