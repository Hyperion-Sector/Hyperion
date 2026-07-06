// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;

namespace Content.Server._Hyperion.NPC;

/// <summary>
/// Hyperion: on execution, startles this mob and ripples the alarm to nearby skittish mobs
/// (<see cref="MouseAlarmSystem"/>). Placed in the flee and scurry branches so panic spreads through a
/// nest. Pure side effect; finishes immediately.
/// </summary>
public sealed partial class RaiseAlarmOperator : HTNOperator
{
    [Dependency] private IEntityManager _entManager = default!;
    private MouseAlarmSystem _alarm = default!;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _alarm = sysManager.GetEntitySystem<MouseAlarmSystem>();
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);
        _alarm.Startle(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Finished;
    }
}
