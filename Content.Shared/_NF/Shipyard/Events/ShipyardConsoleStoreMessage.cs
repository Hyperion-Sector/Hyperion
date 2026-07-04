// Hyperion: drydock tab (ship persistence Cycle 5).
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.Events;

/// <summary>
///     Store the ship whose deed is on the inserted target ID card. The server
///     resolves the grid from the card deed, gates the operator against the ship's
///     stamped account-owner, and hands off to the ship-storage pipeline.
/// </summary>
[Serializable, NetSerializable]
public sealed class ShipyardConsoleStoreMessage : BoundUserInterfaceMessage
{
    public ShipyardConsoleStoreMessage()
    {
    }
}
