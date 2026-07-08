// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Numerics;
using Content.Server._Hyperion.Worldgen.Components;
using Content.Server._Hyperion.Worldgen.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Hyperion.Worldgen
{
    /// <summary>
    ///     Verifies the worldgen-v1 trojan loot pockets (belt-economy Part 3). The pockets are the
    ///     inverse of the distance carver: fixed, learnable spots where debris is enriched. These
    ///     tests assert the roll honors its count, every pocket lands inside the configured radius
    ///     band (so pockets sit in the Wall, not a neighbouring ring), and pocket membership is a
    ///     clean disc around each center.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(TrojanSystem))]
    public sealed class TrojanTest
    {
        /// <summary>
        ///     Adds a field to a live entity so its startup roll fires, then returns the rolled component.
        /// </summary>
        private static TrojanFieldComponent RollField(IEntityManager entMan, TrojanFieldComponent template)
        {
            var ent = entMan.SpawnEntity(null, MapCoordinates.Nullspace);
            entMan.AddComponent(ent, template);
            return template;
        }

        [Test]
        public async Task Roll_ProducesConfiguredCount()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            await server.WaitAssertion(() =>
            {
                var def = RollField(server.EntMan, new TrojanFieldComponent());
                Assert.That(def.Rolled, Is.True);
                Assert.That(def.Pockets, Has.Count.EqualTo(3));

                var custom = RollField(server.EntMan, new TrojanFieldComponent { Count = 5 });
                Assert.That(custom.Pockets, Has.Count.EqualTo(5));
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task Pockets_LandWithinRadiusBand()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            await server.WaitAssertion(() =>
            {
                var field = RollField(server.EntMan, new TrojanFieldComponent());

                foreach (var pocket in field.Pockets)
                {
                    var r = pocket.Center.Length();
                    Assert.That(r, Is.InRange(field.RadiusRange.X, field.RadiusRange.Y),
                        "Pocket center rolled outside the configured radius band");
                }
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task InPocket_IsACleanDiscAroundEachCenter()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            await server.WaitAssertion(() =>
            {
                var field = RollField(server.EntMan, new TrojanFieldComponent { Count = 1, PocketRadius = 150f });
                var center = field.Pockets[0].Center;

                Assert.That(TrojanSystem.InPocket(field, center), Is.True, "center is in the pocket");
                Assert.That(TrojanSystem.InPocket(field, center + new Vector2(149f, 0f)), Is.True,
                    "just inside the pocket radius");
                Assert.That(TrojanSystem.InPocket(field, center + new Vector2(151f, 0f)), Is.False,
                    "just outside the pocket radius");
                Assert.That(TrojanSystem.InPocket(field, Vector2.Zero), Is.False,
                    "the sector center is not a pocket");
            });

            await pair.CleanReturnAsync();
        }
    }
}
