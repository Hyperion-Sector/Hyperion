// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using JetBrains.Annotations;
using Robust.Shared.IoC;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Validation;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Shared._Hyperion.Serialization;

/// <summary>
/// Ship-storage identity (<see cref="Content.Shared._NF.Shipyard.Components.ShuttleDeedComponent.ShipId"/>)
/// is a bare <see cref="Guid"/>, but the engine's data-definition machinery has no
/// built-in serializer for it (unlike <c>NetUserId</c>, which self-serializes) — a
/// grid carrying a raw <c>[DataField] Guid?</c> throws
/// "No data definition found for type System.Guid when writing" on the very first
/// <c>TrySaveGrid</c>. <c>[TypeSerializer]</c> auto-registers this content-side (the
/// engine submodule is never touched for this) via the same reflection scan that
/// finds every other type serializer, so the plain <c>[DataField] public Guid?</c>
/// declaration works with no <c>customTypeSerializer</c> needed. Round-trips through
/// <see cref="Guid.ToString()"/> / <see cref="Guid.Parse(string)"/> ("D" format,
/// i.e. <c>Guid.ToString()</c>'s default), matching the shape of the engine's own
/// <c>DateTimeSerializer</c>.
/// </summary>
[TypeSerializer]
public sealed class GuidSerializer : ITypeSerializer<Guid, ValueDataNode>, ITypeCopyCreator<Guid>
{
    public ValidationNode Validate(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        ISerializationContext? context = null)
    {
        return Guid.TryParse(node.Value, CultureInfo.InvariantCulture, out _)
            ? new ValidatedValueNode(node)
            : new ErrorNode(node, "Failed parsing Guid");
    }

    public Guid Read(
        ISerializationManager serializationManager,
        ValueDataNode node,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null,
        ISerializationManager.InstantiationDelegate<Guid>? instanceProvider = null)
    {
        return Guid.Parse(node.Value, CultureInfo.InvariantCulture);
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        Guid value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null)
    {
        return new ValueDataNode(value.ToString());
    }

    [MustUseReturnValue]
    public Guid CreateCopy(
        ISerializationManager serializationManager,
        Guid source,
        IDependencyCollection dependencies,
        SerializationHookContext hookCtx,
        ISerializationContext? context = null)
    {
        return source;
    }
}
