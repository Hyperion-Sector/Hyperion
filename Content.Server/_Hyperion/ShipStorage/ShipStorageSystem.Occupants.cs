// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Numerics;
using Content.Shared.Buckle;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Store sanitization (PREP): clears EVERY living mob off the grid before serialize by setting them
/// down on the docked station — loose, buckled, or broken out of a crate/locker/pet-carrier. No living
/// mob can ride the blob: a mind must never be serialized, and a living mob does not round-trip the
/// engine serializer at all (it reloads as a dangling reference that corrupts its container and trips
/// the save-time validation backstop). The one exception is Station AI apparatus (a core / intellicard):
/// a live mind there is the AI's body, so the store refuses rather than force-extracting it; a vacant
/// brain is left for the AI-core sanitize step. The other refusal is a mob to eject aboard an undocked
/// ship (nowhere to set them down).
/// </summary>
public sealed partial class ShipStorageSystem
{
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedBuckleSystem _buckle = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;

    /// <summary>
    /// Tile distances (from the station airlock, inward) tried in order for an ejection spot.
    /// First one that lands on a solid station tile wins; the offset keeps ejectees clear of the
    /// dock doors.
    /// </summary>
    private static readonly float[] EvacDistances = { 2.5f, 3.5f, 4.5f };

    /// <summary>
    /// Sets every living mob aboard <paramref name="gridUid"/> down on the station it is docked to —
    /// players, animals, corpses — loose or broken out of a container. Classifies the whole grid before
    /// moving anyone, so a refusal leaves the world untouched. Returns false — meaning the store must
    /// refuse — when:
    /// <list type="bullet">
    /// <item>a live mind is inside AI apparatus (a core or intellicard), which we must not force-extract; or</item>
    /// <item>there is someone to eject but no dock to set them down on (an undocked ship).</item>
    /// </list>
    /// A vacant (mindless) AI brain is left in place for the AI-core sanitize step. MUST run while still
    /// docked — before <see cref="DockingSystem.UndockDocks"/> — and before the sidecar/strip PREP so
    /// ejectees don't collect sidecars. Not abort-restorable, like the AI-core and undock steps.
    /// </summary>
    private bool TryEjectLooseOccupants(EntityUid gridUid)
    {
        // Classify first, mutate second: a false return must not have moved anyone.
        var eject = new List<(EntityUid Uid, bool Minded)>();
        var query = AllEntityQuery<MobStateComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            var minded = TryComp<MindContainerComponent>(uid, out var mindContainer) && mindContainer.HasMind
                         && _mind.TryGetMind(uid, out _, out var mind) && !_mind.IsCharacterDeadPhysically(mind);

            // Station AI apparatus (a core / intellicard) is the ONE thing we don't pull out: a live
            // mind there is the AI's body (refuse rather than force-extract it), and a vacant mindless
            // brain is left for SanitizeStationAiCores to clear. Either way it is not ejected here.
            if (IsInAiApparatus(uid))
            {
                if (minded)
                    return false;

                continue;
            }

            // Every other living mob comes off — loose OR caged (a pet carrier, a locker, a crate).
            // Nothing living can ride the blob: a mind must never be serialized, and a living mob does
            // not round-trip the engine serializer — it reloads as a dangling reference that corrupts
            // whatever contained it (a caged pet left its carrier permanently jammed). Contained ones
            // are broken out below.
            eject.Add((uid, minded));
        }

        if (eject.Count == 0)
            return true;

        // Nowhere to set them down (undocked): refuse rather than delete or ship them.
        if (FindDockLanding(gridUid) is not { } coords)
            return false;

        foreach (var (uid, minded) in eject)
        {
            // Unbuckle and pull out of any container first so SetCoordinates actually relocates them —
            // anyone left aboard would trip the serializer (and a live mind must never ride the blob).
            _buckle.TryUnbuckle(uid, null, false);
            if (_containers.IsEntityInContainer(uid))
                _containers.TryRemoveFromContainer(uid, true);
            _xform.SetCoordinates(uid, coords);

            // Only a mind has a session to read the popup; animals are moved silently.
            if (minded)
                _popup.PopupEntity(Loc.GetString("ship-storage-ejected-occupant"), uid, uid);
        }

        return true;
    }

    /// <summary>
    /// True if <paramref name="uid"/> sits in the container of Station AI apparatus — a core or an
    /// intellicard, both of which carry <see cref="StationAiHolderComponent"/>. Such a mind is the AI's
    /// body: it can't be dumped on a dock, so the store refuses rather than force-extracting it.
    /// </summary>
    private bool IsInAiApparatus(EntityUid uid)
    {
        return _containers.TryGetContainingContainer(uid, out var container)
            && HasComp<StationAiHolderComponent>(container.Owner);
    }

    /// <summary>
    /// Finds an ejection spot on the station: the first docked port's partner on the station grid,
    /// offset inward past the airlock (see <see cref="EvacDistances"/>). Returns null when the ship
    /// is not docked to any grid.
    /// </summary>
    private EntityCoordinates? FindDockLanding(EntityUid gridUid)
    {
        foreach (var dock in _docking.GetDocks(gridUid))
        {
            if (!dock.Comp.Docked || dock.Comp.DockedWith is not { } partner)
                continue;

            var partnerXform = Transform(partner);
            if (partnerXform.GridUid is not { } stationGrid
                || !TryComp<MapGridComponent>(stationGrid, out var gridComp))
                continue;

            var mapId = partnerXform.MapID;
            var dockPos = _xform.GetWorldPosition(partner);
            // A port's world facing points OUT toward its partner (see the weld anchor in
            // DockingSystem.Dock); into the station is the reverse. ToWorldVec is already unit.
            var inward = -_xform.GetWorldRotation(partner).ToWorldVec();

            EntityCoordinates? fallback = null;
            foreach (var dist in EvacDistances)
            {
                var coords = _xform.ToCoordinates(stationGrid, new MapCoordinates(dockPos + inward * dist, mapId));
                fallback ??= coords;
                if (_map.TryGetTileRef(stationGrid, gridComp, coords, out var tile) && !tile.Tile.IsEmpty)
                    return coords;
            }

            // No solid tile along the ray (odd dock geometry); the nearest offset still beats
            // leaving them aboard the blob.
            return fallback;
        }

        return null;
    }
}
