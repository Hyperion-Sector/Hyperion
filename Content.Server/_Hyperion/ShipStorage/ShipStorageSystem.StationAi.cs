// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Content.Shared.Mind.Components;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// The one CONTENT-specific store hook, isolated here so it lifts out cleanly when the tool is
/// dropped into a fork that has no Station AI. Everything else in ShipStorage is content-agnostic;
/// this file knows about exactly one thing — the Station AI apparatus — and only because that
/// apparatus deliberately lives OFF the grid.
/// </summary>
public sealed partial class ShipStorageSystem
{
    [Dependency] private readonly SharedContainerSystem _containers = default!;

    /// <summary>
    /// Store sanitization (PREP, before serialize): empties any Station AI core aboard
    /// <paramref name="gridUid"/>. A Station AI's runtime graph hangs off-grid — the core points
    /// at an invisible "eye" entity in null-space (<see cref="StationAiCoreComponent.RemoteEntity"/>),
    /// and the AI brain in the core's mind slot references that same eye — so the map serializer
    /// logs dangling "missing entity" refs on any ship carrying an active AI, which the round-trip
    /// backstop then rejects. We don't garage the AI: delete the vacant brain and the eye and clear
    /// the ref, so the physical core FIXTURE persists empty and re-slottable (drop in a fresh
    /// intellicard on the far side). Same philosophy as the organics gate — minds and their
    /// apparatus don't ride through storage.
    /// <para>
    /// A player-OCCUPIED core is left untouched: deleting it would ghost the player, and refusing
    /// to store a ship with a live AI aboard is the organics gate's call, not this step's. So only
    /// vacant (ghost-role / unoccupied) AI apparatus is ever removed.
    /// </para>
    /// <para>
    /// Unlike the other PREP steps this is intentionally NOT abort-restorable: the brain/eye are
    /// deleted outright. That is harmless because the intended end state IS an empty core, so a
    /// rare post-serialize abort just reaches that state early on a still-live hull.
    /// </para>
    /// </summary>
    private void SanitizeStationAiCores(EntityUid gridUid)
    {
        var query = AllEntityQuery<StationAiCoreComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var core, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            EntityUid? brain = null;
            if (_containers.TryGetContainer(uid, StationAiHolderComponent.Container, out var container)
                && container.ContainedEntities.Count > 0)
            {
                brain = container.ContainedEntities[0];

                // Occupied core: leave it. Deleting a live AI's brain would ghost the player;
                // that refusal belongs to the organics gate, not here.
                if (TryComp<MindContainerComponent>(brain, out var mind) && mind.HasMind)
                    continue;
            }

            // IMMEDIATE deletes (Del, not QueueDel): serialize runs synchronously later this same
            // tick, so a merely-queued brain would still be a grid child when the serializer walks
            // the tree and would re-introduce the dangling ref. Clear the core's eye ref first so
            // the still-live core doesn't briefly point at a deleted entity.
            if (core.RemoteEntity is { } eye)
            {
                core.RemoteEntity = null;
                Dirty(uid, core);
                Del(eye);
            }

            if (brain is { } b)
                Del(b);
        }
    }
}
