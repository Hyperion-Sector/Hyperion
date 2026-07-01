// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Robust.Shared.Serialization;

namespace Content.Shared._Hyperion.ShipSize;

/// <summary>
/// Size classification for a ship's hull, derived from its built (non-empty) tile count.
/// </summary>
[Serializable, NetSerializable]
public enum ShipSizeClass : byte
{
    Cutter,
    Corvette,
    Frigate,
    Cruiser,
    Capital,
    SuperCapital,
}
