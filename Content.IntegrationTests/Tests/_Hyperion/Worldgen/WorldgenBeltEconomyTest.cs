// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Numerics;
using Content.Server.Mining;
using Content.Shared.Destructible;
using Content.Shared.Mining;
using Content.Shared.Mining.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.Worldgen
{
    /// <summary>
    ///     Verifies the worldgen-v1 iron backbone (belt-economy Part 5b). The NF
    ///     mineral tables shipped no iron - the belt's bulk was bare rock that drops
    ///     nothing - so Hyperion added iron ore walls seeded with steel. Boot only
    ///     proves the prototypes resolve; this proves the belt actually yields iron:
    ///     each iron wall is seeded with steel ore and drops it when mined.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(MiningSystem))]
    public sealed class WorldgenBeltEconomyTest
    {
        private static readonly ProtoId<OrePrototype> SteelOre = "NFOreSteel";

        [Test]
        [TestCase("WallRockIron")]
        [TestCase("WallRockBasaltIron")]
        public async Task IronWall_DropsSteelOre(string wallProto)
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            var entMan = server.EntMan;
            var mapMan = server.MapMan;
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entMan.System<SharedMapSystem>();

            // The ore entity an iron vein should drop, resolved from the prototype
            // rather than hardcoded, so a rename of the ore doesn't silently pass.
            var oreProto = protoMan.Index(SteelOre);
            Assert.That(oreProto.OreEntity, Is.Not.Null, "NFOreSteel defines no ore entity");
            var expectedOre = oreProto.OreEntity!.Value.Id;

            MapId mapId = default;

            await server.WaitAssertion(() =>
            {
                mapId = mapMan.CreateMap();
                var grid = mapMan.CreateGridEntity(mapId);
                mapSystem.SetTile(grid, Vector2i.Zero, new Tile(1));

                var wall = entMan.SpawnEntity(wallProto, new EntityCoordinates(grid.Owner, Vector2.Zero));

                // The whole backbone hinges on this vein being seeded with steel.
                var vein = entMan.GetComponent<OreVeinComponent>(wall);
                Assert.That(vein.CurrentOre?.Id, Is.EqualTo(SteelOre.Id),
                    $"{wallProto} is not seeded with steel ore");

                // Destruction is what spawns the ore (MiningSystem.OnDestruction).
                entMan.EventBus.RaiseLocalEvent(wall, new DestructionEventArgs());
            });

            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                var dropped = 0;
                var query = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
                while (query.MoveNext(out _, out var meta, out var xform))
                {
                    if (xform.MapID == mapId && meta.EntityPrototype?.ID == expectedOre)
                        dropped++;
                }

                Assert.That(dropped, Is.GreaterThan(0),
                    $"{wallProto} dropped no steel ore ({expectedOre}) when mined");
            });

            await pair.CleanReturnAsync();
        }
    }
}
