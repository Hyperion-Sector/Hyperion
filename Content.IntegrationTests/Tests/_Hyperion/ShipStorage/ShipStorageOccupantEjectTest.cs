// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server.Mind;
using Content.Server.Shuttles.Components;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Pins the revised store occupant handling (TryEjectLooseOccupants). On a DOCKED ship, EVERY living
    /// mob is set down on the station — players and animals, loose OR broken out of a crate/carrier —
    /// because none of them round-trip the blob (a mind can't be serialized; a living mob reloads as a
    /// dangling reference that corrupts its container). The one refusal is a mind inside AI apparatus (a
    /// core / intellicard), which must not be force-extracted. (The undocked refusal is covered by
    /// <see cref="ShipStorageOrganicsGateTest"/>.)
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageOccupantEjectTest
    {
        private const string PlayerMobProto = "MobHuman";
        private const string AnimalProto = "MobMouse";

        [Test]
        public async Task LooseLiveMindIsEjectedToDockAndStoreSucceeds()
        {
            await AssertMobEjected(PlayerMobProto, minded: true);
        }

        [Test]
        public async Task LooseMindlessAnimalIsEjectedToDockAndStoreSucceeds()
        {
            await AssertMobEjected(AnimalProto, minded: false);
        }

        [Test]
        public async Task CratedMindIsBrokenOutAndEjectedToDock()
        {
            // A live mind in a plain container (a crate/locker) is broken out and ejected, not refused.
            await AssertMobEjected(PlayerMobProto, minded: true, crateInPlainContainer: true);
        }

        [Test]
        public async Task CagedAnimalIsBrokenOutAndEjectedToDock()
        {
            // A caged animal can't ride the blob either — a reloaded living mob corrupts its container
            // (the dangling-reference / jammed-carrier bug) — so it's broken out and ejected too.
            await AssertMobEjected(AnimalProto, minded: false, crateInPlainContainer: true);
        }

        [Test]
        public async Task AiApparatusMindRefusesTheStore()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var mindSystem = entManager.System<MindSystem>();
            var containers = entManager.System<SharedContainerSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;
            EntityUid brain = default;

            await server.WaitPost(() =>
            {
                (gridUid, _) = CreateDockedShip(entManager, mapManager, mapSystem, protoMan);

                var coords = new EntityCoordinates(gridUid, new Vector2(0.5f, 0.5f));
                // Intellicard carries StationAiHolderComponent (shared with the AI core), so its
                // holder container is what IsInAiApparatus keys on.
                var card = entManager.SpawnEntity("Intellicard", coords);
                Assert.That(containers.TryGetContainer(card, StationAiHolderComponent.Container, out var holder),
                    Is.True, "Intellicard should have its AI-holder container after spawn.");

                brain = entManager.SpawnEntity(PlayerMobProto, coords);
                var mindId = mindSystem.CreateMind(null);
                mindSystem.TransferTo(mindId, brain);
                containers.Insert(brain, holder!);
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var storeResult = await storeTask;

            server.RunTicks(1);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.OrganicsAboard),
                    "A live mind inside AI apparatus must refuse the store, not be force-extracted.");
                Assert.That(entManager.EntityExists(gridUid), Is.True, "A refused store must leave the grid alive.");
                Assert.That(entManager.EntityExists(brain), Is.True, "A refused store must leave the AI brain alive.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Spawns <paramref name="mobProto"/> on a docked ship (optionally mind-bearing, optionally
        /// sealed in a plain crate) and asserts the store succeeds and relocates the mob onto the
        /// station grid, out of any container.
        /// </summary>
        private static async Task AssertMobEjected(
            string mobProto, bool minded, bool crateInPlainContainer = false)
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var mindSystem = entManager.System<MindSystem>();
            var containers = entManager.System<SharedContainerSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;
            EntityUid dockGrid = default;
            EntityUid mob = default;

            await server.WaitPost(() =>
            {
                (gridUid, dockGrid) = CreateDockedShip(entManager, mapManager, mapSystem, protoMan);

                var coords = new EntityCoordinates(gridUid, new Vector2(0.5f, 0.5f));
                mob = entManager.SpawnEntity(mobProto, coords);
                if (minded)
                {
                    var mindId = mindSystem.CreateMind(null);
                    mindSystem.TransferTo(mindId, mob);
                }

                if (crateInPlainContainer)
                {
                    var crate = entManager.SpawnEntity(null, coords);
                    var container = containers.EnsureContainer<Container>(crate, "crate");
                    containers.Insert(mob, container);
                }
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var (storeResult, shipId) = await storeTask;

            server.RunTicks(1);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(storeResult, Is.EqualTo(ShipStorageResult.Success),
                    "A living occupant on a docked ship must NOT block the store; it is ejected instead.");
                Assert.That(shipId, Is.Not.Null);

                Assert.That(entManager.EntityExists(mob), Is.True, "The ejected mob must remain alive.");
                Assert.That(entManager.GetComponent<TransformComponent>(mob).GridUid, Is.EqualTo(dockGrid),
                    "The ejected mob should be relocated onto the docked station grid.");
                Assert.That(containers.IsEntityInContainer(mob), Is.False,
                    "A crated mind must be broken out of its container when ejected.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// A storable ship faked-docked to a floored station grid. FindDockLanding only reads
        /// DockedWith + the partner's transform, and the store's UndockDocks -> Cleanup is null-joint
        /// safe, so a direct DockedWith link sidesteps DockingSystem.Dock's weld/grid machinery.
        /// </summary>
        private static (EntityUid Ship, EntityUid DockGrid) CreateDockedShip(
            IEntityManager entManager, IMapManager mapManager, SharedMapSystem mapSystem, IPrototypeManager protoMan)
        {
            var gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
            ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out var dockGrid);

            // Floor a generous pad on the station so the inward ejection offset lands on a real tile
            // regardless of the (bare) dock port's facing.
            var dockGridComp = entManager.GetComponent<MapGridComponent>(dockGrid);
            for (var x = -3; x <= 7; x++)
                for (var y = -3; y <= 7; y++)
                    mapSystem.SetTile(dockGrid, dockGridComp, new Vector2i(x, y), new Tile(1));

            var shipDock = entManager.SpawnEntity(null, new EntityCoordinates(gridUid, new Vector2(0.5f, 0.5f)));
            var shipDockComp = entManager.EnsureComponent<DockingComponent>(shipDock);
            var stationDock = entManager.SpawnEntity(null, new EntityCoordinates(dockGrid, new Vector2(2f, 2f)));
            var stationDockComp = entManager.EnsureComponent<DockingComponent>(stationDock);
            shipDockComp.DockedWith = stationDock;
            stationDockComp.DockedWith = shipDock;

            return (gridUid, dockGrid);
        }
    }
}
