// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Raised on a grid for the span of a <see cref="ShipStorageSystem.TryStoreShip"/>
/// call (before serialize, dropped in the finally). While present, container
/// insertion targeting anything on the grid is blocked (see
/// <see cref="ShipStorageInProgressSystem"/>) so nothing reparents into a ship
/// during the async DB-commit window before it despawns.
/// <para>[UnsavedComponent] is LOAD-BEARING: the marker is stamped BEFORE TrySaveGrid
/// (it must block insertion during the serialize/commit window), so without it the
/// marker rides the blob and a retrieved ship comes back permanently "in progress" —
/// with ALL container insertion blocked aboard, hands included: nothing on the ship
/// can be picked up. Found in-game (Cycle 6).</para>
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class ShipStorageInProgressComponent : Component;
