// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared.CCVar;
using Content.Shared.Explosion.Components;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 2b of ship persistence: pins the HAZARD store gate. A grid carrying an armed
    /// hazard must be REFUSED with <see cref="ShipStorageResult.HazardAboard"/> before serialize.
    /// The refusal has to leave the world untouched: the live grid (and the hazard entity)
    /// stay alive and no blob revision is filed in the DB.
    ///
    /// RFC store-flow gate clause: "no aboard hazard (armed nuke/active countdown/singularity)?".
    /// Anti-abuse rationale: "runtime countdowns are [DataField]s that resume on thaw" — storing an
    /// armed ship would freeze then resume the timer on retrieve, so the store must be refused up front.
    ///
    /// This test exercises the ACTIVE-COUNTDOWN hazard class via <see cref="ActiveTimerTriggerComponent"/>,
    /// arranged directly (AddComp + a positive TimeRemaining, mirroring TriggerSystem's own activation)
    /// rather than by driving a full bomb workflow. The GREEN driver implements detection for all three
    /// hazard classes (armed nuke / active countdown / singularity presence); the singularity class is
    /// implemented-but-tested-only-by-component-check here because spawning a singularity on a grid crashes
    /// dev builds (engine AutomaticAtmos re-entrancy — see the singularity empty-chunk crash note).
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageHazardGateTest
    {
        // A bare marker entity is enough: we AddComp the active timer directly, so we don't need
        // any bomb/grenade prototype. WallSolid is a stable non-organic grid entity to hang the
        // hazard component on without tripping the organics gate.
        private const string HazardHostProto = "WallSolid";

        [Test]
        public async Task StoreWithActiveCountdownAboardIsRefusedHazard()
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

            EntityUid gridUid = default;
            EntityUid hazardUid = default;

            // Build a small grid with one floored tile, drop a host entity on it, and arm an
            // active countdown timer on that entity so the hazard gate has something to trip on.
            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out var mapId);
                var grid = mapManager.CreateGridEntity(mapId);
                gridUid = grid.Owner;

                // Tile before spawn so the host parents to the grid, not the map
                // (per the grid-children rule).
                mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
                entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid.Owner));

                hazardUid = entManager.SpawnEntity(HazardHostProto, new EntityCoordinates(grid.Owner, Vector2.Zero));

                // Active countdown: AddComp the timer with a positive TimeRemaining, matching
                // TriggerSystem's activation path (AddComp then set TimeRemaining). This is the
                // "active countdown" hazard class the store gate must refuse.
                var timer = entManager.AddComponent<ActiveTimerTriggerComponent>(hazardUid);
                timer.TimeRemaining = 30f;
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // No stored ships for this owner before the attempt: the baseline for the
            // "no DB row created" assertion.
            var before = await shipStorage.GetStoredShips(ownerId);
            Assert.That(before, Is.Empty, "Owner should have no stored ships before the refused store.");

            // Attempt the store. The hazard gate must refuse: HazardAboard, null ship id.
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
                Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.HazardAboard),
                    "A grid with an active countdown aboard must be refused with HazardAboard.");
                Assert.That(storeResult.ShipId, Is.Null,
                    "A refused store must not mint a ship id.");

                // The refusal leaves the world untouched: grid (and hazard) still alive.
                Assert.That(entManager.EntityExists(gridUid), Is.True,
                    "The live grid must remain in the sim after a refused store.");
                Assert.That(entManager.EntityExists(hazardUid), Is.True,
                    "The aboard hazard entity must remain in the sim after a refused store.");

                // No blob revision filed for this owner.
                Assert.That(after, Is.Empty,
                    "A refused store must not file a DB row.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
