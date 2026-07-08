// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Content.Server._Hyperion.Worldgen.Systems;

namespace Content.Server._Hyperion.Worldgen.Components;

/// <summary>
///     Marks a worldgen chunk as participating in fissure carving. The crossings themselves
///     live on the map's <see cref="FissureFieldComponent"/>; this marker only exists so the
///     per-point <c>PrePlaceDebrisFeatureEvent</c> (raised on the chunk) reaches the carver.
///     Put it on the belt bands the fissures must cross.
/// </summary>
[RegisterComponent]
[Access(typeof(FissureCarverSystem))]
public sealed partial class FissureCarverComponent : Component
{
}
