// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 5 store-in-progress flag: while a grid carries
    /// <see cref="ShipStorageInProgressComponent"/> (raised for the span of a
    /// TryStoreShip call), container insertion targeting anything on that grid is
    /// blocked, so nothing reparents into a ship during the async DB-commit window
    /// before it despawns. Dropping the marker re-opens insertion — the gate scopes
    /// to the store window, not the grid's lifetime.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageInProgressTest
    {
        [Test]
        public async Task InsertionBlockedOnlyWhileStoreInProgress()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var containerSys = entManager.System<SharedContainerSystem>();

            EntityUid gridUid = default;
            EntityUid box = default;
            EntityUid item = default;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                (box, item) = ShipStorageTestHelpers.SpawnContainerAndLooseItem(entManager, gridUid);
            });

            // Marker raised: the insert must be refused.
            var blockedInsert = true;
            await server.WaitPost(() =>
            {
                entManager.EnsureComponent<ShipStorageInProgressComponent>(gridUid);
                var container = containerSys.GetContainer(box, "test-container");
                blockedInsert = containerSys.Insert(item, container);
            });
            Assert.That(blockedInsert, Is.False,
                "Container insertion onto a grid with a raised store-in-progress marker must be blocked.");

            // Marker dropped: the same insert succeeds.
            var allowedInsert = false;
            await server.WaitPost(() =>
            {
                entManager.RemoveComponent<ShipStorageInProgressComponent>(gridUid);
                var container = containerSys.GetContainer(box, "test-container");
                allowedInsert = containerSys.Insert(item, container);
            });
            Assert.That(allowedInsert, Is.True,
                "Dropping the store-in-progress marker must re-open container insertion.");

            await pair.CleanReturnAsync();
        }
    }
}
