// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Content.Server._Hyperion.Worldgen.Components;
using Content.Server._Hyperion.Worldgen.Systems;

namespace Content.IntegrationTests.Tests._Hyperion.Worldgen
{
    /// <summary>
    ///     Verifies the outer-belt radiation taper (worldgen-v1 Part 1c). The dose must be zero
    ///     inside the taper's inner edge, ramp monotonically across the band, and clamp at its peak
    ///     past the outer edge, so the deep drift costs a push without being an instant wall.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(FringeRadiationSystem))]
    public sealed class FringeRadiationTest
    {
        [Test]
        public async Task DoseAt_RampsWithRadius()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            await server.WaitAssertion(() =>
            {
                var fringe = new FringeRadiationComponent
                {
                    StartRadius = 37000f,
                    FullRadius = 40000f,
                    MaxRadsPerSecond = 2f,
                };

                // Nothing inside the inner edge.
                Assert.That(FringeRadiationSystem.DoseAt(fringe, 0f), Is.EqualTo(0f));
                Assert.That(FringeRadiationSystem.DoseAt(fringe, 36999f), Is.EqualTo(0f));

                // Off at the edge, half at the midpoint (linear ramp).
                Assert.That(FringeRadiationSystem.DoseAt(fringe, 37000f), Is.EqualTo(0f).Within(0.001f));
                Assert.That(FringeRadiationSystem.DoseAt(fringe, 38500f), Is.EqualTo(1f).Within(0.001f));

                // Clamps at the peak at and past the outer edge.
                Assert.That(FringeRadiationSystem.DoseAt(fringe, 40000f), Is.EqualTo(2f).Within(0.001f));
                Assert.That(FringeRadiationSystem.DoseAt(fringe, 50000f), Is.EqualTo(2f).Within(0.001f));

                // Monotonic across the band.
                var prev = -1f;
                for (var r = 37000f; r <= 40000f; r += 100f)
                {
                    var dose = FringeRadiationSystem.DoseAt(fringe, r);
                    Assert.That(dose, Is.GreaterThanOrEqualTo(prev), $"Dose dropped at radius {r}");
                    prev = dose;
                }
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task DegenerateBand_AppliesFlatDose()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            await server.WaitAssertion(() =>
            {
                // Full <= Start: a hard step, not a ramp. Off below the edge, max at and past it.
                var fringe = new FringeRadiationComponent
                {
                    StartRadius = 39000f,
                    FullRadius = 39000f,
                    MaxRadsPerSecond = 3f,
                };

                Assert.That(FringeRadiationSystem.DoseAt(fringe, 38999f), Is.EqualTo(0f));
                Assert.That(FringeRadiationSystem.DoseAt(fringe, 39000f), Is.EqualTo(3f));
                Assert.That(FringeRadiationSystem.DoseAt(fringe, 41000f), Is.EqualTo(3f));
            });

            await pair.CleanReturnAsync();
        }
    }
}
