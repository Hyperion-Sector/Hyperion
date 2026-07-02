// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._NF.Station.Components;
using Content.Server.Database;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Nuke;
using Content.Shared._Hyperion.CCVar;
using Content.Shared._Hyperion.ShipSize;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Damage;
using Content.Shared.Explosion.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Components;
using Content.Shared.NodeContainer;
using Content.Shared.Nuke;
using Content.Shared.GameTicking;
using Content.Shared.Singularity.Components;
using Content.Shared.Station.Components;
using Robust.Shared.Configuration;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Manager;
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
/// TODO(hyperion): the "store-in-progress" flag is deferred to a later cycle per
/// the RFC. The organics gate (no mind-bearing mob aboard) is live as of Cycle 2a;
/// the hazard gate (armed nuke / active countdown / singularity aboard) is live as
/// of Cycle 2b; the save-time round-trip validation backstop, the active-ship
/// registry, and the store strip-list are all live as of Cycle 3; the grid-side
/// deed identity stamp (cross-round resolve, see <see cref="ResolveOrMintShipId"/>)
/// is live as of Cycle 4.
/// </summary>
public sealed partial class ShipStorageSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly ShipSizeSystem _shipSize = default!;
    [Dependency] private readonly ShipyardSystem _shipyard = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;

    /// <summary>
    /// Test seam for the save-time validation backstop. When assigned, the backstop
    /// treats its round-trip diff as a forced mismatch for the grid the delegate returns
    /// true for, exercising the abort path deterministically: persistent
    /// <c>[DataField]</c> state round-trips by definition, so a genuine save-time
    /// mismatch cannot be manufactured without an actual serializer regression. Null
    /// (the production default) means "diff the round-trip for real".
    /// </summary>
    internal Func<EntityUid, bool>? ValidationMismatchOverride;

    /// <summary>
    /// Test seam: wipes the round-scoped active-ship registry, simulating a round
    /// boundary so cross-round identity paths (the deed leg) can be exercised without
    /// raising a full RoundRestartCleanupEvent through every subscriber in the server.
    /// </summary>
    internal void ClearActiveShipRegistry() => _activeShips.Clear();

    /// <summary>
    /// Active-ship registry (RFC "Anti-abuse", LOCKED): round-scoped <c>ShipId -&gt;
    /// live GridUid</c> map gating retrieve. There is no DB-side row-lock; both
    /// dupe windows are closed entirely in-process, which is sufficient for a
    /// single server instance. The SEQUENTIAL window (retrieve -&gt; fly -&gt;
    /// retrieve again at another console) is closed by the registered entry
    /// itself. The CONCURRENT window (two retrieves of the same ShipId racing
    /// before either has registered — red-team fix #2) is closed by a synchronous
    /// in-flight reservation (the <see cref="EntityUid.Invalid"/> sentinel) that
    /// <see cref="TryRetrieveShip"/> writes at its gate, before its first await —
    /// see that method. A future multi-instance deployment (several server
    /// processes sharing one DB) would need DB-side locking too, since this
    /// registry doesn't cross process boundaries. Entries are added on a
    /// successful retrieve, removed on a successful store and on the registered
    /// grid's deletion (any path — combat loss, admin del, cleanup GC, not just
    /// the store despawn), and the whole map resets on round restart since it has
    /// no meaning across rounds.
    /// </summary>
    private readonly Dictionary<Guid, EntityUid> _activeShips = new();

    /// <summary>
    /// Store strip-list (RFC "Implementation surface", small named mechanism):
    /// components removed from the live grid before <c>TrySaveGrid</c> because they
    /// are derived and/or hold session-scoped refs that would rot across a reload.
    /// <see cref="ShipRepairDataComponent"/> is the founding member (RFC
    /// resolved-decision 7): its <c>NetEntity?</c> refs and raw tile TypeIds are
    /// session-scoped derived state, regenerated against the loaded grid in a later
    /// rehydration cycle — this cycle's scope is strictly the strip. The (future)
    /// validation whitelist is maintained together with this list per the RFC.
    /// </summary>
    private static readonly Type[] StoreStripList =
    {
        typeof(ShipRepairDataComponent),
    };

    public override void Initialize()
    {
        base.Initialize();

        // Broadcast subscription (not component-directed): the engine allows only one
        // directed <MapGridComponent, EntityTerminatingEvent> subscriber process-wide,
        // and GridDeletionContainerSystem already holds that slot. Broadcast fires for
        // every terminating entity, but the release below is a cheap no-op scan of a
        // small (round-scoped, only-currently-flying-ships) map.
        SubscribeLocalEvent<EntityTerminatingEvent>(OnEntityTerminating);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
    }

    /// <summary>
    /// Clears any active-ship registry entry pointing at a grid that's being deleted,
    /// regardless of why (combat loss, admin del, cleanup GC, the store despawn
    /// itself). Without this a ShipId whose grid died outside the store path would
    /// stay "registered" and its owner could never retrieve it again this round.
    /// </summary>
    private void OnEntityTerminating(ref EntityTerminatingEvent args)
    {
        ReleaseActiveShip(args.Entity.Owner);
    }

    /// <summary>
    /// The active-ship registry is in-memory and round-scoped only (RFC "resets with
    /// the round") — it carries no meaning once the round ends, so it's wiped outright
    /// rather than reconciled entry-by-entry.
    /// </summary>
    private void OnRoundRestartCleanup(RoundRestartCleanupEvent args)
    {
        _activeShips.Clear();
    }

    /// <summary>
    /// Removes whichever active-ship registry entry (if any) points at
    /// <paramref name="gridUid"/>. The registry is keyed by ShipId, so releasing by
    /// grid requires a reverse scan; the map is round-scoped and small (only ships
    /// currently flying), so this stays cheap.
    /// </summary>
    private void ReleaseActiveShip(EntityUid gridUid)
    {
        if (_activeShips.Count == 0)
            return;

        foreach (var (shipId, registeredGrid) in _activeShips)
        {
            if (registeredGrid == gridUid)
            {
                _activeShips.Remove(shipId);
                break;
            }
        }
    }

    /// <summary>
    /// Resolves the ShipId a store should file under (red-team fix #1,
    /// RESOLVE-before-mint): a reverse-lookup of <paramref name="gridUid"/> in the
    /// active-ship registry. If this grid was materialized by a prior
    /// <see cref="TryRetrieveShip"/> this round, its entry is still present (it's
    /// only cleared on store/deletion, i.e. after this call), so this returns the
    /// EXISTING ShipId and the caller's store lands as a new revision on the same
    /// row. A grid that was never retrieved this round (never stored, or a fresh
    /// spawn) isn't in the registry, so a brand-new <see cref="Guid"/> is minted —
    /// the same reverse scan shape as <see cref="ReleaseActiveShip"/>.
    /// </summary>
    private Guid ResolveOrMintShipId(EntityUid gridUid)
    {
        foreach (var (id, registeredGrid) in _activeShips)
        {
            if (registeredGrid == gridUid)
                return id;
        }

        // Deed leg (Cycle 4): a grid that wasn't retrieved this round (registry miss)
        // may still carry its identity from a prior round on the grid-side deed.
        if (TryComp<ShuttleDeedComponent>(gridUid, out var deed) && deed.ShipId is { } deedShipId)
            return deedShipId;

        return Guid.NewGuid();
    }

    /// <summary>
    /// Stores <paramref name="gridUid"/> for <paramref name="ownerUserId"/>:
    /// gates on no organics aboard then no hazard aboard, then serializes the grid
    /// to yaml in memory, runs the save-time round-trip validation backstop against
    /// that yaml, checksums the uncompressed text, compresses with zstd,
    /// commits a new blob revision, then despawns the grid
    /// (gate → serialize → validate → commit → despawn).
    /// Identity: RESOLVE-before-mint (red-team fix #1). If <paramref name="gridUid"/>
    /// is currently registered in the active-ship registry — i.e. it was
    /// materialized by a prior <see cref="TryRetrieveShip"/> this round — the store
    /// RESOLVES and reuses that existing ShipId, landing as a new revision on the
    /// same DB row (<see cref="IServerDbManager.SaveShipRevision"/> upserts by
    /// ShipGuid). Only a grid never retrieved this round (not in the registry)
    /// MINTS a fresh <see cref="Guid"/>. Without this, every store-after-retrieve
    /// forks a brand-new, independently-retrievable duplicate row while the
    /// original stays fully retrievable — unbounded duplication on the happy path.
    /// The active-ship registry resolves same-round re-stores; a grid whose registry
    /// entry is gone (a later round) falls back to the grid-side deed's ShipId
    /// (Cycle 4, see <see cref="ResolveOrMintShipId"/>) before minting fresh.
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

        var shipId = ResolveOrMintShipId(gridUid);

        // Stamp identity onto the grid-side deed BEFORE serialize so it rides the blob
        // (and before the sidecars, so live and scratch agree in the validation diff).
        // A later abort leaves the stamp in place — harmless: the id is simply reserved
        // early, and the next successful store resolves it via the deed leg.
        _shipyard.EnsureShipStorageDeed(gridUid, shipId);

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

        // Store strip-list (RFC "Implementation surface"): remove derived /
        // session-scoped components — see StoreStripList — from the live grid before
        // TrySaveGrid, so they never enter the blob. Held as deep copies (not the
        // live, now-removed instances) so an aborted/refused store can restore them
        // with their field data intact. Applied AFTER the sidecar injections above
        // (both must be present in the live-vs-scratch state the validation backstop
        // diffs) and BEFORE serialize, per the RFC PREP ordering. Rides the SAME
        // sidecarsConsumed failure-cleanup discipline as the sidecars.
        var strippedComponents = StripListedComponents(gridUid);
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

            // Save-time validation backstop (RFC "Verification strategy", LOCKED): the
            // just-produced yaml is the only thing about to be committed, so scratch-
            // deserialize IT (not re-read the live grid) and diff its persistent state
            // against the live grid. The real diff always runs — mechanism stays honest —
            // but ValidationMismatchOverride, when a test assigns it, short-circuits the
            // verdict so the abort path can be exercised deterministically (a genuine
            // mismatch of round-tripping [DataField] state can't be manufactured without
            // an actual serializer bug).
            var mismatch = DetectRoundTripMismatch(gridUid, yaml);
            if (ValidationMismatchOverride != null)
                mismatch = ValidationMismatchOverride(gridUid);

            if (mismatch)
                return (ShipStorageResult.ValidationFailed, null);

            var yamlBytes = Encoding.UTF8.GetBytes(yaml);

            // Checksum the UNCOMPRESSED yaml (RFC order: checksum, then compress) so
            // stored hashes survive any future compression change.
            var checksum = SHA256.HashData(yamlBytes);

            var (fingerprint, formatVer) = ReadDriftMetadata(yaml);

            // Vessel prototype capture (spec section 1): the grid alone doesn't know its
            // vessel, but its station's latejoin info does. Empty for stationless grids
            // and pre-Cycle-4 blobs; retrieve tolerates empty (no station recreate).
            var vesselProto = string.Empty;
            if (TryComp<StationMemberComponent>(gridUid, out var stationMember)
                && TryComp<ExtraShuttleInformationComponent>(stationMember.Station, out var vesselInfo)
                && vesselInfo.Vessel is { } vessel)
            {
                vesselProto = vessel.Id;
            }

            var record = new ShipStorageRecord
            {
                ShipGuid = shipId,
                OwnerUserId = ownerUserId,
                ShipName = shipName,
                VesselProto = vesselProto,
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
            // Also close the active-ship registry's sequential-dupe window here: this
            // grid is no longer "flying" once it's committed to the blob.
            ReleaseActiveShip(gridUid);
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

                // Strip-list restore (RFC "abort" clause): the cut precedes serialize,
                // so any non-success exit must undo it — a store that refuses or
                // aborts must leave the live ship exactly as usable as it was before
                // PREP touched it, ShipRepairDataComponent included.
                RestoreStrippedComponents(gridUid, strippedComponents);
            }
        }
    }

    /// <summary>
    /// Save-time round-trip validation backstop (RFC "Verification strategy", LOCKED):
    /// deserializes the just-produced <paramref name="yaml"/> onto an inert scratch map
    /// (<c>InitializeMaps=false, PauseMaps=true</c> — it never map-inits or ticks, so it
    /// can't interact with the live sim), then diffs it against <paramref name="gridUid"/>
    /// in two tiers. Tier 1 is a whole-grid entity-count guard (drop/dupe). Tier 2 is a
    /// per-entity-prototype multiset diff: each side's direct grid children are tallied
    /// by <c>EntityPrototype</c> id, so a serializer bug that swaps one kind of entity
    /// for another while preserving the total count is also caught, not just a raw
    /// count. This is deliberately NOT a field-level diff.
    /// <para>
    /// A byte-for-byte DOUBLE-SERIALIZE comparison (re-serialize the scratch grid via
    /// the same <c>TrySaveGrid(TextWriter)</c> path and compare against
    /// <paramref name="yaml"/>) was tried first — it is the textbook way to diff
    /// persistent <c>[DataField]</c> state with no whitelist machinery, and
    /// <c>Content.IntegrationTests/Tests/SaveLoadSaveTest.cs</c> proves the technique
    /// is stable for content-free grids. It was reverted here because real ship content
    /// makes it unstable as a production abort gate: <c>PhysicsComponent.SleepTime</c>
    /// (a <c>[DataField]</c>) legitimately differs between the live grid's physics body
    /// and an inert scratch reload with zero content drift, because the reload's own
    /// fixture/broadphase rebuild resets it — exactly the "never round-trip
    /// Physics/Fixtures" landmine already on file for this engine. That is one instance
    /// of an open-ended class (fixture recompute, wake state, etc.), not a single fixed
    /// noise source like the map-format "time" stamp, so it can't be normalized away
    /// the same cheap way.
    /// </para>
    /// TODO(hyperion): field-level diff lands with the validation whitelist (RFC
    /// mechanism 10's whitelist companion) — a whitelist-scoped double-serialize
    /// comparison (only the fields the whitelist names) would sidestep the
    /// physics/fixture landmine above; today's multiset diff is the deepest diff stable
    /// enough to gate a production store without one.
    /// <para>
    /// The scratch map is deleted before returning on every path, so a store never
    /// leaks a map. Returns true on MISMATCH (the store must abort); any mismatch is
    /// logged with what differed before returning.
    /// </para>
    /// </summary>
    private bool DetectRoundTripMismatch(EntityUid gridUid, string yaml)
    {
        using var reader = new StringReader(yaml);
        var options = new DeserializationOptions
        {
            InitializeMaps = false,
            PauseMaps = true,
        };

        if (!_mapLoader.TryLoadGrid(reader, "ship_storage/validation", out Entity<MapComponent>? scratchMap, out Entity<MapGridComponent>? scratchGrid, options))
        {
            // Couldn't even reload what was just written: definitely a mismatch.
            Log.Warning($"Ship store validation failed for grid {ToPrettyString(gridUid)}: the just-produced yaml failed to reload onto a scratch grid.");
            return true;
        }

        try
        {
            // Tier 1 (cheap): whole-entity drop/dupe guard, ahead of the deeper diff.
            var liveChildCount = Transform(gridUid).ChildCount;
            var scratchChildCount = Transform(scratchGrid!.Value.Owner).ChildCount;
            if (liveChildCount != scratchChildCount)
            {
                Log.Warning($"Ship store validation failed for grid {ToPrettyString(gridUid)}: entity count mismatch (live={liveChildCount}, scratch={scratchChildCount}).");
                return true;
            }

            // Tier 2 (deeper, still coarse — see the method doc's TODO): per-entity
            // prototype-id multiset diff over each side's direct grid children.
            var liveCounts = CountChildPrototypes(gridUid);
            var scratchCounts = CountChildPrototypes(scratchGrid!.Value.Owner);

            if (!ChildPrototypeCountsMatch(liveCounts, scratchCounts, out var mismatchDetail))
            {
                Log.Warning($"Ship store validation failed for grid {ToPrettyString(gridUid)}: entity-prototype composition mismatch between live and " +
                            $"scratch-reloaded grid ({mismatchDetail}).");
                return true;
            }

            return false;
        }
        finally
        {
            Del(scratchMap!.Value.Owner);
        }
    }

    /// <summary>
    /// Tallies <paramref name="gridUid"/>'s direct children by <c>EntityPrototype</c>
    /// id, for the Tier 2 multiset diff in <see cref="DetectRoundTripMismatch"/>. Same
    /// child set Tier 1's <c>ChildCount</c> already walks (direct grid children only —
    /// entities nested inside containers, e.g. items in a locker, are not direct
    /// children and are not covered by either tier).
    /// </summary>
    private Dictionary<string, int> CountChildPrototypes(EntityUid gridUid)
    {
        var counts = new Dictionary<string, int>();
        var enumerator = Transform(gridUid).ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            var protoId = MetaData(child).EntityPrototype?.ID ?? "<no-prototype>";
            counts.TryGetValue(protoId, out var count);
            counts[protoId] = count + 1;
        }

        return counts;
    }

    /// <summary>
    /// Compares two prototype-id tallies from <see cref="CountChildPrototypes"/> for an
    /// exact match, returning a short human-readable description of the first
    /// discrepancy found (for the mismatch log line) when they don't.
    /// </summary>
    private static bool ChildPrototypeCountsMatch(Dictionary<string, int> live, Dictionary<string, int> scratch, out string mismatchDetail)
    {
        foreach (var (proto, liveCount) in live)
        {
            if (!scratch.TryGetValue(proto, out var scratchCount) || scratchCount != liveCount)
            {
                mismatchDetail = $"prototype '{proto}': live={liveCount}, scratch={(scratch.TryGetValue(proto, out var sc) ? sc.ToString() : "0")}";
                return false;
            }
        }

        foreach (var (proto, scratchCount) in scratch)
        {
            if (!live.ContainsKey(proto))
            {
                mismatchDetail = $"prototype '{proto}': live=0, scratch={scratchCount}";
                return false;
            }
        }

        mismatchDetail = string.Empty;
        return true;
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
    /// Removes every component on <see cref="StoreStripList"/> present on
    /// <paramref name="gridUid"/>, keeping a deep copy of each (via
    /// <see cref="ISerializationManager.CreateCopy{T}"/> — the same mechanism
    /// <c>PolymorphSystem</c>/<c>CloningSystem</c> use to snapshot a component) rather
    /// than the live, now-removed instance, so a refused/aborted store can restore its
    /// field data faithfully. Runs in PREP, before <c>TrySaveGrid</c>, so the stripped
    /// components never enter the serialized yaml.
    /// </summary>
    private List<IComponent> StripListedComponents(EntityUid gridUid)
    {
        var stripped = new List<IComponent>();

        foreach (var type in StoreStripList)
        {
            if (!EntityManager.TryGetComponent(gridUid, type, out var comp))
                continue;

            stripped.Add(_serialization.CreateCopy(comp, notNullableOverride: true));
            RemComp(gridUid, comp);
        }

        return stripped;
    }

    /// <summary>
    /// Restores every component <see cref="StripListedComponents"/> stripped back onto
    /// <paramref name="gridUid"/>, from the held deep copies — a bare EnsureComp of a
    /// fresh instance would come back empty, so this re-adds the copy that still holds
    /// the field data captured at strip time.
    /// </summary>
    private void RestoreStrippedComponents(EntityUid gridUid, List<IComponent> stripped)
    {
        foreach (var comp in stripped)
        {
#pragma warning disable CS0618 // Owner is Obsolete for external callers; this IS the component-restore seam.
            comp.Owner = gridUid;
#pragma warning restore CS0618
            AddComp(gridUid, comp, true);
        }
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
