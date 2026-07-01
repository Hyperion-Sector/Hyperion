// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Hot-index row for a stored ship (see the ship_storage table).
/// The blob itself is fetched separately by (ShipGuid, Revision).
/// </summary>
public sealed record ShipStorageRecord
{
    public required Guid ShipGuid { get; init; }
    public required Guid OwnerUserId { get; init; }
    public required string ShipName { get; init; }
    public required string VesselProto { get; init; }
    public required string ProtoFingerprint { get; init; }
    public required int EngineFormatVer { get; init; }
    public required byte[] Checksum { get; init; }
    public int SizeBytes { get; init; }
    public int SizeClass { get; init; }
    public int CurrentRevision { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
