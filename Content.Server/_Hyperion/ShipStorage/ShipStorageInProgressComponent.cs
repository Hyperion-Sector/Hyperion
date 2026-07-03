// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Raised on a grid for the span of a <see cref="ShipStorageSystem.TryStoreShip"/>
/// call (before serialize, dropped in the finally). While present, container
/// insertion targeting anything on the grid is blocked (see
/// <see cref="ShipStorageInProgressSystem"/>) so nothing reparents into a ship
/// during the async DB-commit window before it despawns.
/// </summary>
[RegisterComponent]
public sealed partial class ShipStorageInProgressComponent : Component;
