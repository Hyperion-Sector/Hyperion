// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared._NF.Shipyard.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 4 rehydration residents: the repair baseline is derived state stripped at
    /// store (Cycle 3) and must be regenerated against the loaded grid; the ownership
    /// component rides the blob but its round-scoped timestamp must be refreshed so the
    /// offline-deletion timer doesn't chew on a previous round's clock.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageRehydrationTest
    {
        [Test]
        public async Task RetrieveRegeneratesRepairBaselineAndRefreshesOwnership()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var timing = server.ResolveDependency<IGameTiming>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;
            EntityUid station = default;
            var staleTime = TimeSpan.FromSeconds(1);

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out _);

                // Ownership as a purchase would have registered it, with a deliberately
                // stale timestamp standing in for "a previous round's clock".
                var ownership = entManager.EnsureComponent<ShipOwnershipComponent>(gridUid);
                ownership.OwnerUserId = new NetUserId(ownerId);
                ownership.LastStatusChangeTime = staleTime;
                ownership.IsOwnerOnline = true;
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

                Assert.That(entManager.TryGetComponent<ShipRepairDataComponent>(retrieved!.Value, out var repair),
                    Is.True, "Retrieve must regenerate the repair baseline the store strip removed.");
                Assert.That(repair.Chunks, Is.Not.Empty, "The regenerated baseline should cover the grid's tiles.");

                Assert.That(entManager.TryGetComponent<ShipOwnershipComponent>(retrieved.Value, out var ownership),
                    Is.True, "Ownership should ride the blob.");
                Assert.That(ownership.OwnerUserId.UserId, Is.EqualTo(ownerId));
                Assert.That(ownership.LastStatusChangeTime, Is.GreaterThan(staleTime),
                    "Retrieve must refresh the round-scoped ownership timestamp.");
                Assert.That(ownership.IsOwnerOnline, Is.False,
                    "A synthetic owner has no session; online state must be re-derived, not trusted from the blob.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
