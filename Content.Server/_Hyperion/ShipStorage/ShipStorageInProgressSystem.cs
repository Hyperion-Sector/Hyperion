// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Robust.Shared.Containers;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Cancels container insertion targeting any container whose owner sits on a grid
/// carrying <see cref="ShipStorageInProgressComponent"/>. Broadcast subscription
/// (the engine raises the attempt event on the container owner AND broadcast): the
/// owner is the crate/locker, not the marked grid, so a component-directed
/// subscription can't see the marker. The check is a cheap two-comp lookup and the
/// marker exists only for the brief store window.
/// </summary>
public sealed class ShipStorageInProgressSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ContainerIsInsertingAttemptEvent>(OnInsertAttempt);
    }

    private void OnInsertAttempt(ContainerIsInsertingAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (Transform(args.Container.Owner).GridUid is { } grid
            && HasComp<ShipStorageInProgressComponent>(grid))
        {
            args.Cancel();
        }
    }
}
