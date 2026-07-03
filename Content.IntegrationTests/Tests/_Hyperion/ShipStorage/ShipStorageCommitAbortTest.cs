// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared._NF.Shipyard.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 5 regression pin (review CRITICAL-1): the commit-time organics re-check
    /// aborts AFTER the DB revision is committed, leaving the grid live. For a
    /// never-retrieved ship that would open a dupe — a committed blob plus a live grid
    /// sharing one ShipId, retrievable into a second grid — UNLESS the store reserved
    /// the ShipId in the active registry at its synchronous gate. This forces the abort
    /// deterministically (the real board-during-await race can't be interleaved in the
    /// synchronous harness) and proves retrieve is refused.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageCommitAbortTest
    {
        [Test]
        public async Task CommitTimeAbort_LeavesShipRegistered_RetrieveRefusedNoDupe()
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

            // Force the commit-time organics re-check to trip for this grid: exercises
            // the abort-AFTER-DB-commit path without an actual mid-await boarder.
            shipStorage.StoreCommitOrganicsOverride = g => g == gridUid;

            try
            {
                Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
                await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
                var storeResult = await storeTask;

                server.RunTicks(1);
                await server.WaitIdleAsync();

                // The store aborted post-commit: the grid is still live, and the ship id
                // was stamped on the grid-side deed before serialize.
                Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.OrganicsAboard));
                Assert.That(entManager.EntityExists(gridUid), Is.True, "Commit-time abort must leave the grid live.");

                Guid shipId = default;
                await server.WaitAssertion(() =>
                {
                    Assert.That(entManager.TryGetComponent<ShuttleDeedComponent>(gridUid, out var deed), Is.True);
                    Assert.That(deed!.ShipId, Is.Not.Null, "Ship id must have been stamped before the abort.");
                    shipId = deed.ShipId!.Value;
                    Assert.That(shipStorage.IsShipActive(shipId), Is.True,
                        "A commit-time abort must leave the ship reserved in the active registry.");
                });

                // A revision WAS committed (the recheck runs after SaveShipRevision), so
                // the dupe is only prevented by the registry reservation, not by an
                // absent DB row.
                var stored = await shipStorage.GetStoredShips(ownerId);
                Assert.That(stored, Has.Count.EqualTo(1), "The commit landed a revision; the row exists.");

                // The load-bearing assertion: retrieve must be REFUSED (the live grid
                // holds the registry slot). Without the sync-gate reservation this would
                // load a second grid from the committed blob — a dupe.
                Task<EntityUid?> retrieveTask = null!;
                await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(shipId, ownerId, station));
                var retrieved = await retrieveTask;

                Assert.That(retrieved, Is.Null,
                    "Retrieve must refuse while the just-aborted grid is still live and registered (no dupe).");
            }
            finally
            {
                shipStorage.StoreCommitOrganicsOverride = null;
            }

            await pair.CleanReturnAsync();
        }
    }
}
