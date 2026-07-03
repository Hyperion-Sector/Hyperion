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
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 5 drydock store handler: the console-side gate in front of TryStoreShip.
    /// Garage ownership is the SHIP'S stamped account (ShipOwnershipComponent), never
    /// the console operator — a borrowed deed card must not let a stranger store (or
    /// re-home) someone else's ship. On success the card-side deed is stripped (the
    /// grid it pointed at is gone), mirroring the sell path.
    /// </summary>
    [TestFixture]
    public sealed class DrydockStoreHandlerTest
    {
        [Test]
        public async Task OwnerStoresShip_GridDespawned_CardDeedRemoved()
        {
            // Connected pair: these tests resolve the operator's account from a real
            // player session (the default pool pair has none).
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var playerManager = server.ResolveDependency<IPlayerManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipyard = entManager.System<ShipyardSystem>();
            var itemSlots = entManager.System<ItemSlotsSystem>();

            var session = playerManager.Sessions.First();

            EntityUid gridUid = default;
            EntityUid console = default;
            EntityUid card = default;
            EntityUid playerEnt = default;
            ShipyardConsoleComponent consoleComp = default!;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out var mapId);

                // The operator is the ship's stamped account-owner.
                var ownership = entManager.EnsureComponent<ShipOwnershipComponent>(gridUid);
                ownership.OwnerUserId = session.UserId;

                // Off-grid: a session-attached entity standing ON the grid trips the
                // organics gate (that's the gate working, not a bug).
                playerEnt = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(10f, 10f), mapId));
                playerManager.SetAttachedEntity(session, playerEnt);

                (console, consoleComp, card) = ShipStorageTestHelpers.CreateDrydockConsoleWithDeedCard(
                    entManager, itemSlots, shipyard, gridUid, playerEnt, mapId);
            });

            Task<(ShipStorageResult Result, Guid? ShipId)?> storeTask = null!;
            await server.WaitPost(() => storeTask = shipyard.TryDrydockStore(console, consoleComp, playerEnt, ShipyardConsoleUiKey.Shipyard));
            var storeResult = await storeTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            Assert.Multiple(() =>
            {
                Assert.That(storeResult, Is.Not.Null, "The owner-operated store should reach the pipeline.");
                Assert.That(storeResult!.Value.Result, Is.EqualTo(ShipStorageResult.Success));
                Assert.That(entManager.Deleted(gridUid), Is.True, "Owner store should despawn the grid.");
                Assert.That(entManager.HasComponent<ShuttleDeedComponent>(card), Is.False,
                    "The card-side deed must be stripped on a successful store.");
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task OwnerStore_OrganicsAboard_RefusedGridAndDeedIntact()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var playerManager = server.ResolveDependency<IPlayerManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var mindSystem = entManager.System<Content.Server.Mind.MindSystem>();
            var shipyard = entManager.System<ShipyardSystem>();
            var itemSlots = entManager.System<ItemSlotsSystem>();

            var session = playerManager.Sessions.First();

            EntityUid gridUid = default;
            EntityUid console = default;
            EntityUid card = default;
            EntityUid playerEnt = default;
            ShipyardConsoleComponent consoleComp = default!;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out var mapId);

                var ownership = entManager.EnsureComponent<ShipOwnershipComponent>(gridUid);
                ownership.OwnerUserId = session.UserId;

                // A mind-bearing mob aboard trips the organics gate inside TryStoreShip.
                var mob = entManager.SpawnEntity("MobHuman", new EntityCoordinates(gridUid, new Vector2(0.5f, 0.5f)));
                var mindId = mindSystem.CreateMind(null);
                mindSystem.TransferTo(mindId, mob);

                playerEnt = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(10f, 10f), mapId));
                playerManager.SetAttachedEntity(session, playerEnt);

                (console, consoleComp, card) = ShipStorageTestHelpers.CreateDrydockConsoleWithDeedCard(
                    entManager, itemSlots, shipyard, gridUid, playerEnt, mapId);
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<(ShipStorageResult Result, Guid? ShipId)?> storeTask = null!;
            await server.WaitPost(() => storeTask = shipyard.TryDrydockStore(console, consoleComp, playerEnt, ShipyardConsoleUiKey.Shipyard));
            var storeResult = await storeTask;

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Assert.Multiple(() =>
            {
                Assert.That(storeResult, Is.Not.Null, "The owner-operated store should reach the pipeline gate.");
                Assert.That(storeResult!.Value.Result, Is.EqualTo(ShipStorageResult.OrganicsAboard),
                    "A crewed ship must be refused with OrganicsAboard at the console layer.");
                Assert.That(entManager.Deleted(gridUid), Is.False, "A refused store must leave the grid alive.");
                Assert.That(entManager.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "A refused store must leave the card-side deed in place.");
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task NonOwnerStore_Refused_GridIntact()
        {
            // Connected pair: these tests resolve the operator's account from a real
            // player session (the default pool pair has none).
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var playerManager = server.ResolveDependency<IPlayerManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipyard = entManager.System<ShipyardSystem>();
            var itemSlots = entManager.System<ItemSlotsSystem>();

            var session = playerManager.Sessions.First();

            EntityUid gridUid = default;
            EntityUid console = default;
            EntityUid card = default;
            EntityUid playerEnt = default;
            ShipyardConsoleComponent consoleComp = default!;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out var mapId);

                // The ship belongs to a DIFFERENT account than the operator's session.
                var ownership = entManager.EnsureComponent<ShipOwnershipComponent>(gridUid);
                ownership.OwnerUserId = new NetUserId(Guid.NewGuid());

                // Off-grid: a session-attached entity standing ON the grid trips the
                // organics gate (that's the gate working, not a bug).
                playerEnt = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(10f, 10f), mapId));
                playerManager.SetAttachedEntity(session, playerEnt);

                (console, consoleComp, card) = ShipStorageTestHelpers.CreateDrydockConsoleWithDeedCard(
                    entManager, itemSlots, shipyard, gridUid, playerEnt, mapId);
            });

            Task<(ShipStorageResult Result, Guid? ShipId)?> storeTask = null!;
            await server.WaitPost(() => storeTask = shipyard.TryDrydockStore(console, consoleComp, playerEnt, ShipyardConsoleUiKey.Shipyard));
            var storeResult = await storeTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            Assert.Multiple(() =>
            {
                Assert.That(storeResult, Is.Null, "A non-owner store must be refused at the gate.");
                Assert.That(entManager.Deleted(gridUid), Is.False,
                    "A refused store must leave the grid untouched.");
                Assert.That(entManager.HasComponent<ShuttleDeedComponent>(card), Is.True,
                    "A refused store must leave the card-side deed in place.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
