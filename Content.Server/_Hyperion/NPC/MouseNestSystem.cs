// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0
using Content.Server.NPC.HTN;
using Robust.Shared.Map;

namespace Content.Server._Hyperion.NPC;

/// <summary>
/// Hyperion: stamps a nesting mob's home into its HTN blackboard at spawn, so a ReturnHome branch can
/// steer it back. The key holds the spawn <see cref="EntityCoordinates"/>; the HTN reads it with a plain
/// MoveToOperator.
/// </summary>
public sealed class MouseNestSystem : EntitySystem
{
    /// <summary>Blackboard key holding the mob's home coordinates.</summary>
    public const string HomeKey = "HomeCoordinates";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MouseNestComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<MouseNestComponent> ent, ref MapInitEvent args)
    {
        // Only HTN mobs can act on it, but stamping regardless is harmless.
        if (!TryComp<HTNComponent>(ent, out var htn))
            return;

        htn.Blackboard.SetValue(HomeKey, Transform(ent).Coordinates);
    }
}
