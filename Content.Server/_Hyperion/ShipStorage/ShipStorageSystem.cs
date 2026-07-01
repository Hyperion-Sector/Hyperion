// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Threading.Tasks;
using Content.Server.Database;
using Robust.Shared.Map;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Phase-1 ship persistence (garage reload): stores a deeded grid as an
/// engine-serialized blob in the database and re-materializes it on retrieve.
/// Wraps the engine map serializer with identity, gating and custody; it never
/// decomposes a ship (see the ship-persistence design RFC).
/// </summary>
public sealed class ShipStorageSystem : EntitySystem
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;

    /// <summary>
    /// Stores <paramref name="gridUid"/> for <paramref name="ownerUserId"/>:
    /// gates, serializes the grid to YAML, commits a new blob revision, then
    /// despawns the grid (serialize → commit → despawn). Returns the ship's
    /// persistent id, or null if a gate refused the store.
    /// </summary>
    public Task<Guid?> TryStoreShip(EntityUid gridUid, Guid ownerUserId)
    {
        // GREEN phase implements per the RFC store flow.
        throw new NotImplementedException("ShipStorageSystem.TryStoreShip is not implemented yet.");
    }

    /// <summary>
    /// Retrieves the ship identified by <paramref name="shipId"/> for
    /// <paramref name="ownerUserId"/>: fetches the current blob revision,
    /// deserializes it onto a map and returns the new grid, or null if the
    /// ship is unknown, owned by someone else, or fails to load.
    /// </summary>
    public Task<EntityUid?> TryRetrieveShip(Guid shipId, Guid ownerUserId)
    {
        // GREEN phase implements per the RFC retrieve flow.
        throw new NotImplementedException("ShipStorageSystem.TryRetrieveShip is not implemented yet.");
    }

    /// <summary>
    /// Lists the stored ships owned by <paramref name="ownerUserId"/> (hot index only).
    /// </summary>
    public Task<List<ShipStorageRecord>> GetStoredShips(Guid ownerUserId)
    {
        return _db.GetShipsByOwner(ownerUserId);
    }
}
