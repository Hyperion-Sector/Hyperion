// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server.Shuttles.Components;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 3 of ship persistence: pins the active-ship registry (the sequential
    /// double-retrieve gate). <see cref="ShipStorageSystem"/> holds a round-scoped
    /// <c>ShipId -&gt; live GridUid</c> map. The DB row-lock only stops CONCURRENT
    /// double-retrieve; this closes the SEQUENTIAL dupe window (retrieve a ship, fly it,
    /// then try to retrieve the same id again at another console).
    ///
    /// <para>The contract in one pass:</para>
    /// <list type="bullet">
    /// <item>Store a ship, retrieve it once — grid A materializes and the id registers.</item>
    /// <item>Retrieve the SAME still-flying id again — must refuse (null) and spawn NO
    /// second grid; grid A stays alive. This is the half that fails today.</item>
    /// <item>Delete grid A — the registry entry clears on grid deletion, so a legitimate
    /// re-retrieve succeeds again.</item>
    /// <item>Store the live copy — the registry entry clears on store (the store despawns
    /// the grid), so a subsequent retrieve succeeds again.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageActiveRegistryTest
    {
        [Test]
        public async Task SequentialDoubleRetrieveIsRefusedUntilReleased()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var cfg = server.ResolveDependency<IConfigurationManager>();
            Assert.That(cfg.GetCVar(CCVars.GridFill), Is.False);

            // Store and retrieve MUST use the same owner id (no logged-in session in this slice).
            var ownerId = Guid.NewGuid();

            // ---- Setup: store a ship so there is a blob to retrieve. ----

            EntityUid gridUid = default;
            await server.WaitPost(() => gridUid = BuildSmallGrid(entManager, mapManager, mapSystem));

            EntityUid station = default;
            await server.WaitPost(() =>
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out _));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var storeResult = await storeTask;

            Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.Success),
                "Setup store of a mindless grid should succeed.");
            Assert.That(storeResult.ShipId, Is.Not.Null, "Setup store should mint a ship id.");
            var shipId = storeResult.ShipId!.Value;

            // The store QueueDels the grid; let the deletion settle before retrieving.
            server.RunTicks(1);
            await server.WaitIdleAsync();

            // ---- First retrieve: grid A materializes and the ship id registers. ----

            Task<EntityUid?> firstTask = null!;
            await server.WaitPost(() => firstTask = shipStorage.TryRetrieveShip(shipId, ownerId, station));
            var firstGrid = await firstTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            Assert.That(firstGrid, Is.Not.Null, "First retrieve of a stored ship should materialize a grid.");

            // Snapshot the live grid set with grid A present, so a second retrieve that
            // (wrongly) spawns a duplicate shows up as a NEW grid.
            HashSet<EntityUid> gridsBeforeDupe = null!;
            await server.WaitAssertion(() => gridsBeforeDupe = SnapshotGrids(entManager));

            // ---- Second (sequential) retrieve of the SAME still-flying id: must refuse. ----

            Task<EntityUid?> dupeTask = null!;
            await server.WaitPost(() => dupeTask = shipStorage.TryRetrieveShip(shipId, ownerId, station));
            var dupeGrid = await dupeTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            HashSet<EntityUid> gridsAfterDupe = null!;
            await server.WaitAssertion(() => gridsAfterDupe = SnapshotGrids(entManager));

            await server.WaitAssertion(() =>
            {
                // Dupe-refusal is the first failing assertion today: with no registry the
                // second retrieve deserializes a second copy and returns a non-null grid.
                Assert.That(dupeGrid, Is.Null,
                    "A sequential second retrieve of a still-flying ship must refuse (return null).");

                Assert.That(entManager.EntityExists(firstGrid!.Value), Is.True,
                    "The first live grid must remain after a refused dupe retrieve.");

                var newGrids = new HashSet<EntityUid>(gridsAfterDupe);
                newGrids.ExceptWith(gridsBeforeDupe);
                Assert.That(newGrids, Is.Empty,
                    "A refused dupe retrieve must not spawn a second grid — exactly one live copy stands.");
            });

            // ---- Release path (a): deleting grid A clears the registry entry. ----

            await server.WaitPost(() => entManager.QueueDeleteEntity(firstGrid!.Value));

            server.RunTicks(2);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
                Assert.That(entManager.EntityExists(firstGrid!.Value), Is.False,
                    "Grid A should be gone after QueueDel settles."));

            Task<EntityUid?> reTask = null!;
            await server.WaitPost(() => reTask = shipStorage.TryRetrieveShip(shipId, ownerId, station));
            var reGrid = await reTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            Assert.That(reGrid, Is.Not.Null,
                "After grid A is deleted the registry entry clears, so a legitimate re-retrieve succeeds.");

            // ---- Release path (b): storing the live copy clears the registry entry. ----

            Task<(ShipStorageResult Result, Guid? ShipId)> reStoreTask = null!;
            await server.WaitPost(() => reStoreTask = shipStorage.TryStoreShip(reGrid!.Value, ownerId));
            var reStoreResult = await reStoreTask;

            Assert.That(reStoreResult.Result, Is.EqualTo(ShipStorageResult.Success),
                "Re-storing the live copy should succeed.");

            // RED-TEAM finding #1 (identity-fork on re-store): TryStoreShip always mints
            // a fresh Guid.NewGuid(), with nothing recording that this live grid was
            // materialized from an existing ShipId. So a re-store of a RETRIEVED ship
            // forks a brand-new DB row while the original row stays fully retrievable —
            // unbounded, uncapped duplication on the happy path. The store must RESOLVE
            // the retrieved identity, not MINT a new one.
            Assert.That(reStoreResult.ShipId, Is.EqualTo(shipId),
                "Re-storing a retrieved ship must reuse its ShipId (resolve, not mint a fork).");

            // ...and the DB must not have grown a second row: after the full
            // store -> retrieve -> re-store cycle of ONE ship, the owner still owns exactly
            // one stored ship. GetStoredShips is a DB task, awaited outside WaitPost like
            // the store/retrieve tasks above; the re-store already committed by this point.
            var ownerShipsAfterReStore = await shipStorage.GetStoredShips(ownerId);
            Assert.That(ownerShipsAfterReStore.Count, Is.EqualTo(1),
                "After store -> retrieve -> re-store of one ship the owner must still have exactly "
                + "one stored ship in the DB, not a forked duplicate.");

            // The store despawns the retrieved grid; let it settle so the entry clears.
            server.RunTicks(2);
            await server.WaitIdleAsync();

            Task<EntityUid?> finalTask = null!;
            await server.WaitPost(() => finalTask = shipStorage.TryRetrieveShip(shipId, ownerId, station));
            var finalGrid = await finalTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            Assert.That(finalGrid, Is.Not.Null,
                "After a store clears the registry entry, retrieve succeeds again.");

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Snapshots the set of live grid entities. Run on the server thread. New grids
        /// appearing between two snapshots reveal a duplicate materialization.
        /// </summary>
        private static HashSet<EntityUid> SnapshotGrids(IEntityManager entManager)
        {
            var set = new HashSet<EntityUid>();
            var query = entManager.AllEntityQueryEnumerator<MapGridComponent>();
            while (query.MoveNext(out var uid, out _))
                set.Add(uid);

            return set;
        }

        /// <summary>
        /// Builds a small grid with one floored tile, mirroring the other ShipStorage tests'
        /// setup. Tile before MapInit so children parent to the grid (per the grid-children
        /// rule). Must run inside a WaitPost.
        /// </summary>
        private static EntityUid BuildSmallGrid(
            IEntityManager entManager,
            IMapManager mapManager,
            SharedMapSystem mapSystem)
        {
            mapSystem.CreateMap(out var mapId);
            var grid = mapManager.CreateGridEntity(mapId);

            mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
            entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid.Owner));

            // Retrieve treats a blob without ShuttleComponent as a load failure; every
            // storable test grid carries one, like every real ship does.
            entManager.EnsureComponent<ShuttleComponent>(grid.Owner);

            return grid.Owner;
        }
    }
}
