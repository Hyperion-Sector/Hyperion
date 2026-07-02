// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared.CCVar;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 2d: pins the hull-damage sidecar invariant. <see cref="DamageableComponent.Damage"/>
    /// is a <c>[DataField(readOnly: true)]</c> (the "// TODO FULL GAME SAVE" marker), so entity
    /// damage does not round-trip through the map serializer: a damaged wall comes back pristine.
    /// Without a sidecar (mirroring the pipe-gas mechanism), store/retrieve is a free-repair exploit
    /// on a combat ship.
    ///
    /// This test damages a wall to a known total, stores + retrieves the ship, then asserts the
    /// reloaded wall carries the same damage total. It fails TODAY: the retrieved wall's total
    /// comes back 0 (readOnly damage was not serialized, and no sidecar restores it).
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageDamageSidecarTest
    {
        // WallSolid: StructuralInorganic damage container (supports the Brute group, so Blunt lands),
        // destruction threshold 2000. A 50-Blunt hit is well below destruction and leaves a stable,
        // damaged-but-intact wall to round-trip.
        private const string WallProto = "WallSolid";
        private const string DamageType = "Blunt";
        private const float DealtDamage = 50f;
        private const float Tolerance = 0.01f;

        [Test]
        public async Task WallDamageSurvivesRoundTrip()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoManager = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var damageSystem = entManager.System<DamageableSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var cfg = server.ResolveDependency<IConfigurationManager>();
            Assert.That(cfg.GetCVar(CCVars.GridFill), Is.False);

            // Stable synthetic owner: store and retrieve MUST use the same id.
            var ownerId = Guid.NewGuid();

            EntityUid gridUid = default;
            EntityUid wallUid = default;
            FixedPoint2 storedTotal = FixedPoint2.Zero;

            // Build a small grid with one floored tile and spawn a wall on it, then deal a known
            // damage amount. ignoreResistances so the wall's WallDefense1 flat-reduction doesn't
            // muddy the dealt total; the assertion still keys off the CAPTURED pre-store total, not
            // a hardcoded number, so it can't pass vacuously against a modifier surprise.
            await server.WaitPost(() =>
            {
                mapSystem.CreateMap(out var mapId);
                var grid = mapManager.CreateGridEntity(mapId);
                gridUid = grid.Owner;

                // Tile before spawn so the wall parents to the grid, not the map
                // (per the grid-children rule).
                mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));

                entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid.Owner));

                wallUid = entManager.SpawnEntity(WallProto, new EntityCoordinates(grid.Owner, Vector2.Zero));

                var bluntType = protoManager.Index<DamageTypePrototype>(DamageType);
                var spec = new DamageSpecifier(bluntType, FixedPoint2.New(DealtDamage));
                damageSystem.TryChangeDamage(wallUid, spec, ignoreResistances: true);

                var damageable = entManager.GetComponent<DamageableComponent>(wallUid);
                storedTotal = damageable.TotalDamage;

                Assert.That(storedTotal, Is.EqualTo(FixedPoint2.New(DealtDamage)),
                    "Pre-store wall damage did not land at the dealt amount.");
            });

            // Store the ship.
            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var storeResult = await storeTask;

            Assert.That(storeResult.Result, Is.EqualTo(ShipStorageResult.Success),
                "TryStoreShip should succeed for a mindless, hazard-free grid.");
            Assert.That(storeResult.ShipId, Is.Not.Null, "TryStoreShip returned null: a gate refused the store.");

            // QueueDel is deferred; let it settle.
            server.RunTicks(1);
            await server.WaitIdleAsync();

            // Retrieve and let the reloaded grid settle.
            EntityUid? retrievedGrid = null;
            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(storeResult.ShipId!.Value, ownerId));
            retrievedGrid = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            // The free-repair pin. Fails TODAY: DamageableComponent.Damage is readOnly, so the
            // map serializer never wrote it and the reloaded wall comes back at 0 damage.
            await server.WaitAssertion(() =>
            {
                Assert.That(retrievedGrid, Is.Not.Null, "TryRetrieveShip returned no grid.");

                var found = false;
                var query = entManager.AllEntityQueryEnumerator<DamageableComponent, TransformComponent>();
                while (query.MoveNext(out _, out var damageable, out var xform))
                {
                    if (xform.GridUid != retrievedGrid!.Value)
                        continue;

                    found = true;
                    Assert.That(damageable.TotalDamage.Float(), Is.EqualTo(storedTotal.Float()).Within(Tolerance),
                        "The reloaded wall's damage total must equal the stored amount (no free repair on store/retrieve).");
                }

                Assert.That(found, Is.True, "No damageable wall was found on the retrieved grid.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
