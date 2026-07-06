// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0
using Content.Server.NPC;
using Content.Server.NPC.HTN.Preconditions;

namespace Content.Server._Hyperion.NPC;

/// <summary>
/// Hyperion: met while the mob is startled (recently alarmed by a fleeing neighbour). Gates the panic
/// scurry branch so alarmed mobs bolt even without a threat of their own in sight.
/// </summary>
public sealed partial class StartledPrecondition : HTNPrecondition
{
    [Dependency] private IEntityManager _entManager = default!;
    private MouseAlarmSystem _alarm = default!;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _alarm = sysManager.GetEntitySystem<MouseAlarmSystem>();
    }

    public override bool IsMet(NPCBlackboard blackboard)
    {
        return blackboard.TryGetValue<EntityUid>(NPCBlackboard.Owner, out var owner, _entManager)
               && _alarm.IsStartled(owner);
    }
}
