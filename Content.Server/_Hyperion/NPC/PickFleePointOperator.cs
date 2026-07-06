// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.Pathfinding;
using Robust.Shared.Map;

namespace Content.Server._Hyperion.NPC;

/// <summary>
/// Hyperion: picks an accessible coordinate that heads <b>away</b> from a threat entity and stores it
/// for a following <see cref="Content.Server.NPC.HTN.PrimitiveTasks.Operators.MoveToOperator"/>. This is
/// the flee primitive for prey mobs (mice).
///
/// It samples several random reachable endpoints (via the pathfinder) and keeps the one whose offset from
/// us projects furthest along the away-from-threat direction, so the mob bolts away instead of choosing a
/// random tile that might run it into the threat. Reuses the tested move/pathfinding pipeline rather than
/// injecting a new steering context.
/// </summary>
public sealed partial class PickFleePointOperator : HTNOperator
{
    [Dependency] private IEntityManager _entManager = default!;
    private PathfindingSystem _pathfinding = default!;
    private SharedTransformSystem _transform = default!;
    private EntityLookupSystem _lookup = default!;

    /// <summary>
    /// Blackboard key holding the threat entity we are fleeing from.
    /// </summary>
    [DataField]
    public string ThreatKey = "Target";

    /// <summary>
    /// Blackboard key holding how far out to look for a flee point.
    /// </summary>
    [DataField]
    public string RangeKey = "FleeRange";

    /// <summary>
    /// Where the chosen flee coordinate is written (consumed by MoveToOperator).
    /// </summary>
    [DataField]
    public string TargetCoordinates = "TargetCoordinates";

    /// <summary>
    /// Where the pathfinding result for the chosen point is stored (re-used by MoveToOperator so it does
    /// not re-path).
    /// </summary>
    [DataField]
    public string PathfindKey = NPCBlackboard.PathfindKey;

    /// <summary>
    /// How many random reachable endpoints to sample before choosing the best one. More samples give a
    /// cleaner flee direction at the cost of more pathfinding per decision.
    /// </summary>
    [DataField]
    public int Samples = 4;

    /// <summary>
    /// How strongly to prefer flee points the threat cannot easily reach (differential reachability).
    /// 0 = pure away-from-threat distance. Above 0, each candidate is scored by how far the threat would
    /// have to travel to reach it <b>using the threat's own collision profile</b>; a point the threat
    /// cannot path to at all (e.g. under a table or through a door a small mob squeezes through but the
    /// predator can't) is treated as an ideal refuge. Costs one extra pathfind per sample.
    /// </summary>
    [DataField]
    public float CoverWeight;

    /// <summary>
    /// How strongly to prefer flee points hugging walls/furniture (thigmotaxis). Above 0, each candidate
    /// is scored by how many anchored structures sit next to it, so a panicking mouse skirts edges and
    /// dives behind cover instead of bolting across open floor.
    /// </summary>
    [DataField]
    public float WallHugWeight;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _pathfinding = sysManager.GetEntitySystem<PathfindingSystem>();
        _transform = sysManager.GetEntitySystem<SharedTransformSystem>();
        _lookup = sysManager.GetEntitySystem<EntityLookupSystem>();
    }

    /// <inheritdoc/>
    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        // We need the threat's position to know which way "away" is.
        if (!blackboard.TryGetValue<EntityUid>(ThreatKey, out var threat, _entManager) ||
            !_entManager.TryGetComponent<TransformComponent>(threat, out var threatXform) ||
            !_entManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
        {
            return (false, null);
        }

        var maxRange = blackboard.GetValueOrDefault<float>(RangeKey, _entManager);

        if (maxRange == 0f)
            maxRange = 8f;

        var ownerPos = _transform.GetWorldPosition(ownerXform);
        var threatPos = _transform.GetWorldPosition(threatXform);
        var awayDir = ownerPos - threatPos;

        // Threat is right on top of us, so any direction is an improvement.
        awayDir = awayDir.LengthSquared() < 0.001f ? new Vector2(1f, 0f) : awayDir.Normalized();

        PathResultEvent? bestPath = null;
        EntityCoordinates bestCoords = default;
        var bestScore = float.NegativeInfinity;

        var flags = _pathfinding.GetFlags(blackboard);

        for (var i = 0; i < Samples; i++)
        {
            cancelToken.ThrowIfCancellationRequested();

            var path = await _pathfinding.GetRandomPath(owner, maxRange, cancelToken, flags: flags);

            if (path.Result != PathResult.Path || path.Path.Count == 0)
                continue;

            var endCoords = path.Path.Last().Coordinates;
            var endPos = _transform.ToMapCoordinates(endCoords).Position;

            // Base score = how far this endpoint lies in the away-from-threat direction.
            var score = Vector2.Dot(endPos - ownerPos, awayDir);

            // Cover bonus: how hard is this point for the threat to reach, in ITS collision profile?
            // A point the predator cannot path to at all is a perfect refuge (under a table, past a door
            // it can't fit through). GetPathDistance pathfinds from the threat using the threat's own mask.
            if (CoverWeight > 0f)
            {
                var threatReach = await _pathfinding.GetPathDistance(threat, endCoords, maxRange * 3f, cancelToken);
                score += CoverWeight * (threatReach ?? maxRange * 4f);
            }

            // Thigmotaxis: prefer points tucked against walls/furniture over open floor.
            if (WallHugWeight > 0f)
                score += WallHugWeight * CountAdjacentAnchored(endCoords);

            if (score <= bestScore)
                continue;

            bestScore = score;
            bestPath = path;
            bestCoords = endCoords;
        }

        // Nothing reachable this pass. Let a lower-priority branch (or the next replan) handle it.
        if (bestPath == null)
            return (false, null);

        return (true, new Dictionary<string, object>()
        {
            { TargetCoordinates, bestCoords },
            { PathfindKey, bestPath },
        });
    }

    /// <summary>Counts anchored structures (walls, tables, machines) hugging a candidate point.</summary>
    private int CountAdjacentAnchored(EntityCoordinates coords)
    {
        var map = _transform.ToMapCoordinates(coords);
        var ents = new HashSet<Entity<TransformComponent>>();
        _lookup.GetEntitiesInRange(map, 1.2f, ents);

        var count = 0;
        foreach (var ent in ents)
        {
            if (ent.Comp.Anchored)
                count++;
        }

        return count;
    }
}

