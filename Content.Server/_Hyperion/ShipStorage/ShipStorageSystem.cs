// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Server._NF.Shipyard.Systems;
using Content.Server.Database;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Nuke;
using Content.Shared._Hyperion.CCVar;
using Content.Shared._Hyperion.ShipSize;
using Content.Shared.Damage;
using Content.Shared.Explosion.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Components;
using Content.Shared.NodeContainer;
using Content.Shared.Nuke;
using Content.Shared.Singularity.Components;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
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
/// TODO(hyperion): the PREP block (sidecars, strip-list, store-in-progress),
/// validation backstop and the active-ship registry are deferred to later
/// cycles per the RFC. The organics gate (no mind-bearing mob aboard) is live
/// as of Cycle 2a; the hazard gate (armed nuke / active countdown /
/// singularity aboard) is live as of Cycle 2b.
/// </summary>
public sealed class ShipStorageSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ShipSizeSystem _shipSize = default!;
    [Dependency] private readonly ShipyardSystem _shipyard = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;

    /// <summary>
    /// Stores <paramref name="gridUid"/> for <paramref name="ownerUserId"/>:
    /// gates on no organics aboard then no hazard aboard, then serializes the grid
    /// to yaml in memory, checksums the uncompressed text, compresses with zstd,
    /// commits a new blob revision, then despawns the grid
    /// (gate → serialize → commit → despawn).
    /// Returns a <see cref="ShipStorageResult"/> plus the ship's persistent id on
    /// success (null on refusal). A refused store leaves the grid fully untouched.
    /// </summary>
    public async Task<(ShipStorageResult Result, Guid? ShipId)> TryStoreShip(EntityUid gridUid, Guid ownerUserId)
    {
        // Organics gate (RFC store flow): reuse the NF FoundOrganics predicate as-is
        // rather than duplicating it. It trips only on player sessions / live minds,
        // so mindless pets pass and persist. Runs before any serialize/DB work so a
        // refusal leaves the world untouched.
        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();
        if (_shipyard.FoundOrganics(gridUid, mobQuery, xformQuery) is not null)
            return (ShipStorageResult.OrganicsAboard, null);

        // Hazard gate (RFC store flow): "no aboard hazard (armed nuke/active
        // countdown/singularity)?" — runtime countdowns are [DataField]s that would
        // resume on thaw, so a store must be refused up front rather than freezing
        // an armed ship. World-wide queries (not a grid-child walk) since hazards
        // are rare; Transform.GridUid follows the parent chain through container
        // nesting, so a hazard stashed inside a crate on the ship still trips this.
        if (HasHazardAboard(gridUid))
            return (ShipStorageResult.HazardAboard, null);

        var shipId = Guid.NewGuid();
        var shipName = Comp<MetaDataComponent>(gridUid).EntityName;
        var sizeClass = _shipSize.GetSizeClass((gridUid, Comp<MapGridComponent>(gridUid)));

        // Pipe-gas sidecar (RFC Fidelity mechanism 2): PipeNet.Air lives on the
        // node-group object graph, which the map serializer cannot reach, so it
        // would be silently lost on store. Inject a PipeNetGasHolderComponent per
        // pipe entity carrying that pipe's volume-proportional share of its net's
        // gas; PipeNetGasRestoreSystem sums it back in (and consumes it) on the
        // first NodeGroupsRebuilt the reloaded grid sees. Injected here, BEFORE
        // TrySaveGrid, so the sidecars are present in the serialized yaml; the
        // live net's Air is left untouched (the ship stays live until despawn).
        var injectedSidecars = InjectPipeNetGasSidecars(gridUid);

        // Damage sidecar (RFC Fidelity mechanism 2): DamageableComponent.Damage is
        // [DataField(readOnly: true)], so the map serializer never writes it and a
        // damaged entity would come back pristine (a free-repair exploit on a combat
        // ship). Same consume-once shape as the gas sidecar: copy the live damage
        // into a DamageSidecarComponent here, before TrySaveGrid, so it lands in the
        // serialized yaml. The live DamageableComponent is left untouched (the ship
        // stays live until despawn). Injected entities ride the SAME failure-cleanup
        // path as the gas sidecars (see the finally block below).
        var injectedDamageSidecars = InjectDamageSidecars(gridUid);
        var sidecarsConsumed = false;

        // TODO(hyperion): store currently runs five world-wide AllEntityQuery scans
        // (three hazard classes + both sidecar injections; damage is the pricey one —
        // every wall has a DamageableComponent). Store is a rare quiesced operation,
        // so correctness-first stands for now; fold these into one shared grid-child
        // walk when the RFC's store profiling lands (measure first, then size it).

        try
        {
            // In-memory engine serialize. Synchronous CPU work on the game thread is the
            // RFC's sanctioned prototype shape (measure, then amortize); the off-thread
            // tail takes exactly this yaml string in a later cycle. (Also: a suspending
            // await inside a WaitPost-driven integration test never resumes — the test
            // loop doesn't pump the sync context — so only DB awaits belong here.)
            string yaml;
            using (var writer = new StringWriter())
            {
                if (!_mapLoader.TrySaveGrid(gridUid, writer))
                    return (ShipStorageResult.SerializeFailed, null);

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

            sidecarsConsumed = true;
            return (ShipStorageResult.Success, shipId);
        }
        finally
        {
            // Cleanup-on-failure: on ANY non-success exit after injection (serialize
            // failure, DB throw), strip the sidecars back off the still-live grid.
            // On success the grid despawns anyway (harmless either way), but a
            // lingering sidecar on a ship that stays alive would re-apply on the
            // next NodeGroupsRebuilt (e.g. a player cutting a pipe) and duplicate
            // gas without persistence ever having been involved. try/finally keyed
            // on the success flag is simpler to reason about than duplicating the
            // cleanup at every early-return site.
            if (!sidecarsConsumed)
            {
                foreach (var sidecar in injectedSidecars)
                    RemComp<PipeNetGasHolderComponent>(sidecar);

                foreach (var sidecar in injectedDamageSidecars)
                    RemComp<DamageSidecarComponent>(sidecar);
            }
        }
    }

    /// <summary>
    /// Distributes each distinct PipeNet's gas on <paramref name="gridUid"/> across
    /// its member pipe entities, proportional to <see cref="PipeNode.Volume"/>, into
    /// a fresh <see cref="PipeNetGasHolderComponent"/> per member. The live net's Air
    /// is left untouched — the ship stays live until despawn. Returns the entities
    /// that received a sidecar, so a failed store can strip them back off.
    /// </summary>
    private List<EntityUid> InjectPipeNetGasSidecars(EntityUid gridUid)
    {
        var injected = new List<EntityUid>();

        // De-dup nets by node-group identity: a net has multiple member pipes, and
        // we only want to distribute its gas once (keyed on the first pipe seen).
        var seenNets = new Dictionary<object, List<(EntityUid Owner, PipeNode Pipe)>>();

        var query = AllEntityQuery<NodeContainerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var nodeContainer, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            foreach (var node in nodeContainer.Nodes.Values)
            {
                if (node is not PipeNode { NodeGroup: { } nodeGroup } pipe)
                    continue;

                if (!seenNets.TryGetValue(nodeGroup, out var members))
                    seenNets[nodeGroup] = members = new List<(EntityUid, PipeNode)>();

                members.Add((uid, pipe));
            }
        }

        foreach (var members in seenNets.Values)
        {
            var totalVolume = 0f;
            foreach (var (_, pipe) in members)
                totalVolume += pipe.Volume;

            if (totalVolume <= 0f)
                continue;

            var netAir = members[0].Pipe.Air;

            foreach (var (owner, pipe) in members)
            {
                var fraction = pipe.Volume / totalVolume;
                var share = new Content.Shared.Atmos.GasMixture(netAir) { Volume = pipe.Volume };
                share.Multiply(fraction);

                var holder = EnsureComp<PipeNetGasHolderComponent>(owner);
                holder.GasMixture = share;
                injected.Add(owner);
            }
        }

        return injected;
    }

    /// <summary>
    /// Copies the live <see cref="DamageSpecifier"/> of every damaged entity on
    /// <paramref name="gridUid"/> (<c>TotalDamage > 0</c>) into a fresh
    /// <see cref="DamageSidecarComponent"/>, so it survives the readOnly
    /// <see cref="DamageableComponent.Damage"/> field being dropped by the map
    /// serializer. The live component is left untouched. Returns the entities that
    /// received a sidecar, so a failed store can strip them back off.
    /// </summary>
    private List<EntityUid> InjectDamageSidecars(EntityUid gridUid)
    {
        var injected = new List<EntityUid>();

        var query = AllEntityQuery<DamageableComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var damageable, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            if (damageable.TotalDamage <= FixedPoint2.Zero)
                continue;

            var holder = EnsureComp<DamageSidecarComponent>(uid);
            holder.DamageDict = new Dictionary<string, FixedPoint2>(damageable.Damage.DamageDict);
            injected.Add(uid);
        }

        return injected;
    }

    /// <summary>
    /// Checks whether any of the three RFC hazard classes are present aboard
    /// <paramref name="gridUid"/>: an armed nuke, an active countdown timer trigger,
    /// or a singularity. Each is a world-wide query filtered by
    /// <see cref="TransformComponent.GridUid"/> rather than a grid-child walk;
    /// hazards are rare, and GridUid resolves correctly through container nesting
    /// (a nuke stashed in a crate still reports the ship's GridUid).
    /// </summary>
    private bool HasHazardAboard(EntityUid gridUid)
    {
        var nukeQuery = AllEntityQuery<NukeComponent, TransformComponent>();
        while (nukeQuery.MoveNext(out _, out var nuke, out var xform))
        {
            if (xform.GridUid == gridUid && nuke.Status == NukeStatus.ARMED)
                return true;
        }

        var timerQuery = AllEntityQuery<ActiveTimerTriggerComponent, TransformComponent>();
        while (timerQuery.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                return true;
        }

        var singularityQuery = AllEntityQuery<SingularityComponent, TransformComponent>();
        while (singularityQuery.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                return true;
        }

        return false;
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
            {
                // Rehydration pass (RFC retrieve flow): sidecar-carried state that the
                // map serializer can't reach on its own gets reapplied here, once the
                // grid has fully materialized. Damage is the first resident; later
                // cycles add device-network re-registration, SmartFridge index rebuild,
                // etc. to this same seam.
                RehydrateDamage(grid.Value.Owner);
                return grid.Value.Owner;
            }

            Del(mapUid);
            Log.Error($"Ship {shipId} revision {revision} passed checksum but failed to load.");
            return null;
        }

        Log.Error($"Ship {shipId}: no stored revision passed verification; retrieve refused.");
        return null;
    }

    /// <summary>
    /// Applies every <see cref="DamageSidecarComponent"/> on <paramref name="gridUid"/>
    /// back onto its holder's <see cref="DamageableComponent"/> via
    /// <see cref="DamageableSystem.SetDamage"/>, then removes the sidecar
    /// (consume-once, naturally idempotent). Deliberately run from this explicit
    /// post-load pass rather than a component-startup hook: applying at
    /// ComponentStartup would fire <c>DamageChangedEvent</c> into
    /// <c>DestructibleSystem</c> while the grid is still settling, risking a
    /// threshold trip (Destruction/ChangeConstructionNode) against not-yet-final
    /// state. Running after <c>TryLoadGrid</c> returns lets thresholds evaluate
    /// against fully-materialized state instead.
    /// </summary>
    private void RehydrateDamage(EntityUid gridUid)
    {
        var query = AllEntityQuery<DamageSidecarComponent, DamageableComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var sidecar, out var damageable, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            var damage = new DamageSpecifier { DamageDict = new Dictionary<string, FixedPoint2>(sidecar.DamageDict) };
            _damageable.SetDamage(uid, damageable, damage);
            RemComp<DamageSidecarComponent>(uid);
        }
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
