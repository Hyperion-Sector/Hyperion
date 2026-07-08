// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

namespace Content.Server._Hyperion.Worldgen.Components;

/// <summary>
///     Per-chunk marker that lets a belt band read the round's trojan pockets (see
///     <see cref="TrojanFieldComponent"/>). Carried by the bands that overlap the pocket radius
///     band so the per-chunk debris-selector event reaches <c>TrojanSystem</c>. Carries no data;
///     the pockets live on the map.
/// </summary>
[RegisterComponent]
public sealed partial class TrojanCarverComponent : Component;
