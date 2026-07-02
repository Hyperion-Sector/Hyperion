// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Content.Shared.Damage;
using Content.Shared.FixedPoint;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Sidecar carrying a damageable entity's <see cref="DamageableComponent.Damage"/>
/// across a store/retrieve round-trip (the field is <c>[DataField(readOnly: true)]</c>,
/// so the map serializer never writes it and a damaged entity would come back
/// pristine). Injected at store time (copies the live damage); applied and removed
/// (consume-once) by an explicit post-load pass in
/// <see cref="ShipStorageSystem.TryRetrieveShip"/>, run after the grid has fully
/// materialized. The component's own presence is the marker: no startup-time
/// lifestage guard is needed or wanted.
/// </summary>
/// <remarks>
/// Stores the raw damage-type dictionary rather than a <see cref="DamageSpecifier"/>
/// directly: <see cref="DamageSpecifier.DamageDict"/> is itself
/// <c>[IncludeDataField(readOnly: true)]</c>, so a DamageSpecifier-typed field would
/// always serialize empty regardless of the containing component's own field
/// attribute. Reconstructed into a DamageSpecifier on apply
/// (<see cref="ShipStorageSystem"/>'s rehydration pass).
/// </remarks>
[RegisterComponent]
public sealed partial class DamageSidecarComponent : Component
{
    [DataField]
    public Dictionary<string, FixedPoint2> DamageDict = new();
}
