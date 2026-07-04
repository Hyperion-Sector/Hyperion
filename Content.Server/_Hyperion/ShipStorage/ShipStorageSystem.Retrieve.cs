// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Server._NF.SectorServices;
using Content.Server._NF.ShuttleRecords;
using Content.Server.Gravity;
using Content.Server.Power.EntitySystems;
using Content.Server._NF.Station.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared._Hyperion.CCVar;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared._NF.ShuttleRecords;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Maps;
using Content.Shared.Station.Components;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Retrieve half of ship storage (Cycle 4): checksum-verified blob fetch, load onto
/// the shared shipyard staging map, rehydration + identity rebind, then FTL-dock
/// presentation at the requesting station.
/// </summary>
public sealed partial class ShipStorageSystem
{
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly SharedShipRepairSystem _shipRepair = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ShuttleConsoleLockSystem _consoleLock = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly ShuttleRecordsSystem _shuttleRecords = default!;
    [Dependency] private readonly SectorServiceSystem _sectorService = default!;

    /// <summary>
    /// Retrieves the ship identified by <paramref name="shipId"/> for
    /// <paramref name="ownerUserId"/> and presents it at <paramref name="stationUid"/>
    /// (the requesting station: retrieve always happens at a station terminal, RFC).
    /// Verifies the blob checksum with fallback over prior revisions; loads onto the
    /// shared shipyard map; rehydrates sidecar state; FTL-docks to the station's
    /// largest grid (proximity fallback inside TryFTLDock). Returns the new grid, or
    /// null if the ship is unknown, owned by someone else, already active, no revision
    /// verifies, or the station is not a valid dock target.
    /// </summary>
    public async Task<EntityUid?> TryRetrieveShip(Guid shipId, Guid ownerUserId, EntityUid stationUid)
    {
        // Requesting-station validation. Checked before the registry gate and the
        // reservation write: a refusal here is free and needs no cleanup.
        if (!TryComp<StationDataComponent>(stationUid, out var stationData)
            || _station.GetLargestGrid((stationUid, stationData)) is not { } targetGrid)
        {
            Log.Warning($"Ship {shipId}: retrieve refused, {ToPrettyString(stationUid)} is not a valid requesting station.");
            return null;
        }

        // Active-ship registry gate (RFC "Anti-abuse", LOCKED): refuse if this ShipId
        // already has a live grid registered this round, OR if a retrieve of it is
        // already in flight (the reservation set just below). Checked first, before
        // any DB work, so a refusal here is cheap.
        if (_activeShips.TryGetValue(shipId, out var liveGrid))
        {
            // EntityUid.Invalid is the in-flight reservation sentinel written below —
            // it must refuse here too, so it's checked BEFORE Exists(), not folded
            // into it: Exists(Invalid) is false, so treating it like an ordinary
            // dead-grid entry would (wrongly) read it as stale and let a second,
            // concurrently-racing retrieve through.
            if (liveGrid == EntityUid.Invalid)
                return null;

            // Belt-and-suspenders: a stale entry that somehow survived its grid's
            // deletion (OnEntityTerminating is expected to have already cleared it).
            if (Exists(liveGrid))
                return null;
        }

        // Reserve the ShipId SYNCHRONOUSLY here, in the same block as the gate check
        // above and strictly before the first await — this closes the concurrent
        // double-retrieve TOCTOU (red-team fix #2). Two TryRetrieveShip calls issued
        // back-to-back (double-click, two racing sessions) before either resumes past
        // its first await previously could BOTH pass the gate, since neither had
        // registered yet; the second call's gate check above now finds this sentinel
        // and refuses. Every exit below MUST release the reservation on failure (see
        // the finally block) or a bad owner / missing row / checksum exhaustion /
        // load failure would leave this ShipId permanently blocked for the rest of
        // the round; a success overwrites the sentinel with the real grid uid before
        // returning, and the finally block leaves that alone.
        _activeShips[shipId] = EntityUid.Invalid;

        try
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

                using var reader = new StreamReader(new MemoryStream(yamlBytes), Encoding.UTF8);
                if (_shipyard.TryAddGridFromReader(reader, $"ship_storage/{shipId}", out var grid))
                {
                    // A ship blob without a ShuttleComponent cannot dock or fly: treat
                    // as a corrupt/exotic revision and fall back to the previous one
                    // (spec section 4). TryLoadGrid cleaned up nothing here — the grid
                    // loaded fine — so delete it explicitly. NEVER delete the shared
                    // shipyard map (the bare-map predecessor deleted its per-retrieve
                    // map; that path is retired).
                    if (!TryComp<ShuttleComponent>(grid.Value, out var shuttle))
                    {
                        Del(grid.Value);
                        Log.Error($"Ship {shipId} revision {revision} has no ShuttleComponent; trying previous revision.");
                        continue;
                    }

                    // The station was validated before the DB awaits; those awaits are
                    // the one window where it (or its largest grid) can die. Everything
                    // from here to the dock is synchronous, so this single re-check is
                    // sufficient: refuse and scrap the freshly loaded grid rather than
                    // strand an active-registered ship on the shared staging map.
                    if (!Exists(targetGrid))
                    {
                        Del(grid.Value);
                        Log.Warning($"Ship {shipId}: requesting station's dock target died mid-retrieve; retrieve refused.");
                        return null;
                    }

                    try
                    {
                        // Rehydration pass (RFC retrieve flow): sidecar-carried state that
                        // the map serializer can't reach gets reapplied once the grid has
                        // fully materialized. Fidelity state first (the general net —
                        // vending stock, market inventory, …, re-applied from each entity's
                        // ShipCapturedStateComponent), then damage, then the repair baseline,
                        // then the ownership touch-up, then deed/lock rebind, station recreate
                        // and records — in this order, all before the dock presentation.
                        _fidelity.RestoreCaptured(grid.Value);

                        // Transient FTL scrub: blobs stored before the store-side FTL strip
                        // (and belt-and-suspenders for any that slip through) can carry a stale
                        // FTLComponent that leaves the reborn ship stuck mid-FTL-lifecycle with
                        // the shuttle system erroring every tick. Drop it before the FTL-dock.
                        if (HasComp<FTLComponent>(grid.Value))
                            RemComp<FTLComponent>(grid.Value);

                        // Store-in-progress scrub: blobs written before the marker became
                        // [UnsavedComponent] carry it (it is stamped before serialize by design),
                        // and a ship still wearing it has ALL container insertion blocked aboard —
                        // hands included, so nothing on the ship can be picked up.
                        if (HasComp<ShipStorageInProgressComponent>(grid.Value))
                            RemComp<ShipStorageInProgressComponent>(grid.Value);

                        // Re-fire charged-machine activation (derived state): a gravity generator
                        // pushes gravity onto the grid's GravityComponent only on the charge
                        // activation EDGE (ChargedMachineActivatedEvent). On load its charge/Active
                        // DataFields come back already-full, so the charge loop sees no edge and
                        // never re-pushes; meanwhile the generator's own GravityActive flag is
                        // NON-serialized (so it reads false) and the grid's ComponentInit refresh
                        // has already cleared GravityComponent.Enabled. Net: live generator, no
                        // gravity. Re-raise the activation per generator so its normal handler
                        // re-applies gravity; a genuinely-unpowered generator self-corrects on its
                        // next discharge (ChargedMachineDeactivatedEvent). GravityGeneratorComponent
                        // is [Access]-locked to its own system, so re-raising the event is the
                        // correct seam — we don't touch its state directly.
                        var genQuery = AllEntityQuery<GravityGeneratorComponent, TransformComponent>();
                        while (genQuery.MoveNext(out var genUid, out _, out var genXform))
                        {
                            if (genXform.GridUid != grid.Value)
                                continue;

                            var activated = new ChargedMachineActivatedEvent();
                            RaiseLocalEvent(genUid, ref activated);
                        }

                        RehydrateDamage(grid.Value);

                        // Repair baseline: derived state, stripped at store (Cycle 3);
                        // regenerate against the loaded grid — retrieve fires neither
                        // MapInit nor ShipBought, and ShipRepair is ShipBought's only
                        // subscriber (RFC retrieve flow).
                        _shipRepair.GenerateRepairData(grid.Value);

                        RefreshShipOwnership(grid.Value);

                        // Identity rebind (spec section 3, steps 2-3): deed first, then the
                        // uid-string locks that key off it.
                        _shipyard.RebindDeedForRetrieve(grid.Value, shipId);
                        _consoleLock.RestampShuttleId(grid.Value, grid.Value.ToString());

                        RecreateStation(grid.Value, index);
                        AddShuttleRecord(grid.Value);

                        // Present at the requesting station: the station-death window is
                        // closed by the re-check above, so a false here is genuinely the
                        // no-docking-config proximity fallback (both cases inside TryFTLDock).
                        if (!_shuttle.TryFTLDock(grid.Value, shuttle, targetGrid))
                            Log.Warning($"Ship {shipId}: no docking config at {ToPrettyString(stationUid)}; presented via proximity fallback.");

                        // Overwrite the in-flight reservation with the real grid uid (ship
                        // is now "flying"; sequential re-retrieve must refuse).
                        _activeShips[shipId] = grid.Value;
                        return grid.Value;
                    }
                    catch
                    {
                        // A throw mid-pipeline would otherwise orphan a live grid on the
                        // shared staging map while the finally releases the reservation —
                        // exactly the dupe window the registry exists to close. Scrap the
                        // grid, then let the failure propagate loudly.
                        Del(grid.Value);
                        throw;
                    }
                }

                Log.Error($"Ship {shipId} revision {revision} passed checksum but failed to load.");
                return null;
            }

            Log.Error($"Ship {shipId}: no stored revision passed verification; retrieve refused.");
            return null;
        }
        finally
        {
            // Release the reservation on every failure exit above — it's still the
            // Invalid sentinel there, never overwritten. A success already replaced
            // it with the real grid uid, so this is a no-op on that path (the
            // TryGetValue guard below skips the Remove rather than clobbering a
            // just-registered live ship's entry).
            if (_activeShips.TryGetValue(shipId, out var current) && current == EntityUid.Invalid)
                _activeShips.Remove(shipId);
        }
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
    /// ShipOwnershipComponent rides the blob (all [DataField]s), but
    /// LastStatusChangeTime is round-scoped absolute time — a previous round's clock
    /// would feed the offline-deletion timer garbage. Refresh it and re-derive online
    /// state from the live session list rather than trusting the stored flag.
    /// </summary>
    private void RefreshShipOwnership(EntityUid gridUid)
    {
        if (!TryComp<ShipOwnershipComponent>(gridUid, out var ownership))
            return;

        ownership.IsOwnerOnline = _player.TryGetSessionById(ownership.OwnerUserId, out _);
        ownership.LastStatusChangeTime = _timing.CurTime;
        Dirty(gridUid, ownership);
    }

    /// <summary>
    /// A ship is its own station only when its vessel has a matching GameMapPrototype
    /// (RFC station model: persist the ship, RECREATE the round-scoped station).
    /// Preserves the ship's (possibly player-renamed) name over the proto's name
    /// generator by passing it to InitializeNewStation directly. A blob with no vessel
    /// proto retrieves stationless; its serialized StationMember (whose Station uid
    /// dangled to Invalid on load) is removed rather than left lying to consumers.
    /// </summary>
    private void RecreateStation(EntityUid gridUid, ShipStorageRecord index)
    {
        if (string.IsNullOrEmpty(index.VesselProto)
            || !_protoMan.TryIndex<GameMapPrototype>(index.VesselProto, out var stationProto)
            || !stationProto.Stations.TryGetValue(index.VesselProto, out var stationConfig))
        {
            Log.Info($"Ship {index.ShipGuid}: no station recreate (vessel proto '{index.VesselProto}').");
            RemComp<StationMemberComponent>(gridUid);
            return;
        }

        var station = _station.InitializeNewStation(stationConfig, new[] { gridUid }, Name(gridUid));
        var vesselInfo = EnsureComp<ExtraShuttleInformationComponent>(station);
        vesselInfo.Vessel = index.VesselProto;
    }

    /// <summary>
    /// Re-lists the ship in the sector shuttle records: the store is keyed by
    /// NetEntity, which was reassigned on load, and the old record died with its
    /// round — this is an add, not an edit (RFC retrieve flow). PurchasePrice 0:
    /// a retrieve is not a transaction, the record is a registry listing.
    /// </summary>
    private void AddShuttleRecord(EntityUid gridUid)
    {
        if (!TryComp<ShuttleDeedComponent>(gridUid, out var deed))
            return;

        // The records store rides the sector-services entity; a round with no sector
        // host (bare integration pairs, exotic setups) has nowhere to file — skip
        // rather than let AddRecord throw on the Invalid uid and kill the retrieve.
        if (!Exists(_sectorService.GetServiceEntity()))
            return;

        _shuttleRecords.AddRecord(new ShuttleRecord(
            name: deed.ShuttleName ?? string.Empty,
            suffix: deed.ShuttleNameSuffix ?? string.Empty,
            ownerName: deed.ShuttleOwner ?? string.Empty,
            entityUid: GetNetEntity(gridUid),
            purchasedWithVoucher: deed.PurchasedWithVoucher,
            purchasePrice: 0));
    }
}
