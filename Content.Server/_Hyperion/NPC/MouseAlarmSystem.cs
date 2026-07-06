// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0
using System.Collections.Generic;
using Robust.Shared.Timing;

namespace Content.Server._Hyperion.NPC;

/// <summary>
/// Hyperion: prey alarm propagation. When a skittish mob starts fleeing it calls <see cref="Startle"/>,
/// which latches its own panic timer and spreads the panic to nearby <see cref="SkittishComponent"/> mobs.
/// Those mobs' HTN then scurries them via a StartledPrecondition-gated branch, so they bolt without ever
/// having seen the threat. Scurrying spreads the mobs apart, so the cascade dies out on its own.
/// </summary>
public sealed class MouseAlarmSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>
    /// Latch the mob's panic and ripple it out to nearby skittish mobs.
    /// </summary>
    public void Startle(Entity<SkittishComponent?> mob)
    {
        if (!Resolve(mob.Owner, ref mob.Comp, false))
            return;

        var now = _timing.CurTime;
        Latch(mob.Comp, now);

        var coords = _transform.GetMapCoordinates(mob.Owner);
        var nearby = new HashSet<Entity<SkittishComponent>>();
        _lookup.GetEntitiesInRange(coords, mob.Comp.AlarmRadius, nearby);

        foreach (var other in nearby)
        {
            if (other.Owner == mob.Owner)
                continue;

            Latch(other.Comp, now);
        }
    }

    /// <summary>Is the mob currently startled?</summary>
    public bool IsStartled(Entity<SkittishComponent?> mob)
    {
        return Resolve(mob.Owner, ref mob.Comp, false) && mob.Comp.StartledUntil > _timing.CurTime;
    }

    private void Latch(SkittishComponent comp, TimeSpan now)
    {
        // Refresh-if-longer: an already-more-startled mob keeps its later timer.
        var until = now + TimeSpan.FromSeconds(comp.AlarmSeconds);
        if (until > comp.StartledUntil)
            comp.StartledUntil = until;
    }
}
