// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared.VendingMachines;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 6 fidelity layer, proven on a LIVE grid (no serialize pipeline yet): a stocked
    /// vending machine's inventory (which the engine serializer can't write) is CAPTURED into a
    /// sidecar and cleared at store, then RESTORED intact at retrieve — content-agnostic, zero
    /// content edits. This is the mechanism the reverted vending DataDefinition change is
    /// replaced by.
    /// </summary>
    [TestFixture]
    public sealed class ShipStateFidelityTest
    {
        private const string VendingProto = "VendingMachineCigs";

        [Test]
        public async Task VendingStock_CapturedAndRestored()
        {
            // No `await using`: on failure we want the real assertion to surface, not the
            // pair-disposal masking it. CleanReturnAsync runs only on success.
            var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var fidelity = entManager.System<ShipStateFidelitySystem>();

            EntityUid grid = default;
            EntityUid vendor = default;
            Dictionary<string, VendingMachineInventoryEntry> original = default!;

            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out var mapId);
                var g = mapManager.CreateGridEntity(mapId);
                grid = g.Owner;
                mapSystem.SetTile(grid, g.Comp, Vector2i.Zero, new Tile(1));
                entManager.RunMapInit(grid, entManager.GetComponent<MetaDataComponent>(grid));

                // Spawn the vendor onto the already-initialized map (tile centre so it parents
                // to the grid); it auto-runs MapInit, so RestockInventoryFromPrototype stocks
                // its Inventory. Do NOT RunMapInit it again — that double-inits and errors.
                vendor = entManager.SpawnEntity(VendingProto, new EntityCoordinates(grid, new Vector2(0.5f, 0.5f)));
            });

            server.RunTicks(1);
            await server.WaitIdleAsync();

            var stockedCount = 0;
            var clearedCount = -1;
            var hadSidecar = false;
            System.Exception? thrown = null;

            await server.WaitPost(() =>
            {
                try
                {
                    var comp = entManager.GetComponent<VendingMachineComponent>(vendor);
                    stockedCount = comp.Inventory.Count;
                    original = comp.Inventory.ToDictionary(
                        kv => kv.Key,
                        kv => new VendingMachineInventoryEntry(kv.Value.Type, kv.Value.ID, kv.Value.Amount));

                    fidelity.CaptureAndStrip(grid);
                    clearedCount = comp.Inventory.Count;
                    hadSidecar = entManager.HasComponent<ShipCapturedStateComponent>(vendor);

                    fidelity.RestoreCaptured(grid);
                }
                catch (System.Exception e)
                {
                    thrown = e;
                }
            });

            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(thrown, Is.Null, $"capture/restore threw: {thrown}");
                Assert.That(stockedCount, Is.GreaterThan(0), "Precondition: the vendor stocked at map-init.");
                Assert.That(clearedCount, Is.Zero, "Capture must clear the unserializable field for the serializer.");
                Assert.That(hadSidecar, Is.True, "The captured state must ride a sidecar on the entity.");

                var comp = entManager.GetComponent<VendingMachineComponent>(vendor);
                Assert.That(entManager.HasComponent<ShipCapturedStateComponent>(vendor), Is.False,
                    "The sidecar must be consumed on restore.");
                Assert.That(comp.Inventory, Has.Count.EqualTo(original.Count), "Stock count must round-trip.");
                foreach (var (key, entry) in original)
                {
                    Assert.That(comp.Inventory, Contains.Key(key));
                    var r = comp.Inventory[key];
                    Assert.Multiple(() =>
                    {
                        Assert.That(r.Type, Is.EqualTo(entry.Type), $"{key} Type");
                        Assert.That(r.ID, Is.EqualTo(entry.ID), $"{key} ID");
                        Assert.That(r.Amount, Is.EqualTo(entry.Amount), $"{key} Amount");
                    });
                }
            });

            await pair.CleanReturnAsync();
        }
    }
}
