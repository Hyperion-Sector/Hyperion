// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._Hyperion.CCVar;
using Content.Shared._Hyperion.ShipSize;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;
using YamlDotNet.RepresentationModel;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Phase-1 ship persistence (garage reload): stores a deeded grid as an
/// engine-serialized blob in the database and re-materializes it on retrieve.
/// Wraps the engine map serializer with an identity (<see cref="Guid"/> ship id),
/// owner check, integrity checksums and drift metadata; it never decomposes a
/// ship (see the ship-persistence design RFC).
/// The pipeline is fully in-memory: serialize → yaml text → checksum → zstd → DB,
/// and the mirror on retrieve. No filesystem involvement.
/// TODO(hyperion): store-flow gating (FoundOrganics, hazard checks), the PREP block
/// (sidecars, strip-list, store-in-progress), validation backstop and the
/// active-ship registry are deferred to later cycles per the RFC.
/// </summary>
public sealed class ShipStorageSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ShipSizeSystem _shipSize = default!;

    /// <summary>
    /// Stores <paramref name="gridUid"/> for <paramref name="ownerUserId"/>:
    /// serializes the grid to yaml in memory, checksums the uncompressed text,
    /// compresses with zstd, commits a new blob revision, then despawns the grid
    /// (serialize → commit → despawn). Returns the ship's persistent id, or null
    /// if the grid failed to serialize.
    /// TODO(hyperion): store-flow gates (FoundOrganics, hazard) are deferred to
    /// a later cycle per the RFC; this method does not gate the store today.
    /// </summary>
    public async Task<Guid?> TryStoreShip(EntityUid gridUid, Guid ownerUserId)
    {
        var shipId = Guid.NewGuid();
        var shipName = Comp<MetaDataComponent>(gridUid).EntityName;
        var sizeClass = _shipSize.GetSizeClass((gridUid, Comp<MapGridComponent>(gridUid)));

        // In-memory engine serialize. Synchronous CPU work on the game thread is the
        // RFC's sanctioned prototype shape (measure, then amortize); the off-thread
        // tail takes exactly this yaml string in a later cycle. (Also: a suspending
        // await inside a WaitPost-driven integration test never resumes — the test
        // loop doesn't pump the sync context — so only DB awaits belong here.)
        string yaml;
        using (var writer = new StringWriter())
        {
            if (!_mapLoader.TrySaveGrid(gridUid, writer))
                return null;

            yaml = writer.ToString();
        }

        var yamlBytes = Encoding.UTF8.GetBytes(yaml);

        // Checksum the UNCOMPRESSED yaml (RFC order: checksum, then compress) so
        // stored hashes survive any future compression change.
        var checksum = SHA256.HashData(yamlBytes);

        var (fingerprint, formatVer) = ReadDriftMetadata(yaml);

        var record = new ShipStorageRecord
        {
            ShipGuid = shipId,
            OwnerUserId = ownerUserId,
            ShipName = shipName,
            // TODO(hyperion): VesselProto comes with deed integration (the grid alone
            // doesn't know its vessel prototype); filled in the console/deed cycle.
            VesselProto = string.Empty,
            ProtoFingerprint = fingerprint,
            EngineFormatVer = formatVer,
            Checksum = checksum,
            SizeClass = (int) sizeClass,
        };

        var blob = CompressZstd(yamlBytes);
        var keepRevisions = _cfg.GetCVar(HyperionCVars.ShipStorageKeepRevisions);
        await _db.SaveShipRevision(record, blob, keepRevisions);

        // serialize -> commit -> despawn: the grid is only removed after the blob is
        // filed. A concurrent double-store of the same ship cannot dupe: the composite
        // PK on (ship_guid, revision) makes the second transaction fail loudly.
        QueueDel(gridUid);

        return shipId;
    }

    /// <summary>
    /// Retrieves the ship identified by <paramref name="shipId"/> for
    /// <paramref name="ownerUserId"/>. Verifies the blob checksum and falls back
    /// to earlier revisions on mismatch (logged); decompresses in memory and loads
    /// the grid onto a fresh map. Returns the new grid, or null if the ship is
    /// unknown, owned by someone else, or no revision passes verification.
    /// </summary>
    public async Task<EntityUid?> TryRetrieveShip(Guid shipId, Guid ownerUserId)
    {
        var index = await _db.GetShipIndex(shipId);
        if (index == null || index.OwnerUserId != ownerUserId)
            return null;

        var keepRevisions = _cfg.GetCVar(HyperionCVars.ShipStorageKeepRevisions);
        var oldest = Math.Max(1, index.CurrentRevision - keepRevisions + 1);

        for (var revision = index.CurrentRevision; revision >= oldest; revision--)
        {
            var stored = await _db.GetShipBlob(shipId, revision);
            if (stored == null)
                continue;

            byte[] yamlBytes;
            try
            {
                yamlBytes = DecompressZstd(stored.Blob);
            }
            catch (Exception e)
            {
                Log.Error($"Ship {shipId} revision {revision} failed to decompress, trying previous revision: {e.Message}");
                continue;
            }

            if (!SHA256.HashData(yamlBytes).AsSpan().SequenceEqual(stored.Checksum))
            {
                Log.Error($"Ship {shipId} revision {revision} failed checksum verification, trying previous revision.");
                continue;
            }

            if (revision != index.CurrentRevision)
                Log.Warning($"Ship {shipId} retrieved from fallback revision {revision} (current {index.CurrentRevision} corrupt).");

            // TODO(hyperion): a later cycle presents the grid docked via the
            // shipyard-map FTL pattern (see ShipyardSystem.TryAddShuttle) instead of
            // a bare new map per retrieve.
            var mapUid = _map.CreateMap(out var mapId);

            using var reader = new StreamReader(new MemoryStream(yamlBytes), Encoding.UTF8);
            if (_mapLoader.TryLoadGrid(mapId, reader, $"ship_storage/{shipId}", out var grid))
                return grid.Value.Owner;

            Del(mapUid);
            Log.Error($"Ship {shipId} revision {revision} passed checksum but failed to load.");
            return null;
        }

        Log.Error($"Ship {shipId}: no stored revision passed verification; retrieve refused.");
        return null;
    }

    /// <summary>
    /// Lists the stored ships owned by <paramref name="ownerUserId"/> (hot index only).
    /// </summary>
    public Task<List<ShipStorageRecord>> GetStoredShips(Guid ownerUserId)
    {
        return _db.GetShipsByOwner(ownerUserId);
    }

    /// <summary>
    /// Extracts the reconciliation metadata from a serialized grid: the map format
    /// version, and a fingerprint hashing the sorted set of entity prototype ids the
    /// blob references (the drift key — a changed set means re-bake territory).
    /// </summary>
    private static (string Fingerprint, int FormatVersion) ReadDriftMetadata(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode) stream.Documents[0].RootNode;

        var formatVer = 0;
        if (root.Children.TryGetValue(new YamlScalarNode("meta"), out var metaNode)
            && metaNode is YamlMappingNode meta
            && meta.Children.TryGetValue(new YamlScalarNode("format"), out var format))
        {
            int.TryParse(((YamlScalarNode) format).Value, out formatVer);
        }

        var protos = new SortedSet<string>(StringComparer.Ordinal);
        if (root.Children.TryGetValue(new YamlScalarNode("entities"), out var entitiesNode)
            && entitiesNode is YamlSequenceNode entities)
        {
            foreach (var entry in entities.Children.OfType<YamlMappingNode>())
            {
                if (entry.Children.TryGetValue(new YamlScalarNode("proto"), out var proto)
                    && ((YamlScalarNode) proto).Value is { Length: > 0 } protoId)
                {
                    protos.Add(protoId);
                }
            }
        }

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', protos))));
        return (fingerprint, formatVer);
    }

    private static byte[] CompressZstd(byte[] input)
    {
        using var output = new MemoryStream();
        using (var compress = new ZStdCompressStream(output, ownStream: false))
        {
            compress.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] DecompressZstd(byte[] input)
    {
        using var decompress = new ZStdDecompressStream(new MemoryStream(input));
        using var output = new MemoryStream();
        decompress.CopyTo(output);
        return output.ToArray();
    }
}
