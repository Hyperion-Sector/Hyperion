// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Content.Shared.Atmos;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Sidecar carrying one pipe entity's share of its PipeNet gas across a
/// store/retrieve round-trip (PipeNet.Air lives on the node-group object
/// graph, which the map serializer never touches). Injected at store time
/// (distribute by <see cref="Content.Server.NodeContainer.Nodes.PipeNode.Volume"/>),
/// applied and removed (consume-once) on the first <c>NodeGroupsRebuilt</c> the
/// owner sees after load. The component's own presence is the marker: no
/// startup-time lifestage guard is needed or wanted.
/// </summary>
[RegisterComponent]
public sealed partial class PipeNetGasHolderComponent : Component
{
    [DataField]
    public GasMixture GasMixture = new();
}
