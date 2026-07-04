// Hyperion: drydock tab (ship persistence Cycle 5).
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>
///     Retrieve the stored ship with this id to the console's station. The server
///     re-checks ownership against the operator's account (the drydock list only
///     ever contained their ships, but the id arrives from the client).
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleRetrieveMessage : BoundUserInterfaceMessage
{
    public readonly Guid ShipId;

    public ShipyardConsoleRetrieveMessage(Guid shipId)
    {
        ShipId = shipId;
    }
}
