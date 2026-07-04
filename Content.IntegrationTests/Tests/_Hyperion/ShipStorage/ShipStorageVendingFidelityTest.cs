// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server.Shuttles.Components;
using Content.Shared.CCVar;
using Content.Shared.VendingMachines;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 6 fidelity layer, proven through the WHOLE pipeline (not just the isolated
    /// live-grid mechanism in <see cref="ShipStateFidelityTest"/>): a stocked vending
    /// machine's <c>Inventory</c> — a <c>Dictionary&lt;string, VendingMachineInventoryEntry&gt;</c>
    /// the engine map serializer cannot write — is captured onto a
    /// <see cref="ShipCapturedStateComponent"/> at store, rides the blob, and is restored
    /// intact at retrieve. Zero content edits: the same reverted vending machine that broke
    /// 72 ships round-trips faithfully because the tool adapts to it.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageVendingFidelityTest
    {
        private const string VendingProto = "VendingMachineCigs";

        [Test]
        public async Task StoreThenRetrievePreservesVendingStock()
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

            var ownerId = Guid.NewGuid();

            EntityUid gridUid = default;
            EntityUid vendorUid = default;

            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out var mapId);
                var grid = mapManager.CreateGridEntity(mapId);
                gridUid = grid.Owner;

                mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
                entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid.Owner));

                // Retrieve treats a blob without ShuttleComponent as a load failure.
                entManager.EnsureComponent<ShuttleComponent>(grid.Owner);

                // Spawn at the tile centre so the vendor parents to the grid; it auto-runs
                // MapInit onto the already-initialized map, so RestockInventoryFromPrototype
                // fills its Inventory with the proto's stock — the modification to preserve.
                vendorUid = entManager.SpawnEntity(VendingProto, new EntityCoordinates(grid.Owner, new Vector2(0.5f, 0.5f)));
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // Snapshot the live stock before the store touches anything.
            var original = new Dictionary<string, VendingMachineInventoryEntry>();
            await server.WaitAssertion(() =>
            {
                var comp = entManager.GetComponent<VendingMachineComponent>(vendorUid);
                Assert.That(comp.Inventory, Is.Not.Empty,
                    "Precondition: the vendor should have stocked at map-init.");
                foreach (var (key, entry) in comp.Inventory)
                    original[key] = new VendingMachineInventoryEntry(entry.Type, entry.ID, entry.Amount);
            });

            EntityUid station = default;
            await server.WaitPost(() =>
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out _));

            // Store.
            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var storeResult = await storeTask;

            Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.Success),
                "TryStoreShip should succeed for a mindless grid carrying a stocked vendor.");
            Assert.That(storeResult.ShipId, Is.Not.Null, "TryStoreShip returned null: a gate refused the store.");

            server.RunTicks(1);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
                Assert.That(entManager.EntityExists(gridUid), Is.False,
                    "The live grid should be removed from the sim after a successful store."));

            // Retrieve.
            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(storeResult.ShipId!.Value, ownerId, station));
            var retrievedGrid = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(retrievedGrid, Is.Not.Null, "TryRetrieveShip returned no grid.");

                // The store-in-progress marker is stamped on the live grid BEFORE serialize (it
                // must block container insertion during the store window), so without
                // [UnsavedComponent] it rides the blob — and a retrieved ship that still carries
                // it has ALL container insertion blocked aboard, including picking items up into
                // your hands. Found in-game: nothing on a retrieved ship could be picked up.
                Assert.That(entManager.HasComponent<ShipStorageInProgressComponent>(retrievedGrid!.Value), Is.False,
                    "The store-in-progress marker must not survive the blob round-trip.");

                // Find the reborn vendor and assert its stock came back exactly.
                var found = false;
                var query = entManager.AllEntityQueryEnumerator<VendingMachineComponent, TransformComponent>();
                while (query.MoveNext(out var vendorEnt, out var vendor, out var xform))
                {
                    if (xform.GridUid != retrievedGrid!.Value)
                        continue;

                    found = true;
                    Assert.That(vendor.Inventory, Has.Count.EqualTo(original.Count),
                        "The reloaded vendor's stock count must match what was stored, not the empty serialized field.");

                    foreach (var (key, entry) in original)
                    {
                        Assert.That(vendor.Inventory, Contains.Key(key), $"missing stock entry {key}");
                        var r = vendor.Inventory[key];
                        Assert.Multiple(() =>
                        {
                            Assert.That(r.Type, Is.EqualTo(entry.Type), $"{key} Type");
                            Assert.That(r.ID, Is.EqualTo(entry.ID), $"{key} ID");
                            Assert.That(r.Amount, Is.EqualTo(entry.Amount), $"{key} Amount");
                        });
                    }

                    // The sidecar must have been consumed on restore, not left riding the entity.
                    Assert.That(entManager.HasComponent<ShipCapturedStateComponent>(vendorEnt), Is.False,
                        "The captured-state sidecar must be removed after restore.");
                }

                Assert.That(found, Is.True, "No vending machine was found on the retrieved grid.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
