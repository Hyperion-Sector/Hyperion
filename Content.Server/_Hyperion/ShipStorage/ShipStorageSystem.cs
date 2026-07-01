// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Server.Database;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Phase-1 ship persistence (garage reload): stores a deeded grid as an
/// engine-serialized blob in the database and re-materializes it on retrieve.
/// Wraps the engine map serializer with an identity (<see cref="Guid"/> ship id) and
/// owner check; it never decomposes a ship (see the ship-persistence design RFC).
/// TODO(hyperion): store-flow gating (FoundOrganics, hazard checks) and custody
/// transfer are deferred to a later cycle per the RFC and are not implemented here.
/// </summary>
public sealed class ShipStorageSystem : EntitySystem
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IResourceManager _resourceManager = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;

    private const int KeepRevisions = 3;

    /// <summary>
    /// Stores <paramref name="gridUid"/> for <paramref name="ownerUserId"/>:
    /// serializes the grid to YAML, commits a new blob revision, then despawns
    /// the grid (serialize → commit → despawn). Returns the ship's persistent
    /// id, or null if the grid failed to serialize (<see cref="MapLoaderSystem.TrySaveGrid"/>
    /// returned false).
    /// TODO(hyperion): store-flow gates (FoundOrganics, hazard) are deferred to
    /// a later cycle per the RFC; this method does not gate the store today.
    /// </summary>
    public async Task<Guid?> TryStoreShip(EntityUid gridUid, Guid ownerUserId)
    {
        // Serialize to a scratch file in user data, then read it back as bytes. The engine
        // serializer only writes to a TextWriter/ResPath, so a scratch file is the minimal
        // path to get an in-memory blob without touching the real filesystem elsewhere.
        var shipId = Guid.NewGuid();
        var scratchPath = new ResPath($"/ship_storage/{shipId}.yml");

        var shipName = Comp<MetaDataComponent>(gridUid).EntityName;

        if (!_mapLoader.TrySaveGrid(gridUid, scratchPath))
            return null;

        // Synchronous I/O throughout: the integration test game loop's message channel does not
        // pump the RobustSynchronizationContext, so a genuinely-suspending await here would hang
        // forever in the integration test game loop (the live server pumps the sync context
        // normally; off-thread I/O is a deferred RFC step, not a prohibition).
        byte[] gzipped;
        try
        {
            using (var stream = _resourceManager.UserData.Open(scratchPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var buffer = new MemoryStream())
            {
                using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    stream.CopyTo(gzip);
                }

                gzipped = buffer.ToArray();
            }
        }
        finally
        {
            _resourceManager.UserData.Delete(scratchPath);
        }

        var checksum = SHA256.HashData(gzipped);

        var record = new ShipStorageRecord
        {
            ShipGuid = shipId,
            OwnerUserId = ownerUserId,
            ShipName = shipName,
            VesselProto = string.Empty,
            ProtoFingerprint = string.Empty,
            EngineFormatVer = 1,
            Checksum = checksum,
        };

        await _db.SaveShipRevision(record, gzipped, KeepRevisions);

        // serialize -> commit -> despawn: the grid is only removed after the blob is filed.
        QueueDel(gridUid);

        return shipId;
    }

    /// <summary>
    /// Retrieves the ship identified by <paramref name="shipId"/> for
    /// <paramref name="ownerUserId"/>: fetches the current blob revision,
    /// deserializes it onto a map and returns the new grid, or null if the
    /// ship is unknown, owned by someone else, or fails to load.
    /// </summary>
    public async Task<EntityUid?> TryRetrieveShip(Guid shipId, Guid ownerUserId)
    {
        var index = await _db.GetShipIndex(shipId);
        if (index == null || index.OwnerUserId != ownerUserId)
            return null;

        var blob = await _db.GetShipBlob(shipId, index.CurrentRevision);
        if (blob == null)
            return null;

        var scratchPath = new ResPath($"/ship_storage/{shipId}-retrieve.yml");

        // Synchronous I/O: see the note in TryStoreShip (the live server pumps the sync context
        // normally; off-thread I/O is a deferred RFC step, not a prohibition here either).
        bool loaded;
        Entity<MapGridComponent>? grid;
        try
        {
            using (var gzip = new GZipStream(new MemoryStream(blob), CompressionMode.Decompress))
            using (var stream = _resourceManager.UserData.Open(scratchPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                gzip.CopyTo(stream);
            }

            // TODO(hyperion): TryLoadGrid mints a fresh throwaway map per retrieve (the out map is
            // discarded here); nothing reparents or deletes it. A later cycle must present the grid
            // docked via the shipyard-map FTL pattern (see ShipyardSystem.TryAddShuttle) and either
            // reparent onto that map + delete this scratch one, or capture the out-map and QueueDel it.
            loaded = _mapLoader.TryLoadGrid(scratchPath, out _, out grid);
        }
        finally
        {
            _resourceManager.UserData.Delete(scratchPath);
        }

        return loaded ? grid!.Value.Owner : null;
    }

    /// <summary>
    /// Lists the stored ships owned by <paramref name="ownerUserId"/> (hot index only).
    /// </summary>
    public Task<List<ShipStorageRecord>> GetStoredShips(Guid ownerUserId)
    {
        return _db.GetShipsByOwner(ownerUserId);
    }
}
