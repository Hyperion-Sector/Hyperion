// SPDX-License-Identifier: MPL-2.0
using System.Numerics;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Physics;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;

namespace Content.Server._Hyperion.PhysicsStress;

/// <summary>
/// Spawns N free physics bodies with random velocity and no AI, to drive the broadphase move-churn
/// (reinsert/body/tick) and contact-find (FindGridsIntersecting / pair query) cost in isolation from the
/// solver — the Class-1 move-churn isolation load of the physics test suite, on a live content server.
/// <code>stressshrapnel &lt;count&gt; [speed=30] [radius=40] [hard=true]</code>
/// hard=true (default): elastic frictionless bouncers — sustained in-area load (move + contact).
/// hard=false: pass-through movers — isolates move + query from the solver but disperses over time.
/// Spawns at random offsets over [radius] on the first grid found, so it works headless (no player).
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed class StressShrapnelCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    public string Command => "stressshrapnel";
    public string Description => "Spawn N no-AI free bodies with random velocity to drive broadphase move/query churn for profiling.";
    public string Help => "stressshrapnel <count> [speed=30] [radius=40] [hard=true]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var count) || count <= 0)
        {
            shell.WriteLine("Usage: stressshrapnel <count> [speed=30] [radius=40] [hard=true]");
            return;
        }

        var speed = 30f; // t/s, well above the broadphase fat-AABB margin so every body escapes its box each tick
        if (args.Length >= 2 && float.TryParse(args[1], out var s))
            speed = s;

        var radius = 40f;
        if (args.Length >= 3 && float.TryParse(args[2], out var r))
            radius = r;

        var hard = true;
        if (args.Length >= 4 && bool.TryParse(args[3], out var h))
            hard = h;

        var xforms = _entMan.System<SharedTransformSystem>();
        var physics = _entMan.System<SharedPhysicsSystem>();
        var fixtures = _entMan.System<FixtureSystem>();

        // Spawn on the first grid found so this works headless (no player attached).
        EntityUid? gridUid = null;
        var gridQuery = _entMan.EntityQueryEnumerator<MapGridComponent>();
        while (gridQuery.MoveNext(out var g, out _))
        {
            gridUid = g;
            break;
        }

        if (gridUid is null)
        {
            shell.WriteLine("No grid found to spawn on.");
            return;
        }

        var origin = xforms.GetMapCoordinates(gridUid.Value);
        var shape = new PhysShapeCircle(0.2f);

        for (var i = 0; i < count; i++)
        {
            var pos = origin.Offset(_random.NextVector2(-radius, radius));
            var uid = _entMan.SpawnEntity(null, pos); // bare entity, no proto, no AI

            var body = _entMan.EnsureComponent<PhysicsComponent>(uid);
            physics.SetBodyType(uid, BodyType.Dynamic, body: body);
            physics.SetFixedRotation(uid, false, body: body);

            fixtures.TryCreateFixture(uid, shape, "shrapnel", hard: hard,
                collisionLayer: (int) CollisionGroup.Impassable,
                collisionMask: (int) CollisionGroup.Impassable,
                body: body);

            if (_entMan.TryGetComponent<FixturesComponent>(uid, out var manager)
                && manager.Fixtures.TryGetValue("shrapnel", out var fixture))
            {
                physics.SetFriction(uid, "shrapnel", fixture, 0f, manager: manager);
                physics.SetRestitution(uid, fixture, 1f, manager: manager);
            }

            physics.SetLinearVelocity(uid, _random.NextAngle().ToVec() * speed, body: body);
            physics.WakeBody(uid, body: body);
            physics.SetSleepingAllowed(uid, body, false); // never sleep even if a collision momentarily zeroes velocity
        }

        shell.WriteLine($"Spawned {count} shrapnel bodies at {speed} t/s over radius {radius} (hard={hard}) on grid {gridUid.Value}.");
    }
}
