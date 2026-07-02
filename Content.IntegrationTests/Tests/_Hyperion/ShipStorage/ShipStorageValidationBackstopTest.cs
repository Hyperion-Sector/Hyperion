// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 3 of ship persistence: pins the save-time validation backstop. Before a store
    /// commits a blob it must diff the freshly serialized round-trip against the live grid;
    /// on any persistent-state mismatch the store ABORTS. This test pins both halves of that
    /// contract in one pass:
    ///
    /// <para>Happy path — a normal grid stores clean: validation runs, passes, the call
    /// returns <see cref="ShipStorageResult.Success"/> and a DB row lands.</para>
    ///
    /// <para>Forced mismatch — with the round-trip diff forced to disagree via the
    /// <see cref="ShipStorageSystem.ValidationMismatchOverride"/> seam, the store must return
    /// <see cref="ShipStorageResult.ValidationFailed"/>, mint no ship id, file no DB row, and
    /// leave the live grid alive and usable. A genuine mismatch of persistent
    /// <c>[DataField]</c> state can't be manufactured without a real serializer bug (those
    /// fields round-trip by definition), so the seam is the deterministic, rot-proof lever.</para>
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageValidationBackstopTest
    {
        private const string StackProto = "SheetSteel1";

        [Test]
        public async Task RoundTripMismatchAbortsStore()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var cfg = server.ResolveDependency<IConfigurationManager>();
            Assert.That(cfg.GetCVar(CCVars.GridFill), Is.False);

            // Independent owners so each half's "row landed" / "no row filed" DB assertion
            // stands on its own.
            var ownerHappy = Guid.NewGuid();
            var ownerAbort = Guid.NewGuid();

            // ---- Half A: a clean store passes the backstop and files a row. ----

            EntityUid happyGrid = default;
            await server.WaitPost(() => happyGrid = BuildSmallGrid(entManager, mapManager, mapSystem));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<(ShipStorageResult Result, Guid? ShipId)> happyTask = null!;
            await server.WaitPost(() =>
            {
                // No override: the backstop diffs for real, and a faithful round-trip passes.
                shipStorage.ValidationMismatchOverride = null;
                happyTask = shipStorage.TryStoreShip(happyGrid, ownerHappy);
            });
            var happyResult = await happyTask;

            server.RunTicks(1);
            await server.WaitIdleAsync();

            var happyRows = await shipStorage.GetStoredShips(ownerHappy);

            Assert.Multiple(() =>
            {
                Assert.That(happyResult.Result, Is.EqualTo(ShipStorageResult.Success),
                    "A clean grid whose round-trip validates must store successfully.");
                Assert.That(happyResult.ShipId, Is.Not.Null,
                    "A successful store must mint a ship id.");
                Assert.That(happyRows, Is.Not.Empty,
                    "A successful store must file a DB row.");
            });

            // ---- Half B: a forced round-trip mismatch aborts, leaving the world intact. ----

            EntityUid abortGrid = default;
            await server.WaitPost(() => abortGrid = BuildSmallGrid(entManager, mapManager, mapSystem));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // No stored ships for the abort owner before the attempt: the baseline for the
            // "no DB row filed" assertion.
            var beforeAbort = await shipStorage.GetStoredShips(ownerAbort);
            Assert.That(beforeAbort, Is.Empty, "Abort owner should have no stored ships before the attempt.");

            Task<(ShipStorageResult Result, Guid? ShipId)> abortTask = null!;
            await server.WaitPost(() =>
            {
                // Force the backstop's round-trip diff to report a mismatch for this grid.
                shipStorage.ValidationMismatchOverride = uid => uid == abortGrid;
                abortTask = shipStorage.TryStoreShip(abortGrid, ownerAbort);
            });
            var abortResult = await abortTask;

            // Clear the seam so a recycled pair can't leak the override into a later test.
            await server.WaitPost(() => shipStorage.ValidationMismatchOverride = null);

            // QueueDel is deferred; pump a tick so that IF the (backstop-less) store despawned
            // the grid, the deletion has resolved and the "grid still alive" assert is honest.
            server.RunTicks(1);
            await server.WaitIdleAsync();

            var afterAbort = await shipStorage.GetStoredShips(ownerAbort);

            await server.WaitAssertion(() =>
            {
                Assert.That(abortResult.Result, Is.EqualTo(ShipStorageResult.ValidationFailed),
                    "A store whose round-trip diff mismatches must abort with ValidationFailed.");
                Assert.That(abortResult.ShipId, Is.Null,
                    "An aborted store must not mint a ship id.");

                // The abort leaves the live grid alive and usable, not half-committed or dropped.
                Assert.That(entManager.EntityExists(abortGrid), Is.True,
                    "The live grid must remain in the sim after an aborted store.");
                Assert.That(entManager.HasComponent<MapGridComponent>(abortGrid), Is.True,
                    "The live grid must still be a usable grid after an aborted store.");

                // No blob revision filed for the abort owner.
                Assert.That(afterAbort, Is.Empty,
                    "An aborted store must not file a DB row.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Builds a small grid with one floored tile and a stack on it, mirroring the other
        /// ShipStorage tests' setup. Tile before spawn so the stack parents to the grid, not
        /// the map (per the grid-children rule). Must run inside a WaitPost.
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

            entManager.SpawnEntity(StackProto, new EntityCoordinates(grid.Owner, Vector2.Zero));

            return grid.Owner;
        }
    }
}
