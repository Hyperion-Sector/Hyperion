// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Content.Server._Hyperion.Worldgen.Components;
using Content.Server.Radiation.Components;
using Content.Server.Radiation.Systems;

namespace Content.Server._Hyperion.Worldgen.Systems;

/// <summary>
///     Applies the outer-belt radiation taper (see <see cref="FringeRadiationComponent"/>). Once a
///     second, sweeps every radiation receiver on a fringe-bearing map and doses it by distance
///     from the sector center. O(receivers)/sec: no sources, no raycasts. The dose is handed to the
///     engine's <see cref="RadiationSystem.IrradiateEntity"/> so it flows through the same
///     OnIrradiatedEvent -> Radiation-damage path (and Geiger counters) as any other radiation.
/// </summary>
public sealed class FringeRadiationSystem : EntitySystem
{
    [Dependency] private RadiationSystem _radiation = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    // The design's "O(players)/sec" cadence. The dose is per-second, so the elapsed window since
    // the last sweep is also the exposure time for one application.
    private const float UpdateInterval = 1f;

    private float _accumulator;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < UpdateInterval)
            return;

        var interval = _accumulator;
        _accumulator = 0f;

        var query = EntityQueryEnumerator<RadiationReceiverComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapUid is not { } map || !TryComp<FringeRadiationComponent>(map, out var fringe))
                continue;

            // Sector center is the map origin, so radius is just the receiver's world distance.
            var dose = DoseAt(fringe, _transform.GetWorldPosition(xform).Length());
            if (dose > 0f)
                _radiation.IrradiateEntity(uid, dose, interval);
        }
    }

    /// <summary>
    ///     Dose (rads/second) at a distance from the sector center. Pure geometry, split out so the
    ///     ramp is testable without standing up the map/receiver plumbing.
    /// </summary>
    public static float DoseAt(FringeRadiationComponent fringe, float r)
    {
        if (r < fringe.StartRadius)
            return 0f;

        // Degenerate band (Full <= Start): a hard step to max at the edge, not a ramp.
        if (fringe.FullRadius <= fringe.StartRadius)
            return fringe.MaxRadsPerSecond;

        var t = Math.Clamp((r - fringe.StartRadius) / (fringe.FullRadius - fringe.StartRadius), 0f, 1f);
        return fringe.MaxRadsPerSecond * t;
    }
}
