// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 3 of ship persistence: pins the store strip-list. Before <c>TrySaveGrid</c>
    /// the store must remove derived / session-scoped components on a named strip-list
    /// from the live grid so they never enter the blob. <see cref="ShipRepairDataComponent"/>
    /// is the founding member: it lives on the grid entity, holds session-scoped
    /// <see cref="Robust.Shared.GameObjects.NetEntity"/> refs plus raw tile TypeIds that rot
    /// across a reload, and it is a plain <c>[DataField]</c>-serializable component, so absent
    /// a strip it would round-trip straight back onto the reloaded grid.
    ///
    /// <para>Round-trip half — a grid carrying <see cref="ShipRepairDataComponent"/> is
    /// stored and retrieved; the retrieved grid must NOT carry the component. (Scope is
    /// strictly the STRIP — regenerating repair data against the loaded grid is a later
    /// rehydration cycle, so the retrieved grid legitimately has no repair data at all.)</para>
    ///
    /// <para>Abort-restore half — the component is re-attached to the retrieved grid, a
    /// validation abort is forced via the <see cref="ShipStorageSystem.ValidationMismatchOverride"/>
    /// seam, and a store is attempted. It must return
    /// <see cref="ShipStorageResult.ValidationFailed"/> AND leave the component on the live
    /// grid: the strip must be undone on abort, mirroring the sidecar failure-cleanup.</para>
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageStripListTest
    {
        private const string StackProto = "SheetSteel1";

        // Non-default sentinel (the component's default ChunkSize is 5): recognizable dummy
        // data that proves the component is non-default and must serialize, so the round-trip
        // assertion can't pass vacuously.
        private const int SentinelChunkSize = 7;

        [Test]
        public async Task StoreStripsRepairDataAndAbortRestoresIt()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var cfg = server.ResolveDependency<IConfigurationManager>();
            Assert.That(cfg.GetCVar(CCVars.GridFill), Is.False);

            var ownerId = Guid.NewGuid();

            // ---- Build a small grid carrying a populated ShipRepairDataComponent. ----
            EntityUid gridUid = default;
            await server.WaitPost(() =>
            {
                gridUid = BuildSmallGrid(entManager, mapManager, mapSystem);

                var repairData = entManager.EnsureComponent<ShipRepairDataComponent>(gridUid);
                repairData.ChunkSize = SentinelChunkSize;

                Assert.That(entManager.GetComponent<ShipRepairDataComponent>(gridUid).ChunkSize,
                    Is.EqualTo(SentinelChunkSize),
                    "Sentinel chunk size did not stick on the pre-store grid.");
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // ---- Store: strip-list must remove ShipRepairDataComponent before serialize. ----
            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() =>
            {
                shipStorage.ValidationMismatchOverride = null;
                storeTask = shipStorage.TryStoreShip(gridUid, ownerId);
            });
            var storeResult = await storeTask;

            Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.Success),
                "The grid should store successfully; the strip-list is not a refusal.");
            Assert.That(storeResult.ShipId, Is.Not.Null, "A successful store must mint a ship id.");

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // ---- Retrieve and let the deserialized grid settle. ----
            EntityUid? retrievedGrid = null;
            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(storeResult.ShipId!.Value, ownerId));
            retrievedGrid = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            // Round-trip half: this is the assertion that fails today. With no strip-list the
            // [DataField] component serializes into the blob and comes back on the reloaded grid.
            await server.WaitAssertion(() =>
            {
                Assert.That(retrievedGrid, Is.Not.Null, "TryRetrieveShip returned no grid.");
                Assert.That(entManager.EntityExists(retrievedGrid!.Value), Is.True,
                    "The retrieved grid should exist in the sim.");
                Assert.That(entManager.HasComponent<ShipRepairDataComponent>(retrievedGrid!.Value), Is.False,
                    "The strip-list must remove ShipRepairDataComponent before serialize, so the "
                    + "retrieved grid must not carry a stale copy.");
            });

            // ---- Abort-restore half: a stripped component must be restored on abort. ----
            await server.WaitPost(() =>
            {
                var repairData = entManager.EnsureComponent<ShipRepairDataComponent>(retrievedGrid!.Value);
                repairData.ChunkSize = SentinelChunkSize;
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<(ShipStorageResult Result, Guid? ShipId)> abortTask = null!;
            await server.WaitPost(() =>
            {
                // Force the round-trip diff to mismatch for the live grid so the store aborts
                // AFTER the strip-list has removed the component.
                shipStorage.ValidationMismatchOverride = uid => uid == retrievedGrid!.Value;
                abortTask = shipStorage.TryStoreShip(retrievedGrid!.Value, ownerId);
            });
            var abortResult = await abortTask;

            await server.WaitPost(() => shipStorage.ValidationMismatchOverride = null);

            server.RunTicks(1);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(abortResult.Result, Is.EqualTo(ShipStorageResult.ValidationFailed),
                    "A forced round-trip mismatch must abort the store.");
                Assert.That(entManager.EntityExists(retrievedGrid!.Value), Is.True,
                    "The live grid must remain in the sim after an aborted store.");
                Assert.That(entManager.HasComponent<ShipRepairDataComponent>(retrievedGrid!.Value), Is.True,
                    "An aborted store must restore the stripped ShipRepairDataComponent to the live grid, "
                    + "mirroring the sidecar failure-cleanup.");
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
