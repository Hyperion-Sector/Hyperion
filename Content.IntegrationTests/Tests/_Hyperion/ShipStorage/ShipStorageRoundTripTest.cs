// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server.Stack;
using Content.Shared.CCVar;
using Content.Shared.Stacks;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Phase-1 ship persistence spine: pins the minimal store -> retrieve round-trip
    /// through <see cref="ShipStorageSystem"/>. A grid carrying an observable player
    /// modification (a stack whose Count differs from its prototype baseline) is stored,
    /// the live grid is removed from the sim, then the ship is retrieved by id + owner
    /// and the modification must survive the blob round-trip intact.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageRoundTripTest
    {
        // SheetSteel1's prototype count is 1; pick a sentinel that provably differs so the
        // engine delta serializer must record it (otherwise the assertion passes vacuously).
        private const string StackProto = "SheetSteel1";
        private const int SentinelCount = 37;

        [Test]
        public async Task StoreThenRetrievePreservesStackCount()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var stackSystem = entManager.System<StackSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var cfg = server.ResolveDependency<IConfigurationManager>();
            Assert.That(cfg.GetCVar(CCVars.GridFill), Is.False);

            // Stable synthetic owner: no logged-in session on the operator side in this slice.
            // Store and retrieve MUST use the same owner id.
            var ownerId = Guid.NewGuid();

            EntityUid gridUid = default;
            EntityUid stackUid = default;

            // Build a small grid with one floored tile, drop a stack on it, and set the
            // sentinel count so there is a player modification to preserve across the round-trip.
            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out var mapId);
                var grid = mapManager.CreateGridEntity(mapId);
                gridUid = grid.Owner;

                // Tile before spawn so the stack parents to the grid, not the map
                // (per the grid-children rule).
                mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));

                entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid.Owner));

                stackUid = entManager.SpawnEntity(StackProto, new EntityCoordinates(grid.Owner, Vector2.Zero));
                stackSystem.SetCount(stackUid, SentinelCount);

                Assert.That(entManager.GetComponent<StackComponent>(stackUid).Count, Is.EqualTo(SentinelCount),
                    "Sentinel count did not stick on the pre-store stack.");
            });

            // Store the grid. serialize -> commit -> despawn: the returned task completes when
            // the blob is filed; the grid deletion is the last step (QueueDel, resolves next tick).
            Guid? shipId = null;
            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var storeResult = await storeTask;
            shipId = storeResult.ShipId;

            Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.Success),
                "TryStoreShip should succeed for a mindless grid.");
            Assert.That(shipId, Is.Not.Null, "TryStoreShip returned null: a gate refused the store.");

            // QueueDel is deferred; assert the live grid is gone only after a tick settles.
            server.RunTicks(1);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
                Assert.That(entManager.EntityExists(gridUid), Is.False,
                    "The live grid should be removed from the sim after a successful store."));

            // Retrieve the ship for the same owner and let the deserialized grid settle.
            EntityUid? retrievedGrid = null;
            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(shipId!.Value, ownerId));
            retrievedGrid = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(retrievedGrid, Is.Not.Null, "TryRetrieveShip returned no grid.");
                Assert.That(entManager.EntityExists(retrievedGrid!.Value), Is.True,
                    "The retrieved grid should exist in the sim.");

                // Find the stack on the reloaded grid and assert the modification survived.
                var found = false;
                var query = entManager.AllEntityQueryEnumerator<StackComponent, TransformComponent>();
                while (query.MoveNext(out _, out var stack, out var xform))
                {
                    if (xform.GridUid != retrievedGrid.Value)
                        continue;

                    found = true;
                    Assert.That(stack.Count, Is.EqualTo(SentinelCount),
                        "The reloaded stack's Count must equal the stored sentinel, not the prototype default.");
                }

                Assert.That(found, Is.True, "No stack was found on the retrieved grid.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
