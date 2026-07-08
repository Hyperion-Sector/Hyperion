// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Generic;
using System.Numerics;
using Content.Server._Hyperion.Worldgen.Components;
using Content.Server._Hyperion.Worldgen.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._Hyperion.Worldgen
{
    /// <summary>
    ///     Verifies the worldgen-v1 fissure carver (belt-economy Part 1). The whole point of a
    ///     constructive carver over the ambient noise carver is that it <em>guarantees</em> an
    ///     inner-to-outer crossing. These tests assert that guarantee as a geometric property of
    ///     any rolled field: a lane exists at every radius, each lane is continuous (never teleports
    ///     between radii), and the belt is not wholesale erased.
    /// </summary>
    [TestFixture]
    [TestOf(typeof(FissureCarverSystem))]
    public sealed class FissureCarverTest
    {
        private static Vector2 Polar(float r, float theta)
            => new(r * MathF.Cos(theta), r * MathF.Sin(theta));

        /// <summary>
        ///     Adds a field to a live entity so its startup roll fires, then returns the rolled component.
        /// </summary>
        private static FissureFieldComponent RollField(IEntityManager entMan, FissureFieldComponent template)
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
                // Default roster is 2 capital + 4 small = 6.
                var def = RollField(server.EntMan, new FissureFieldComponent());
                Assert.That(def.Rolled, Is.True);
                Assert.That(def.Fissures, Has.Count.EqualTo(6));

                // Custom roster is respected.
                var custom = RollField(server.EntMan, new FissureFieldComponent
                {
                    Classes = new List<FissureClass>
                    {
                        new() { Count = 3, HalfWidthTiles = 20f },
                        new() { Count = 2, HalfWidthTiles = 10f },
                    },
                });
                Assert.That(custom.Fissures, Has.Count.EqualTo(5));
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task Field_HasALaneAtEveryRadius()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            await server.WaitAssertion(() =>
            {
                var field = RollField(server.EntMan, new FissureFieldComponent());
                var lo = field.RadiusRange.X;
                var hi = field.RadiusRange.Y;

                for (var r = lo + 1f; r <= hi - 1f; r += 250f)
                {
                    var found = false;
                    for (var a = 0f; a < MathF.Tau; a += 0.005f)
                    {
                        if (FissureCarverSystem.IsCarved(field, Polar(r, a)))
                        {
                            found = true;
                            break;
                        }
                    }

                    Assert.That(found, Is.True, $"No fissure lane exists at radius {r}");
                }
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task SingleFissure_IsContinuousAcrossRadius()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            await server.WaitAssertion(() =>
            {
                // One fissure with generous drift: this is the hard case for continuity.
                var field = RollField(server.EntMan, new FissureFieldComponent
                {
                    Classes = new List<FissureClass>
                    {
                        new() { Count = 1, HalfWidthTiles = 12f, OverhangPadTiles = 48f, DriftAmplitude = 0.30f },
                    },
                });

                var lo = field.RadiusRange.X;
                var hi = field.RadiusRange.Y;
                const float window = 0.15f; // generous vs. the max per-step drift (~0.033 rad at step 100)

                // Seed the tracked lane center at the inner edge.
                float? center = FindCarvedNear(field, lo + 1f, 0f, MathF.PI);
                Assert.That(center, Is.Not.Null, "No lane at the inner edge of the band");

                for (var r = lo + 101f; r <= hi - 1f; r += 100f)
                {
                    var next = FindCarvedNear(field, r, center!.Value, window);
                    Assert.That(next, Is.Not.Null,
                        $"Lane vanished or jumped >{window} rad at radius {r} (was continuous below)");
                    center = next;
                }
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task Field_DoesNotEraseTheBelt()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;

            await server.WaitAssertion(() =>
            {
                var field = RollField(server.EntMan, new FissureFieldComponent());
                var lo = field.RadiusRange.X;
                var hi = field.RadiusRange.Y;

                var carved = 0;
                var total = 0;
                for (var r = lo + 1f; r <= hi - 1f; r += 500f)
                {
                    for (var a = 0f; a < MathF.Tau; a += 0.02f)
                    {
                        total++;
                        if (FissureCarverSystem.IsCarved(field, Polar(r, a)))
                            carved++;
                    }
                }

                var fraction = (float) carved / total;
                Assert.That(fraction, Is.GreaterThan(0f), "Field carved nothing");
                Assert.That(fraction, Is.LessThan(0.25f),
                    $"Fissures erased {fraction:P0} of the belt; crossings should stay scarce");
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        ///     Scans a window of angles around <paramref name="near"/> at radius r and returns the
        ///     midpoint of the carved sub-arc, or null if none is carved in the window.
        /// </summary>
        private static float? FindCarvedNear(FissureFieldComponent field, float r, float near, float window)
        {
            float? min = null;
            float? max = null;
            for (var d = -window; d <= window; d += 0.001f)
            {
                if (!FissureCarverSystem.IsCarved(field, Polar(r, near + d)))
                    continue;

                min ??= near + d;
                max = near + d;
            }

            if (min is null || max is null)
                return null;

            return (min.Value + max.Value) / 2f;
        }
    }
}
