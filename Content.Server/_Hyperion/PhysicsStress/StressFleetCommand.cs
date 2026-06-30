// SPDX-License-Identifier: MPL-2.0
using System.Numerics;
using Content.Server.Administration;
using Content.Server.NPC.HTN;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Utility;

namespace Content.Server._Hyperion.PhysicsStress;

/// <summary>
/// The representative combat load of the physics test suite (Gate 2). Loads <c>perSide</c> real ship grids
/// per side in two opposing clusters, strips their onboard AI for a reproducible result, and shoves the
/// clusters together so they ram, split, and shed debris — the dense-ship-melee cost shape (grid-query +
/// constraint-solve + splits + move-churn) the live cost map identified as the prod-crushing scenario.
/// <code>stressfleet &lt;perSide&gt; [ship=ramdronesmall] [speed=30] [separation=120]</code>
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed class StressFleetCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entMan = default!;

    public string Command => "stressfleet";
    public string Description => "Load N ship grids per side and ram the two clusters together to drive realistic combat physics for profiling.";
    public string Help => "stressfleet <perSide> [ship=ramdronesmall] [speed=30] [separation=120]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var perSide) || perSide <= 0)
        {
            shell.WriteLine("Usage: stressfleet <perSide> [ship=ramdronesmall] [speed=30] [separation=120]");
            return;
        }

        var ship = args.Length >= 2 ? args[1] : "ramdronesmall";
        var speed = 30f;
        if (args.Length >= 3 && float.TryParse(args[2], out var sp)) speed = sp;
        var separation = 120f;
        if (args.Length >= 4 && float.TryParse(args[3], out var sep)) separation = sep;

        var xforms = _entMan.System<SharedTransformSystem>();
        var physics = _entMan.System<SharedPhysicsSystem>();
        var loader = _entMan.System<MapLoaderSystem>();

        // Find a map to load onto, headless: use the first grid's map.
        EntityUid? anchorGrid = null;
        var gridQuery = _entMan.EntityQueryEnumerator<MapGridComponent>();
        while (gridQuery.MoveNext(out var g, out _))
        {
            anchorGrid = g;
            break;
        }

        if (anchorGrid is null)
        {
            shell.WriteLine("No grid found to anchor a map.");
            return;
        }

        var mapId = _entMan.GetComponent<TransformComponent>(anchorGrid.Value).MapID;
        var origin = xforms.GetMapCoordinates(anchorGrid.Value).Position;
        var path = new ResPath($"/Maps/_Mono/Shuttles/World/{ship}.yml");

        var spawned = 0;
        for (var side = 0; side < 2; side++)
        {
            var dirX = side == 0 ? 1f : -1f;          // left cluster pushes +x, right pushes -x
            var baseX = origin.X - dirX * separation * 0.5f;

            for (var i = 0; i < perSide; i++)
            {
                var offset = new Vector2(baseX, origin.Y + (i - perSide * 0.5f) * 25f);
                if (!loader.TryLoadGrid(mapId, path, out var grid, offset: offset))
                {
                    shell.WriteLine($"Failed to load ship grid '{ship}' ({path}).");
                    return;
                }

                var gridUid = grid.Value.Owner;
                NeutralizeAi(gridUid);

                var body = _entMan.EnsureComponent<PhysicsComponent>(gridUid);
                physics.SetBodyType(gridUid, BodyType.Dynamic, body: body);
                physics.SetLinearVelocity(gridUid, new Vector2(dirX * speed, 0f), body: body);
                physics.WakeBody(gridUid, body: body);
                spawned++;
            }
        }

        shell.WriteLine($"Spawned {spawned} '{ship}' grids ({perSide}/side) closing at {speed} t/s over {separation}.");
    }

    /// <summary>Removes the onboard HTN pilot from a freshly-loaded grid so its motion is fully imposed.</summary>
    private void NeutralizeAi(EntityUid gridUid)
    {
        var enumerator = _entMan.GetComponent<TransformComponent>(gridUid).ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            if (_entMan.HasComponent<HTNComponent>(child))
                _entMan.RemoveComponent<HTNComponent>(child);
        }
    }
}
