// Hyperion: drydock tab (ship persistence Cycle 5) — console handlers in front of
// the ship-storage pipeline. New Hyperion file placed in the NF namespace so it can
// extend the NF ShipyardSystem partial; the pipeline itself lives in
// Content.Server._Hyperion.ShipStorage.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.BUI;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Robust.Shared.Player;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem
{
    [Dependency] private readonly ShipStorageSystem _shipStorage = default!;

    private async void OnStoreMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleStoreMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        await TryDrydockStore(uid, component, player, (ShipyardConsoleUiKey)args.UiKey);
    }

    /// <summary>
    /// Fills the console's stored-ship cache for the operator's account (async DB
    /// read), then re-pushes the interface state so the drydock tab shows the fresh
    /// list. The cache exists because the synchronous state builder (RefreshState)
    /// can't await the DB.
    /// </summary>
    internal async Task RefreshDrydockState(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        component.CachedStoredShips = new();

        // Empty when no card is inserted (spec) and when the operator has no session.
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true }
            || !TryComp<ActorComponent>(player, out var actor))
            return;

        var rows = await _shipStorage.GetStoredShips(actor.PlayerSession.UserId.UserId);

        // The console (or the operator) may have died during the await.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return;

        component.CachedStoredShips = rows
            // Hide ships that are already live this round (retrieved, or mid-store):
            // the DB row survives a retrieve, but the active-registry gate would refuse
            // a second retrieve, so listing them would only offer a phantom row that
            // fails on click.
            .Where(r => !_shipStorage.IsShipActive(r.ShipGuid))
            .Select(r => new StoredShipInfo(r.ShipGuid, r.ShipName, r.SizeClass))
            .ToList();

        RefreshConsoleUiState(uid, component, player, uiKey);
    }

    /// <summary>
    /// Console-side store gate + hand-off. Resolves the grid from the inserted card's
    /// deed, refuses unless the operator's session IS the ship's stamped account-owner
    /// (<see cref="ShipOwnershipComponent.OwnerUserId"/> — garage ownership follows the
    /// ship's account, never the card holder, so a borrowed deed card can't store or
    /// re-home someone else's ship), then calls the pipeline. On success the card-side
    /// deed is stripped, mirroring the sell path: the grid it pointed at is gone.
    /// Returns null when a console gate refuses (pipeline never entered), else the
    /// pipeline's result.
    /// </summary>
    internal async Task<(ShipStorageResult Result, Guid? ShipId)?> TryDrydockStore(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, ShipyardConsoleUiKey uiKey)
    {
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return null;
        }

        if (!TryComp<ShuttleDeedComponent>(targetId, out var deed) || deed.ShuttleUid is not { Valid: true } shuttleUid)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-deed"));
            PlayDenySound(player, uid, component);
            return null;
        }

        // Garage owner = the ship's stamped account. A ship with no ownership stamp has
        // no garage to file into, so it can't be stored from the console either.
        if (!TryComp<ShipOwnershipComponent>(shuttleUid, out var ownership)
            || !TryComp<ActorComponent>(player, out var actor)
            || ownership.OwnerUserId != actor.PlayerSession.UserId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-store-not-owner"));
            PlayDenySound(player, uid, component);
            return null;
        }

        var result = await _shipStorage.TryStoreShip(shuttleUid, ownership.OwnerUserId.UserId);

        // The DB await yielded: the console/card/operator may be gone. Bail before
        // touching any of them (async void handler — a throw here is unhandled). The
        // store itself already completed or refused; only the UI epilogue is skipped.
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(player))
            return result;

        if (result.Result != ShipStorageResult.Success)
        {
            ConsolePopup(player, Loc.GetString(StoreRefusalLoc(result.Result)));
            PlayDenySound(player, uid, component);
            return result;
        }

        // The grid is gone (pipeline despawns after commit); strip the now-dangling
        // card-side deed, as sell does. The persistent identity lives in the DB row.
        // Guard the card too: it could have been pulled from the slot during the await.
        if (!TerminatingOrDeleted(targetId))
            RemComp<ShuttleDeedComponent>(targetId);
        ConsolePopup(player, Loc.GetString("shipyard-console-store-success"));
        PlayConfirmSound(player, uid, component);

        // The stored list just grew — refresh the drydock tab.
        await RefreshDrydockState(uid, component, player, uiKey);
        return result;
    }

    private async void OnRetrieveMessage(EntityUid uid, ShipyardConsoleComponent component, ShipyardConsoleRetrieveMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        await TryDrydockRetrieve(uid, component, player, args.ShipId, (ShipyardConsoleUiKey)args.UiKey);
    }

    /// <summary>
    /// Console-side retrieve gate + hand-off. The pipeline itself re-checks the DB
    /// row's owner against the operator's account (a spoofed ShipId for someone
    /// else's ship returns null), presents the ship FTL-docked at this console's
    /// station and rebinds the grid-side deed; this handler adds the card side:
    /// a fresh deed minted onto the inserted (deed-free) ID card so the retrieved
    /// ship is immediately flyable. Returns the new grid, or null on refusal.
    /// </summary>
    internal async Task<EntityUid?> TryDrydockRetrieve(EntityUid uid, ShipyardConsoleComponent component, EntityUid player, Guid shipId, ShipyardConsoleUiKey uiKey)
    {
        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-no-idcard"));
            PlayDenySound(player, uid, component);
            return null;
        }

        // One ship per card: a card already carrying a deed can't take the mint.
        if (HasComp<ShuttleDeedComponent>(targetId))
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-already-deeded"));
            PlayDenySound(player, uid, component);
            return null;
        }

        if (!TryComp<ActorComponent>(player, out var actor))
            return null;

        if (_station.GetOwningStation(uid) is not { Valid: true } station)
        {
            ConsolePopup(player, Loc.GetString("shipyard-console-invalid-station"));
            PlayDenySound(player, uid, component);
            return null;
        }

        var grid = await _shipStorage.TryRetrieveShip(shipId, actor.PlayerSession.UserId.UserId, station);
        if (grid is null)
        {
            // Guard the popup: the operator may have disconnected during the await.
            if (!TerminatingOrDeleted(player))
            {
                ConsolePopup(player, Loc.GetString("shipyard-console-retrieve-failed"));
                PlayDenySound(player, uid, component);
            }
            return null;
        }

        // The DB await yielded: the card/operator may be gone. The ship is already
        // docked and registered; skip the card mint rather than throw in async void
        // (recoverable — the owner re-retrieves next round from the blob). Card gone =
        // nothing to mint onto.
        if (TerminatingOrDeleted(targetId) || TerminatingOrDeleted(player))
            return grid;

        MintCardDeed(targetId, grid.Value, player);
        ConsolePopup(player, Loc.GetString("shipyard-console-retrieve-success"));
        PlayConfirmSound(player, uid, component);

        // The stored list just shrank — refresh the drydock tab.
        await RefreshDrydockState(uid, component, player, uiKey);
        return grid;
    }

    /// <summary>
    /// Player-facing reason for a refused store. Every non-success
    /// <see cref="ShipStorageResult"/> maps to a popup so a refusal is never silent.
    /// </summary>
    private static string StoreRefusalLoc(ShipStorageResult result)
    {
        return result switch
        {
            ShipStorageResult.OrganicsAboard => "shipyard-console-store-organics",
            ShipStorageResult.HazardAboard => "shipyard-console-store-hazard",
            _ => "shipyard-console-store-failed",
        };
    }

    /// <summary>
    /// Mints a fresh card-side deed onto <paramref name="targetId"/> for
    /// <paramref name="shuttleUid"/>, mirroring the purchase deed-assign block
    /// (see OnPurchaseMessage): same property assignment, holder back-reference and
    /// grid-deed holder update. Used by retrieve (the old card died with its round)
    /// and by tests standing up post-purchase state.
    /// </summary>
    internal void MintCardDeed(EntityUid targetId, EntityUid shuttleUid, EntityUid player)
    {
        TryComp<ShuttleDeedComponent>(shuttleUid, out var gridDeed);
        var name = gridDeed != null ? GetFullName(gridDeed) : Name(shuttleUid);
        var owner = Name(player).Trim();

        var deed = EnsureComp<ShuttleDeedComponent>(targetId);
        AssignShuttleDeedProperties(deed, shuttleUid, name, owner, purchasedWithVoucher: false);
        deed.DeedHolder = targetId;

        // The grid-side deed tracks its current holder card (purchase leaves it this
        // way; retrieve's rebind cleared the dead one).
        if (gridDeed != null)
            gridDeed.DeedHolder = targetId;
    }
}
