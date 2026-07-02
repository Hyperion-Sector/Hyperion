// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Content.Server.Atmos.EntitySystems;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.NodeContainer;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Applies <see cref="PipeNetGasHolderComponent"/> sidecars back into their
/// pipe's net on <see cref="NodeGroupsRebuilt"/>, then removes the sidecar
/// (consume-once). The sidecar's presence IS the apply condition: no
/// startup-time lifestage guard, and no ordering assumption about when the
/// first rebuild fires relative to load. A player cutting a pipe later
/// forces another NodeGroupsRebuilt on the survivors, but by then the
/// sidecars are gone, so the merge does not re-fire (no gas-dupe exploit).
/// </summary>
public sealed class PipeNetGasRestoreSystem : EntitySystem
{
    [Dependency] private readonly AtmosphereSystem _atmosphere = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PipeNetGasHolderComponent, NodeGroupsRebuilt>(OnNodeGroupsRebuilt);
    }

    private void OnNodeGroupsRebuilt(Entity<PipeNetGasHolderComponent> ent, ref NodeGroupsRebuilt args)
    {
        if (!TryComp<NodeContainerComponent>(ent, out var nodeContainer))
        {
            RemComp<PipeNetGasHolderComponent>(ent);
            return;
        }

        foreach (var node in nodeContainer.Nodes.Values)
        {
            if (node is not PipeNode { NodeGroup: not null } pipe)
                continue;

            // Merge (not overwrite) so a net spanning multiple sidecar-bearing
            // members sums correctly instead of the last one clobbering the rest.
            _atmosphere.Merge(pipe.Air, ent.Comp.GasMixture);
        }

        // Consume-once: remove immediately after applying so the next rebuild
        // (e.g. a player cutting a pipe) does not re-add the stored gas.
        RemComp<PipeNetGasHolderComponent>(ent);
    }
}
