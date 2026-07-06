// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0
namespace Content.Server._Hyperion.NPC;

/// <summary>
/// Hyperion: marks a mob that keeps a home tile (its nest). Stamped with the spawn location on map init
/// by <see cref="MouseNestSystem"/>; the HTN drifts the mob back toward it when there's nothing more
/// pressing to do, giving mice a territory instead of an aimless wander.
/// </summary>
[RegisterComponent]
public sealed partial class MouseNestComponent : Component
{
}
