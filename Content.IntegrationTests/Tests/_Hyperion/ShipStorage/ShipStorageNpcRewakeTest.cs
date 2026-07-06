// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server.Shuttles.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Pins the retrieve-side HTN re-wake (RewakeNpcs). Retrieve loads onto the already-initialized
    /// shipyard map and never raises MapInitEvent, so OnNPCMapInit — the sole installer of the NPC
    /// wake (ActiveNPCComponent) and the blackboard Owner — never fires. Without the re-wake a
    /// reborn ship's autopilot (and any other persisted HTN equipment, e.g. turrets) comes back
    /// inert. The autopilot console is the concrete case and the general proof of the re-wake: it is
    /// anchored HTN ship equipment that persists through storage. It is stored deliberately asleep
    /// with a corrupted Owner and a live autopilot destination, and retrieve must wake it, re-seed
    /// the Owner, and clear the stale destination.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageNpcRewakeTest
    {
        [Test]
        public async Task AutopilotConsoleIsReawokenAndTargetClearedOnRetrieve()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var npc = entManager.System<NPCSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;
            EntityUid station = default;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out _);

                // ComputerShuttle carries the autopilot HTN (rootTask AutopilotShuttleCompound).
                var console = entManager.SpawnEntity("ComputerShuttle", new EntityCoordinates(gridUid, new Vector2(0.5f, 0.5f)));

                var htn = entManager.GetComponent<HTNComponent>(console);
                var comp = entManager.GetComponent<ShuttleConsoleComponent>(console);

                // Simulate a player having set an autopilot destination, then the pre-store rot:
                // asleep (no ActiveNPCComponent to serialize) and a stale Owner (the EntityUid-rot a
                // reload leaves). The destination must NOT survive — a stored ship is at rest and
                // mustn't fly off to a now-gone point on retrieve.
                htn.Blackboard.SetValue(comp.AutopilotTargetKey, new EntityCoordinates(gridUid, Vector2.Zero));
                npc.SleepNPC(console, htn);
                htn.Blackboard.SetValue(NPCBlackboard.Owner, EntityUid.Invalid);
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var (storeResult, shipId) = await storeTask;
            Assert.That(storeResult, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(shipId!.Value, ownerId, station));
            var retrieved = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(retrieved, Is.Not.Null, "Retrieve returned no grid.");

                var console = FindHtnOnGrid(entManager, retrieved!.Value);
                Assert.That(console, Is.Not.EqualTo(EntityUid.Invalid),
                    "The shuttle console should be a child of the retrieved grid.");

                var htn = entManager.GetComponent<HTNComponent>(console);
                var comp = entManager.GetComponent<ShuttleConsoleComponent>(console);

                Assert.That(npc.IsAwake(console, htn), Is.True,
                    "RewakeNpcs must re-activate the autopilot console so it can plan and steer again.");
                Assert.That(htn.Blackboard.TryGetValue<EntityUid>(NPCBlackboard.Owner, out var owner, entManager)
                        && owner == console,
                    Is.True, "RewakeNpcs must re-seed the console's blackboard Owner to the reloaded uid.");
                Assert.That(htn.Blackboard.TryGetValue<EntityCoordinates>(comp.AutopilotTargetKey, out _, entManager),
                    Is.False, "The pre-store autopilot destination must be cleared on retrieve.");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>First HTN NPC that is a child of <paramref name="gridUid"/>, or Invalid.</summary>
        private static EntityUid FindHtnOnGrid(IEntityManager entManager, EntityUid gridUid)
        {
            var query = entManager.EntityQueryEnumerator<HTNComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.GridUid == gridUid)
                    return uid;
            }

            return EntityUid.Invalid;
        }
    }
}
