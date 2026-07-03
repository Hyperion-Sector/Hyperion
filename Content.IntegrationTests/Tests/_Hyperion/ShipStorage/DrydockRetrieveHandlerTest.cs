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
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 5 drydock retrieve handler: the console-side gate in front of
    /// TryRetrieveShip, plus the card-side deed mint. The pipeline owns the DB
    /// owner re-check and the FTL-dock presentation (Cycle 4); the handler owns
    /// resolving the station from the console and stamping a fresh deed onto the
    /// inserted (deed-free) card so the retrieved ship is immediately flyable.
    /// </summary>
    [TestFixture]
    public sealed class DrydockRetrieveHandlerTest
    {
        [Test]
        public async Task OwnerRetrieves_GridReturns_CardDeedMinted()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var playerManager = server.ResolveDependency<IPlayerManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();
            var shipyard = entManager.System<ShipyardSystem>();
            var itemSlots = entManager.System<ItemSlotsSystem>();

            var session = playerManager.Sessions.First();
            var ownerAccount = session.UserId.UserId;

            EntityUid gridUid = default;
            EntityUid station = default;
            EntityUid dockGrid = default;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out dockGrid);
            });

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerAccount));
            var (storeResult, shipId) = await storeTask;
            Assert.That(storeResult, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            EntityUid console = default;
            EntityUid card = default;
            EntityUid playerEnt = default;
            ShipyardConsoleComponent consoleComp = default!;

            await server.WaitPost(() =>
            {
                // The console sits ON the station's dock grid (tile center), so
                // GetOwningStation resolves the requesting station from it. The card
                // is deed-FREE: retrieve mints onto an empty card.
                console = entManager.SpawnEntity(null, new EntityCoordinates(dockGrid, new Vector2(0.5f, 0.5f)));
                consoleComp = entManager.EnsureComponent<ShipyardConsoleComponent>(console);
                card = entManager.SpawnEntity(null, new EntityCoordinates(dockGrid, new Vector2(0.5f, 0.5f)));
                itemSlots.TryInsert(console, consoleComp.TargetIdSlot, card, user: null);

                var xform = entManager.GetComponent<TransformComponent>(dockGrid);
                playerEnt = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(15f, 15f), xform.MapID));
                playerManager.SetAttachedEntity(session, playerEnt);
            });

            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipyard.TryDrydockRetrieve(console, consoleComp, playerEnt, shipId!.Value, ShipyardConsoleUiKey.Shipyard));
            var retrieved = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(retrieved, Is.Not.Null, "Owner retrieve should return the new grid.");
                Assert.That(entManager.TryGetComponent<ShuttleDeedComponent>(card, out var cardDeed), Is.True,
                    "Retrieve must mint a card-side deed onto the inserted card.");
                Assert.That(cardDeed!.ShuttleUid, Is.EqualTo(retrieved), "Minted deed must point at the retrieved grid.");
                Assert.That(cardDeed.DeedHolder, Is.EqualTo(card), "Minted deed must back-reference its card.");

                Assert.That(entManager.TryGetComponent<ShuttleDeedComponent>(retrieved!.Value, out var gridDeed), Is.True);
                Assert.That(gridDeed!.DeedHolder, Is.EqualTo(card),
                    "The grid-side deed must track the freshly minted card as its holder.");
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task WrongOwnerRetrieve_RefusedNoDeed()
        {
            await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var playerManager = server.ResolveDependency<IPlayerManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();
            var shipyard = entManager.System<ShipyardSystem>();
            var itemSlots = entManager.System<ItemSlotsSystem>();

            var session = playerManager.Sessions.First();

            EntityUid gridUid = default;
            EntityUid station = default;
            EntityUid dockGrid = default;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out dockGrid);
            });

            // Stored under a STRANGER's account, not the operator session's.
            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, Guid.NewGuid()));
            var (storeResult, shipId) = await storeTask;
            Assert.That(storeResult, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            EntityUid console = default;
            EntityUid card = default;
            EntityUid playerEnt = default;
            ShipyardConsoleComponent consoleComp = default!;

            await server.WaitPost(() =>
            {
                console = entManager.SpawnEntity(null, new EntityCoordinates(dockGrid, new Vector2(0.5f, 0.5f)));
                consoleComp = entManager.EnsureComponent<ShipyardConsoleComponent>(console);
                card = entManager.SpawnEntity(null, new EntityCoordinates(dockGrid, new Vector2(0.5f, 0.5f)));
                itemSlots.TryInsert(console, consoleComp.TargetIdSlot, card, user: null);

                var xform = entManager.GetComponent<TransformComponent>(dockGrid);
                playerEnt = entManager.SpawnEntity(null, new MapCoordinates(new Vector2(15f, 15f), xform.MapID));
                playerManager.SetAttachedEntity(session, playerEnt);
            });

            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipyard.TryDrydockRetrieve(console, consoleComp, playerEnt, shipId!.Value, ShipyardConsoleUiKey.Shipyard));
            var retrieved = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            Assert.Multiple(() =>
            {
                Assert.That(retrieved, Is.Null, "Another account's ship must not retrieve.");
                Assert.That(entManager.HasComponent<ShuttleDeedComponent>(card), Is.False,
                    "A refused retrieve must not mint a deed.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
