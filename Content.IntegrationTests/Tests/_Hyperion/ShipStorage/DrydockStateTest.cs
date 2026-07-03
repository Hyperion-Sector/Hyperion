// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server._NF.Shipyard.Systems;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Containers.ItemSlots;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 5 drydock list: RefreshDrydockState fills the console's stored-ship
    /// cache from the OPERATOR's account (the DB hot index), which the synchronous
    /// state builder then carries to the client. Two stored ships in, two rows out,
    /// keyed by the persistent ShipId.
    /// </summary>
    [TestFixture]
    public sealed class DrydockStateTest
    {
        [Test]
        public async Task DrydockState_ListsOwnerStoredShips()
        {
            // Connected pair: the list is keyed on a real operator session's account.
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var playerManager = server.ResolveDependency<IPlayerManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();
            var shipyard = entManager.System<ShipyardSystem>();

            var session = playerManager.Sessions.First();
            var ownerAccount = session.UserId.UserId;

            EntityUid gridA = default;
            EntityUid gridB = default;

            await server.WaitPost(() =>
            {
                gridA = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                gridB = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
            });

            Task<(ShipStorageResult Result, Guid? ShipId)> storeA = null!;
            await server.WaitPost(() => storeA = shipStorage.TryStoreShip(gridA, ownerAccount));
            var (resultA, idA) = await storeA;
            Assert.That(resultA, Is.EqualTo(ShipStorageResult.Success));

            Task<(ShipStorageResult Result, Guid? ShipId)> storeB = null!;
            await server.WaitPost(() => storeB = shipStorage.TryStoreShip(gridB, ownerAccount));
            var (resultB, idB) = await storeB;
            Assert.That(resultB, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            EntityUid console = default;
            EntityUid playerEnt = default;
            ShipyardConsoleComponent consoleComp = default!;

            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out var mapId);
                playerEnt = entManager.SpawnEntity(null, new MapCoordinates(Vector2.Zero, mapId));
                playerManager.SetAttachedEntity(session, playerEnt);

                console = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(2f, 0f), mapId));
                consoleComp = entManager.EnsureComponent<ShipyardConsoleComponent>(console);

                // The drydock list only populates with a card inserted (account-scoped,
                // but the card is the interaction gate — spec). A blank card suffices;
                // the list keys off the operator's session, not the card's deed.
                var itemSlots = entManager.System<ItemSlotsSystem>();
                var card = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(2f, 0f), mapId));
                itemSlots.TryInsert(console, consoleComp.TargetIdSlot, card, user: null);
            });

            Task refreshTask = null!;
            await server.WaitPost(() => refreshTask = shipyard.RefreshDrydockState(console, consoleComp, playerEnt, ShipyardConsoleUiKey.Shipyard));
            await refreshTask;

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // Superset, not exact-set: the integration DB is not reset on pool recycle,
            // so rows written under the shared dummy session's UserId by an earlier
            // Connected test can persist. Assert our two ships are listed rather than
            // demanding the list contain ONLY them (that property is covered by the
            // wrong-owner retrieve test). This is the fix for the earlier 1/22 flake.
            Assert.That(consoleComp.CachedStoredShips.Select(s => s.ShipId),
                Is.SupersetOf(new[] { idA!.Value, idB!.Value }),
                "The drydock cache must list the operator account's freshly stored ships.");

            await pair.CleanReturnAsync();
        }
    }
}
