// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.Atmos;
using Content.Shared.CCVar;
using Content.Shared.NodeContainer;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 2c: pins the pipe-net gas sidecar invariant. Pipe-network gas lives on the
    /// PipeNet node group (the <see cref="Content.Server.NodeContainer.NodeGroups.PipeNet"/>
    /// Air GasMixture), which the map serializer cannot reach, so a naive store/retrieve
    /// loses it. A sidecar component per pipe entity must carry the gas across the round-trip
    /// and re-sum it into the rebuilt net.
    ///
    /// ASSERT A (sidecar contract): total moles per gas across the retrieved grid's pipe nets
    /// equals the stored total within tolerance.
    ///
    /// ASSERT B (consume-once): deleting a pipe on the retrieved grid forces a NodeGroupsRebuilt
    /// on the survivors; the sidecar must be consumed on apply so the stored gas is NOT re-added
    /// (a duplication exploit). Grid net total must not increase after the cut.
    /// </summary>
    [TestFixture]
    public sealed class ShipStoragePipeGasSidecarTest
    {
        // GasPipeStraight is Longitudinal (North|South); a vertical column of them on adjacent
        // tiles links into ONE PipeNet. Four pipes gives a real multi-entity net to distribute
        // gas across (the sidecar must split by volume and re-sum).
        private const string PipeProto = "GasPipeStraight";
        private const int PipeCount = 4;

        // A known, distinctive charge so a vacuous (~zero) round-trip can't sneak past.
        private const float OxygenMoles = 50f;
        private const float NitrogenMoles = 30f;
        private const float Tolerance = 0.5f;

        [Test]
        public async Task PipeNetGasSurvivesRoundTripAndDoesNotDupeOnRebuild()
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

            // Build a vertical pipe run, anchored on floored tiles, then pump so the PipeNet forms.
            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out var mapId);
                var grid = mapManager.CreateGridEntity(mapId);
                gridUid = grid.Owner;

                // Tiles before anchored spawns so the pipes parent to the grid, not the map.
                for (var y = 0; y < PipeCount; y++)
                    mapSystem.SetTile(grid.Owner, grid.Comp, new Vector2i(0, y), new Tile(1));

                entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid.Owner));

                for (var y = 0; y < PipeCount; y++)
                    entManager.SpawnEntity(PipeProto, new EntityCoordinates(grid.Owner, new Vector2(0, y)));
            });

            // Let the node graph flood and the PipeNet coalesce.
            server.RunTicks(5);
            await server.WaitIdleAsync();

            // Inject a known gas charge into the formed net through the PipeNet's Air mixture,
            // then record the per-gas totals across the grid's nets.
            float storedOxygen = 0f;
            float storedNitrogen = 0f;
            await server.WaitPost(() =>
            {
                // Reach the net via any member pipe: NodeContainer -> "pipe" PipeNode -> PipeNet.Air.
                var air = GetFirstPipeNetAir(entManager, gridUid);
                Assert.That(air, Is.Not.Null, "No PipeNet formed on the grid; the pipe run did not link.");

                air!.SetMoles(Gas.Oxygen, OxygenMoles);
                air.SetMoles(Gas.Nitrogen, NitrogenMoles);

                (storedOxygen, storedNitrogen) = SumGridPipeGas(entManager, gridUid);

                Assert.That(storedOxygen, Is.EqualTo(OxygenMoles).Within(Tolerance),
                    "Pre-store oxygen charge did not stick on the net.");
                Assert.That(storedNitrogen, Is.EqualTo(NitrogenMoles).Within(Tolerance),
                    "Pre-store nitrogen charge did not stick on the net.");
            });

            // Store the ship; the blob is filed off the map serializer, which cannot see PipeNet gas.
            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var storeResult = await storeTask;

            Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.Success),
                "TryStoreShip should succeed for a mindless, hazard-free pipe grid.");
            Assert.That(storeResult.ShipId, Is.Not.Null, "TryStoreShip returned null: a gate refused the store.");

            server.RunTicks(1);
            await server.WaitIdleAsync();

            // Retrieve and let the nets rebuild on the reloaded grid.
            EntityUid? retrievedGrid = null;
            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(storeResult.ShipId!.Value, ownerId));
            retrievedGrid = await retrieveTask;

            server.RunTicks(5);
            await server.WaitIdleAsync();

            // ASSERT A -- the sidecar contract. Fails TODAY: the net comes back with ~zero gas
            // because PipeNet.Air is not serialized and there is no sidecar to restore it.
            await server.WaitAssertion(() =>
            {
                Assert.That(retrievedGrid, Is.Not.Null, "TryRetrieveShip returned no grid.");

                var (oxygen, nitrogen) = SumGridPipeGas(entManager, retrievedGrid!.Value);

                Assert.That(oxygen, Is.EqualTo(storedOxygen).Within(Tolerance),
                    "Retrieved pipe-net oxygen must equal the stored total (the sidecar must restore it).");
                Assert.That(nitrogen, Is.EqualTo(storedNitrogen).Within(Tolerance),
                    "Retrieved pipe-net nitrogen must equal the stored total (the sidecar must restore it).");
            });

            // ASSERT B -- consume-once. Delete one pipe to force a NodeGroupsRebuilt on the
            // survivors; the sidecar must already be consumed, so the stored gas is not re-applied.
            float beforeCutTotal = 0f;
            await server.WaitPost(() =>
            {
                var (ox, ni) = SumGridPipeGas(entManager, retrievedGrid!.Value);
                beforeCutTotal = ox + ni;

                // Delete a single pipe entity on the retrieved grid.
                var victim = GetFirstPipeEntity(entManager, retrievedGrid!.Value);
                Assert.That(victim, Is.Not.EqualTo(EntityUid.Invalid), "No pipe entity found on the retrieved grid to cut.");
                entManager.QueueDeleteEntity(victim);
            });

            server.RunTicks(5);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                var (ox, ni) = SumGridPipeGas(entManager, retrievedGrid!.Value);
                var afterCutTotal = ox + ni;

                Assert.That(afterCutTotal, Is.LessThanOrEqualTo(beforeCutTotal + Tolerance),
                    "Cutting a pipe re-applied the stored gas: the sidecar must be consume-once (duplication exploit).");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Returns the Air GasMixture of the first PipeNet reachable from a pipe entity on the grid,
        /// via NodeContainer -> "pipe" PipeNode -> PipeNet. This is the exact access path GREEN reuses.
        /// </summary>
        private static GasMixture? GetFirstPipeNetAir(IEntityManager entManager, EntityUid gridUid)
        {
            var query = entManager.AllEntityQueryEnumerator<NodeContainerComponent, TransformComponent>();
            while (query.MoveNext(out _, out var nodeContainer, out var xform))
            {
                if (xform.GridUid != gridUid)
                    continue;

                foreach (var node in nodeContainer.Nodes.Values)
                {
                    if (node is PipeNode pipe && pipe.NodeGroup != null)
                        return pipe.Air;
                }
            }

            return null;
        }

        /// <summary>
        /// Sums moles per gas (oxygen, nitrogen) across every distinct PipeNet on the grid.
        /// Distinct nets are de-duplicated by node-group identity so a shared net isn't counted twice.
        /// </summary>
        private static (float Oxygen, float Nitrogen) SumGridPipeGas(IEntityManager entManager, EntityUid gridUid)
        {
            var seen = new HashSet<object>();
            var oxygen = 0f;
            var nitrogen = 0f;

            var query = entManager.AllEntityQueryEnumerator<NodeContainerComponent, TransformComponent>();
            while (query.MoveNext(out _, out var nodeContainer, out var xform))
            {
                if (xform.GridUid != gridUid)
                    continue;

                foreach (var node in nodeContainer.Nodes.Values)
                {
                    if (node is not PipeNode pipe || pipe.NodeGroup == null)
                        continue;

                    if (!seen.Add(pipe.NodeGroup))
                        continue;

                    oxygen += pipe.Air.GetMoles(Gas.Oxygen);
                    nitrogen += pipe.Air.GetMoles(Gas.Nitrogen);
                }
            }

            return (oxygen, nitrogen);
        }

        /// <summary>
        /// Returns the first pipe-bearing entity on the grid, or <see cref="EntityUid.Invalid"/>.
        /// </summary>
        private static EntityUid GetFirstPipeEntity(IEntityManager entManager, EntityUid gridUid)
        {
            var query = entManager.AllEntityQueryEnumerator<NodeContainerComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var nodeContainer, out var xform))
            {
                if (xform.GridUid != gridUid)
                    continue;

                foreach (var node in nodeContainer.Nodes.Values)
                {
                    if (node is PipeNode)
                        return uid;
                }
            }

            return EntityUid.Invalid;
        }
    }
}
