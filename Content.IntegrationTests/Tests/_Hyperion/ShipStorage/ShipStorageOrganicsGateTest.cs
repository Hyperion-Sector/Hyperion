// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server.Mind;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 2a of ship persistence: pins the ORGANICS store gate. A grid carrying a
    /// mind-bearing, living mob must be REFUSED with <see cref="ShipStorageResult.OrganicsAboard"/>.
    /// The refusal has to leave the world untouched: the live grid stays alive and no
    /// blob revision is filed in the DB. FoundOrganics (reused from the NF shipyard) trips
    /// on live minds even without a player session, so a MindSystem-created mind transferred
    /// onto a spawned mob is enough to exercise the gate.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageOrganicsGateTest
    {
        // MobHuman carries MobStateComponent + MindContainerComponent, so a transferred
        // mind resolves via TryGetMind(child, ...) and the mob is Alive on spawn (not
        // dead physically) — exactly the branch FoundOrganics refuses on.
        private const string MobProto = "MobHuman";

        [Test]
        public async Task StoreWithMindAboardIsRefusedOrganics()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var mindSystem = entManager.System<MindSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var cfg = server.ResolveDependency<IConfigurationManager>();
            Assert.That(cfg.GetCVar(CCVars.GridFill), Is.False);

            var ownerId = Guid.NewGuid();

            EntityUid gridUid = default;
            EntityUid mobUid = default;

            // Build a small grid with one floored tile, drop a mob on it, and give the mob a
            // live mind (no player session needed) so the organics gate has something to trip on.
            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out var mapId);
                var grid = mapManager.CreateGridEntity(mapId);
                gridUid = grid.Owner;

                // Tile before spawn so the mob parents to the grid, not the map
                // (per the grid-children rule).
                mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
                entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid.Owner));

                mobUid = entManager.SpawnEntity(MobProto, new EntityCoordinates(grid.Owner, Vector2.Zero));

                // Session-less mind: CreateMind(null) + TransferTo links a MindComponent to the
                // mob's MindContainer, which is what FoundOrganics' TryGetMind branch keys on.
                var mindId = mindSystem.CreateMind(null);
                mindSystem.TransferTo(mindId, mobUid);
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // No stored ships for this owner before the attempt: the baseline for the
            // "no DB row created" assertion.
            var before = await shipStorage.GetStoredShips(ownerId);
            Assert.That(before, Is.Empty, "Owner should have no stored ships before the refused store.");

            // Attempt the store. The organics gate must refuse: OrganicsAboard, null ship id.
            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var storeResult = await storeTask;

            // QueueDel is deferred; pump a tick so that IF the (bugged) store despawned the
            // grid, the deletion has resolved and the "grid still alive" assert is honest.
            server.RunTicks(1);
            await server.WaitIdleAsync();

            var after = await shipStorage.GetStoredShips(ownerId);

            await server.WaitAssertion(() =>
            {
                Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.OrganicsAboard),
                    "A grid with a mind-bearing mob aboard must be refused with OrganicsAboard.");
                Assert.That(storeResult.ShipId, Is.Null,
                    "A refused store must not mint a ship id.");

                // The refusal leaves the world untouched: grid (and mob) still alive.
                Assert.That(entManager.EntityExists(gridUid), Is.True,
                    "The live grid must remain in the sim after a refused store.");
                Assert.That(entManager.EntityExists(mobUid), Is.True,
                    "The aboard mob must remain in the sim after a refused store.");

                // No blob revision filed for this owner.
                Assert.That(after, Is.Empty,
                    "A refused store must not file a DB row.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
