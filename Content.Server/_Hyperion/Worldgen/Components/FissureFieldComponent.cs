// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Numerics;

namespace Content.Server._Hyperion.Worldgen.Components;

/// <summary>
///     Round-global fissure set for the worldgen-v1 belt (design repo Part 1).
///     Lives on the worldgen map (added via the worldgenConfig bundle). The bearings
///     are rolled once at startup and stored so every chunk's <see cref="FissureCarverComponent"/>
///     reads the same crossings: geometry is a pure function of world position plus
///     these rolled bearings, so the carved lane never jitters at a chunk seam.
/// </summary>
[RegisterComponent]
public sealed partial class FissureFieldComponent : Component
{
    /// <summary>
    ///     The classes of fissure to roll. Default roster is the design's recommended
    ///     2 capital-width crossings + 4 small-ship shortcuts.
    /// </summary>
    [DataField]
    public List<FissureClass> Classes = new()
    {
        new FissureClass { Count = 2, HalfWidthTiles = 40f, OverhangPadTiles = 48f, DriftAmplitude = 0.22f },
        new FissureClass { Count = 4, HalfWidthTiles = 12f, OverhangPadTiles = 48f, DriftAmplitude = 0.30f },
    };

    /// <summary>
    ///     The world-radius band over which fissures cut. Outside this range the carver
    ///     is a no-op (the inner apron stays open on its own, the outer void has no rock).
    /// </summary>
    [DataField]
    public Vector2 RadiusRange = new(3000f, 37000f);

    /// <summary>
    ///     The rolled crossings. Populated once at startup; persisted so a map save/reload
    ///     does not re-roll a different set under already-spawned rock.
    /// </summary>
    [DataField]
    public List<Fissure> Fissures = new();

    /// <summary>
    ///     Guards against re-rolling if the component starts up more than once.
    /// </summary>
    [DataField]
    public bool Rolled;
}

/// <summary>
///     Config for a group of same-width fissures. All widths are in world tiles.
/// </summary>
[DataDefinition]
public sealed partial class FissureClass
{
    /// <summary>How many fissures of this class to roll.</summary>
    [DataField]
    public int Count = 1;

    /// <summary>
    ///     Half-width of the clear lane, held roughly constant in world space (the angular
    ///     half-width is derived per radius). 40 ~= a capital-width lane, 12 ~= a small ship.
    /// </summary>
    [DataField]
    public float HalfWidthTiles = 12f;

    /// <summary>
    ///     Extra half-width added purely to cancel big-rock overhang: the carve rejects rock
    ///     centers, so a giant centered just outside the lane would still bulge in. Pad by the
    ///     band's max rock radius (Colossal ~= 44) so the physical lane stays clear.
    /// </summary>
    [DataField]
    public float OverhangPadTiles = 48f;

    /// <summary>
    ///     Angular wander amplitude (radians) applied as the lane climbs in radius. Kept small
    ///     enough that the lane stays connected but large enough to read as winding, not radial.
    /// </summary>
    [DataField]
    public float DriftAmplitude = 0.25f;

    /// <summary>
    ///     Chamber swell amplitude (tiles). Drives the lane wider/narrower along its length so it
    ///     breathes into open pockets. 0 = a constant-width lane.
    /// </summary>
    [DataField]
    public float ChamberAmplitudeTiles;
}

/// <summary>
///     A single rolled crossing. Geometry: at radius r the lane centers on
///     <c>Bearing + DriftAmplitude * driftNoise(r)</c> with angular half-width
///     <c>(HalfWidthTiles + OverhangPadTiles + chamber(r)) / r</c>.
/// </summary>
[DataDefinition]
public sealed partial class Fissure
{
    [DataField]
    public float Bearing;

    [DataField]
    public float HalfWidthTiles;

    [DataField]
    public float OverhangPadTiles;

    [DataField]
    public float DriftAmplitude;

    [DataField]
    public float ChamberAmplitudeTiles;

    [DataField]
    public float DriftPhaseA;

    [DataField]
    public float DriftPhaseB;

    [DataField]
    public float ChamberPhase;
}
