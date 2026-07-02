// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Outcome of a <see cref="ShipStorageSystem.TryStoreShip"/> call. A refused store
/// surfaces WHY so the caller (console / BUI) can tell the player what to fix, and
/// so a refusal leaves the grid untouched instead of silently no-op'ing.
/// </summary>
public enum ShipStorageResult : byte
{
    /// <summary>The grid was serialized, filed and despawned.</summary>
    Success,

    /// <summary>The engine map serializer could not write the grid.</summary>
    SerializeFailed,

    /// <summary>A living, sapient being (player session or live mind) is aboard.</summary>
    OrganicsAboard,

    /// <summary>An aboard hazard (armed nuke, active countdown, singularity) blocks the store.</summary>
    HazardAboard,

    /// <summary>
    /// The save-time round-trip validation backstop found the freshly serialized blob
    /// disagrees with the live grid on persistent state. The store is aborted before any
    /// blob revision is filed and the live grid is left intact, so a serializer regression
    /// can never silently drop or half-commit a ship.
    /// </summary>
    ValidationFailed,
}
