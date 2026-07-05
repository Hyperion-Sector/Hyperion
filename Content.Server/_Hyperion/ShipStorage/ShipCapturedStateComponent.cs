// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Tool-owned sidecar: rides an entity through the engine grid serialize/reload carrying the
/// component/field state the serializer can't faithfully write. Populated at store (the
/// original fields are cleared so the serializer doesn't choke), consumed and removed at
/// retrieve (fields re-applied over the reborn entity). Because it lives ON the entity, no
/// separate id-correlation is needed — the serializer preserves it alongside its entity, and
/// on load each entity already carries its own captured state.
///
/// <para>Content-agnostic: keyed by "ComponentType|FieldName", value is the captured state as
/// a base64-of-YAML blob (base64 sidesteps YAML-in-YAML escaping). Nothing here is specific to
/// vending machines or any other content — the same mechanism serves whatever the audit finds.</para>
/// </summary>
[RegisterComponent]
public sealed partial class ShipCapturedStateComponent : Component
{
    [DataField]
    public Dictionary<string, string> Fields = new();
}
