// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared._NF.Shipyard.Prototypes;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// FIDELITY SWEEP (the test the mechanism tests should have been paired with): load
    /// every real vessel grid from the roster MAP-INITIALIZED (so vending machines stock,
    /// atmos fills, etc. — the live state the synthetic one-tile grids never had) and run
    /// it through the real store pipeline, cataloging the outcome per ship. SerializeFailed
    /// / ValidationFailed / an uncaught exception = a real fidelity gap on real content;
    /// Success / OrganicsAboard / HazardAboard are acceptable outcomes. Prints the full
    /// breakdown, then hard-gates on no serialize failure / no exception, with a generous
    /// ceiling over the known nondeterministic validation tail (random MapInit spawns the
    /// inert scratch reload can't reproduce). Station-AI vessels store like any other now —
    /// their off-grid eye/brain apparatus is emptied at store (ShipStorageSystem.StationAi.cs),
    /// so no ship is excluded. This is the fidelity gate over the whole real roster; it started
    /// as the red catalog that drove the fidelity-layer work.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageRosterSweepTest
    {
        [Test]
        public async Task StoreEveryVessel_MapInitialized_Catalog()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapLoader = entManager.System<MapLoaderSystem>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var vessels = protoMan.EnumeratePrototypes<VesselPrototype>()
                .Where(v => !v.Abstract && v.ShuttlePath != default)
                .OrderBy(v => v.ID)
                .ToList();

            TestContext.Out.WriteLine($"[roster-sweep] {vessels.Count} vessels to store.");

            // category -> ship ids
            var catalog = new Dictionary<string, List<string>>();
            void Record(string category, string id)
            {
                if (!catalog.TryGetValue(category, out var list))
                    catalog[category] = list = new List<string>();
                list.Add(id);
            }

            var ownerId = Guid.NewGuid();

            foreach (var vessel in vessels)
            {
                Entity<MapComponent>? map = null;
                Entity<MapGridComponent>? grid = null;
                var loaded = false;

                try
                {
                    await server.WaitPost(() =>
                    {
                        var opts = new DeserializationOptions { InitializeMaps = true };
                        loaded = mapLoader.TryLoadGrid(vessel.ShuttlePath, out map, out grid, opts);
                    });
                }
                catch (Exception e)
                {
                    Record($"LOAD_EXCEPTION:{e.GetType().Name}", vessel.ID);
                    continue;
                }

                if (!loaded || grid is not { } g)
                {
                    Record("LOAD_FAILED", vessel.ID);
                    continue;
                }

                await server.WaitIdleAsync();

                try
                {
                    Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
                    await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(g.Owner, ownerId));
                    var (result, _) = await storeTask;
                    Record(result.ToString(), vessel.ID);
                }
                catch (Exception e)
                {
                    Record($"STORE_EXCEPTION:{e.GetType().Name}", vessel.ID);
                }

                // Tear down whatever survived (a successful store despawned the grid but
                // left the map; a refusal left both) and clear the round-scoped registry
                // so ships don't interfere across iterations.
                await server.WaitPost(() =>
                {
                    shipStorage.ClearActiveShipRegistry();
                    if (map is { } m && !entManager.Deleted(m.Owner))
                        entManager.DeleteEntity(m.Owner);
                });
                await server.WaitIdleAsync();
            }

            // Print the catalog, most-populous category first.
            TestContext.Out.WriteLine("[roster-sweep] ===== RESULTS =====");
            foreach (var (category, ids) in catalog.OrderByDescending(kv => kv.Value.Count))
            {
                TestContext.Out.WriteLine($"[roster-sweep] {category}: {ids.Count}");
                TestContext.Out.WriteLine($"[roster-sweep]     {string.Join(", ", ids)}");
            }

            var serializeFailed = catalog.GetValueOrDefault("SerializeFailed") ?? new();
            var validationFailed = catalog.GetValueOrDefault("ValidationFailed") ?? new();
            var exceptions = catalog.Where(kv => kv.Key.Contains("EXCEPTION")).SelectMany(kv => kv.Value).ToList();
            var success = catalog.GetValueOrDefault("Success")?.Count ?? 0;

            TestContext.Out.WriteLine(
                $"[roster-sweep] SUMMARY: {success} ok / {serializeFailed.Count} serialize-fail / " +
                $"{validationFailed.Count} validation-fail / {exceptions.Count} exception / {vessels.Count} total.");

            // Hard gate: NOTHING may fail to serialize or throw. The vending-machine
            // DataDefinition fix recovered 72 ships here; this keeps that class from
            // ever regressing. A new unserializable type on real content trips this.
            Assert.That(serializeFailed, Is.Empty,
                "Real vessels must serialize. See the [roster-sweep] catalog + server log for the unserializable type.");
            Assert.That(exceptions, Is.Empty,
                "Storing a real vessel threw. See the [roster-sweep] catalog for the ship + exception type.");

            // Tracked residual (entity-count drop on round-trip reload — a separate,
            // subtler fidelity gap). The FAILING SET IS NONDETERMINISTIC across runs
            // (observed ~5, membership varies: e.g. Arkansaw/Flyssa/Medicus/Ravager/Saturn
            // one run, a different mix the next), which points at MapInit-time RANDOM
            // spawns whose live count the inert scratch reload can't reproduce — under
            // investigation, not yet fixed. So gate on a generous CEILING (catches a mass
            // fidelity regression from a bad fix) rather than an exact allow-list (which
            // the nondeterminism makes flaky). The set is logged above for tracking.
            const int validationResidualCeiling = 12;
            Assert.That(validationFailed.Count, Is.LessThanOrEqualTo(validationResidualCeiling),
                $"Round-trip validation failures ({validationFailed.Count}) exceeded the tracked residual " +
                $"ceiling ({validationResidualCeiling}) — a fidelity regression, not the known nondeterministic tail. " +
                $"Failing ships: {string.Join(", ", validationFailed)}");

            await pair.CleanReturnAsync();
        }
    }
}
