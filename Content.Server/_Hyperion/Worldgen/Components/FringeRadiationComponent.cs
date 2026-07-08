// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

namespace Content.Server._Hyperion.Worldgen.Components;

/// <summary>
///     Deep-space radiation taper for the outer belt (design repo worldgen-v1 Part 1c).
///     Lives on the worldgen map (added via the worldgenConfig bundle). Unlike the engine's
///     source-driven radiation, this applies a <em>sourceless</em> per-second dose to every
///     <c>RadiationReceiver</c> whose distance from the sector center (the map origin) falls in
///     the taper band: zero below <see cref="StartRadius"/>, ramping linearly to
///     <see cref="MaxRadsPerSecond"/> at <see cref="FullRadius"/>, and clamped beyond.
///
///     Because there is no source, ship-hull tile shielding does not apply: the engine only
///     evaluates hull resistance along a source-to-receiver ray, and there is no ray here.
///     Personal rad resistance still does, since the dose lands as ordinary Radiation damage and
///     worn-gear modifiers cut it downstream. This is the turn-back tax on the empty drift, not a
///     shielding puzzle; the reward-gate core over the Wall can get real sources later if we want
///     hull to matter there.
/// </summary>
[RegisterComponent]
public sealed partial class FringeRadiationComponent : Component
{
    /// <summary>
    ///     Inner edge of the taper. No dose is applied inside this radius.
    /// </summary>
    [DataField]
    public float StartRadius = 37000f;

    /// <summary>
    ///     Radius at which the dose reaches <see cref="MaxRadsPerSecond"/>. Between here and
    ///     <see cref="StartRadius"/> the dose ramps linearly; past it the dose is clamped to max.
    ///     Defaults to the belt's 40k terminal edge, so the ramp fills the drift and the clamp
    ///     covers the void beyond (where nothing spawns, but a ship can still push).
    /// </summary>
    [DataField]
    public float FullRadius = 40000f;

    /// <summary>
    ///     Peak dose in rads per second, applied at and past <see cref="FullRadius"/>. Kept mild:
    ///     a chip that punishes a long unshielded push, not an instant kill. Worn rad gear cuts it
    ///     (hardsuits resist Radiation ~30-75%). Interim tuning knob.
    /// </summary>
    [DataField]
    public float MaxRadsPerSecond = 2f;
}
