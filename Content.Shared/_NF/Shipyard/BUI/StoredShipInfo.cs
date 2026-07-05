// Hyperion: drydock tab (ship persistence Cycle 5).
using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shipyard.BUI;

/// <summary>
///     One stored ship in the drydock tab's retrieve list: the persistent ship id
///     plus the display fields the row renders. Mirrors the hot-index DB row; the
///     client never sees blobs.
/// </summary>
[Serializable, NetSerializable]
public sealed class StoredShipInfo
{
    public Guid ShipId;
    public string Name = string.Empty;
    public int SizeClass;

    public StoredShipInfo(Guid shipId, string name, int sizeClass)
    {
        ShipId = shipId;
        Name = name;
        SizeClass = sizeClass;
    }
}
