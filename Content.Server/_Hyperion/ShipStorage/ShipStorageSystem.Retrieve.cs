// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared._Hyperion.CCVar;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Station.Components;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
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

                    // Rehydration pass (RFC retrieve flow): sidecar-carried state that
                    // the map serializer can't reach gets reapplied once the grid has
                    // fully materialized. Damage, then the repair baseline, then the
                    // ownership touch-up; later cycle-4 tasks add deed/lock rebind,
                    // station recreate and records to this same seam, in this order.
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

                    // Present at the requesting station: instant dock when a config
                    // exists, proximity placement otherwise (both inside TryFTLDock).
                    if (!_shuttle.TryFTLDock(grid.Value, shuttle, targetGrid))
                        Log.Warning($"Ship {shipId}: no docking config at {ToPrettyString(stationUid)}; presented via proximity fallback.");

                    // Overwrite the in-flight reservation with the real grid uid (ship
                    // is now "flying"; sequential re-retrieve must refuse).
                    _activeShips[shipId] = grid.Value;
                    return grid.Value;
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
}
