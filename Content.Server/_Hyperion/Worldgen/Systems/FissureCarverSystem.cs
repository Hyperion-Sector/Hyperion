// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Numerics;
using Content.Server._Hyperion.Worldgen.Components;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Systems.Debris;
using Robust.Shared.Random;

namespace Content.Server._Hyperion.Worldgen.Systems;

/// <summary>
///     Carves guaranteed inner-to-outer crossings ("fissures") through the belt so the Wall
///     is passable (design repo worldgen-v1 Part 1). Unlike the ambient <c>NoiseRangeCarver</c>,
///     which is a stateless noise threshold with no path awareness, a fissure is a constructed
///     radial channel: per round we roll N bearings, and at radius r the lane centers on a
///     drifting bearing with a nonempty angular width. Because that width is strictly positive
///     at every radius and the bearing varies continuously, the carved region is a connected
///     path from the inner belt to the outer belt by construction.
///
///     Carvers union (every subscriber may reject a point), so these crossings compose with the
///     ambient worms into side-galleries and false paths for free.
/// </summary>
public sealed partial class FissureCarverSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IRobustRandom _random = default!;

    // Spatial angular frequencies of the drift/chamber sinusoids, in radians of phase per world
    // unit of radius. Two decorrelated drift octaves read as organic winding; the per-fissure
    // phases below keep separate fissures from wandering in lockstep.
    private const float DriftFreqA = MathF.Tau / 9000f;
    private const float DriftFreqB = MathF.Tau / 3500f;
    private const float ChamberFreq = MathF.Tau / 4000f;

    /// <inheritdoc />
    public override void Initialize()
    {
        SubscribeLocalEvent<FissureFieldComponent, ComponentStartup>(OnFieldStartup);
        SubscribeLocalEvent<FissureCarverComponent, PrePlaceDebrisFeatureEvent>(OnPrePlaceDebris);
    }

    /// <summary>
    ///     Rolls the round's crossings once, when the worldgen config drops the field onto the map.
    /// </summary>
    private void OnFieldStartup(EntityUid uid, FissureFieldComponent component, ComponentStartup args)
    {
        if (component.Rolled)
            return;

        var total = 0;
        foreach (var cls in component.Classes)
            total += Math.Max(0, cls.Count);

        component.Fissures.Clear();

        if (total > 0)
        {
            // Stratify bearings into `total` even sectors, then shuffle which fissure lands in which
            // sector so the wide and narrow classes don't clump on one side of the belt.
            var slots = new List<int>(total);
            for (var i = 0; i < total; i++)
                slots.Add(i);
            _random.Shuffle(slots);

            var next = 0;
            foreach (var cls in component.Classes)
            {
                for (var i = 0; i < cls.Count; i++)
                {
                    var slot = slots[next++];
                    var bearing = (slot + _random.NextFloat()) / total * MathF.Tau;

                    component.Fissures.Add(new Fissure
                    {
                        Bearing = bearing,
                        HalfWidthTiles = cls.HalfWidthTiles,
                        OverhangPadTiles = cls.OverhangPadTiles,
                        DriftAmplitude = cls.DriftAmplitude,
                        ChamberAmplitudeTiles = cls.ChamberAmplitudeTiles,
                        DriftPhaseA = _random.NextFloat() * MathF.Tau,
                        DriftPhaseB = _random.NextFloat() * MathF.Tau,
                        ChamberPhase = _random.NextFloat() * MathF.Tau,
                    });
                }
            }
        }

        component.Rolled = true;
    }

    private void OnPrePlaceDebris(EntityUid uid, FissureCarverComponent component,
        ref PrePlaceDebrisFeatureEvent args)
    {
        // Something already carved this point; nothing to do (carvers union).
        if (args.Handled)
            return;

        // The rolled crossings live on the map, not the chunk. Chunks sit in nullspace, so the
        // map comes off the chunk component, not the transform.
        if (!TryComp<WorldChunkComponent>(uid, out var chunk))
            return;

        if (!TryComp<FissureFieldComponent>(chunk.Map, out var field) || !field.Rolled)
            return;

        // Sector center is the map origin, so the point's polar coords are just its world position.
        if (IsCarved(field, _transform.ToMapCoordinates(args.Coords).Position))
            args.Handled = true;
    }

    /// <summary>
    ///     Whether a world position (relative to the sector center at the map origin) falls inside
    ///     any rolled fissure. Pure geometry, split out so the connectivity guarantee is testable
    ///     without standing up the worldgen chunk/map plumbing.
    /// </summary>
    public static bool IsCarved(FissureFieldComponent field, Vector2 pos)
    {
        var r = pos.Length();
        if (r < field.RadiusRange.X || r > field.RadiusRange.Y)
            return false;

        var ang = MathF.Atan2(pos.Y, pos.X);

        foreach (var fissure in field.Fissures)
        {
            var theta = fissure.Bearing + fissure.DriftAmplitude
                * (0.65f * MathF.Sin(r * DriftFreqA + fissure.DriftPhaseA)
                    + 0.35f * MathF.Sin(r * DriftFreqB + fissure.DriftPhaseB));

            var halfWidthTiles = fissure.HalfWidthTiles + fissure.OverhangPadTiles;
            if (fissure.ChamberAmplitudeTiles > 0f)
                halfWidthTiles += fissure.ChamberAmplitudeTiles
                    * 0.5f * (1f + MathF.Sin(r * ChamberFreq + fissure.ChamberPhase));

            // Constant world-space width -> shrinking angular width as the lane climbs.
            var halfWidthAng = halfWidthTiles / MathF.Max(r, 1f);

            // Shortest signed angular distance, wrapped to [-pi, pi].
            var d = ang - theta;
            d = MathF.Atan2(MathF.Sin(d), MathF.Cos(d));

            if (MathF.Abs(d) <= halfWidthAng)
                return true;
        }

        return false;
    }
}
