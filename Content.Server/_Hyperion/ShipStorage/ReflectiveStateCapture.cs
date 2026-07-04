// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// Tool-side (ship-storage) reflective capture/restore for component/field state the
/// engine map serializer can't faithfully round-trip. This is NOT a replacement for the
/// engine serializer (that owns the STRUCTURE — which prototypes exist and where). It is
/// the fidelity escape hatch for the STATE the serializer drops: it snapshots a value to a
/// portable <see cref="DataNode"/> and restores it, touching ZERO content — the whole point
/// is that ship storage adapts to the content, not the reverse.
///
/// <para>Strategy: try to let the <see cref="ISerializationManager"/> write the value
/// natively; if it can (primitives, enums, strings, [DataDefinition]s, engine-serializable
/// collections), delegate — that's faithful and free. Only when the manager throws do we
/// decompose ONE level (dictionary / enumerable / object-by-reflection) and recurse, so
/// reflection is bounded to exactly the parts the engine can't reach.</para>
///
/// <para>Some values are structurally un-round-trippable (a <see cref="System.Type"/> key, a
/// boxed <c>object</c> value, a delegate, a native handle). Those return null from
/// <see cref="TryCapture"/> — the caller strips them rather than persisting garbage (they are
/// invariably round-scoped / derived state, bucket B).</para>
/// </summary>
public sealed class ReflectiveStateCapture
{
    private readonly ISerializationManager _serialization;

    // Reflective object mappings tag the concrete type so restore can rebuild the right
    // class even through a base-typed field. Reserved key; real data fields can't collide
    // (it isn't a valid C# member name).
    private const string TypeTag = "$hyperion_type";

    public ReflectiveStateCapture(ISerializationManager serialization)
    {
        _serialization = serialization;
    }

    /// <summary>
    /// Snapshot <paramref name="value"/> to a portable node, or null if it is structurally
    /// uncapturable (caller should strip it). <paramref name="value"/> must be non-null.
    /// </summary>
    public DataNode? TryCapture(object value)
    {
        var type = value.GetType();

        // 1. Let the engine serializer do it if it can — faithful and cheapest.
        try
        {
            return _serialization.WriteValue(type, value, alwaysWrite: true);
        }
        catch
        {
            // Falls through to reflective decomposition. We catch it ourselves, so no
            // error-log spam (unlike the map serializer, which logs then aborts the grid).
        }

        // 2. Types we refuse to decompose: their "fields" are meaningless to persist.
        //    IsAssignableFrom, not ==: a Type instance is really a RuntimeType, a delegate
        //    a concrete subclass, etc. — the base check catches the whole family.
        if (typeof(System.Type).IsAssignableFrom(type)
            || typeof(Delegate).IsAssignableFrom(type)
            || typeof(MemberInfo).IsAssignableFrom(type)
            || type == typeof(object)
            || type == typeof(nint) || type == typeof(nuint) || type.IsPointer)
            return null;

        // 3. Dictionary -> sequence of {k, v} pairs (handles any key type, if capturable).
        if (value is IDictionary dict)
        {
            var seq = new SequenceDataNode();
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is null || CaptureChild(entry.Key) is not { } keyNode)
                    return null;
                if (entry.Value is null)
                    return null; // null dict value: bail rather than half-capture.
                if (CaptureChild(entry.Value) is not { } valNode)
                    return null;
                var pair = new MappingDataNode();
                pair.Add("k", keyNode);
                pair.Add("v", valNode);
                seq.Add(pair);
            }
            return seq;
        }

        // 4. Other enumerable -> sequence of elements.
        if (value is IEnumerable en and not string)
        {
            var seq = new SequenceDataNode();
            foreach (var element in en)
            {
                if (element is null || CaptureChild(element) is not { } elemNode)
                    return null;
                seq.Add(elemNode);
            }
            return seq;
        }

        // 5. Object -> mapping of instance fields (all state, not just [DataField]:
        //    the whole reason we're here is the serializer's field view is unreliable).
        var mapping = new MappingDataNode();
        mapping.Add(TypeTag, new ValueDataNode(type.AssemblyQualifiedName!));
        foreach (var field in InstanceFields(type))
        {
            var fieldValue = field.GetValue(value);
            if (fieldValue is null)
                continue; // absent key => restore leaves the field at its default.
            if (CaptureChild(fieldValue) is not { } node)
                return null;
            mapping.Add(field.Name, node);
        }
        return mapping;

        DataNode? CaptureChild(object child) => TryCapture(child);
    }

    /// <summary>
    /// Rebuild a value of <paramref name="declaredType"/> from a node produced by
    /// <see cref="TryCapture"/>.
    /// </summary>
    public object? Restore(System.Type declaredType, DataNode node)
    {
        // Object mappings carry their concrete type; dictionaries/enumerables use the
        // declared type. Anything else was written natively by the manager.
        if (node is MappingDataNode map && map.TryGet(TypeTag, out var typeNode))
        {
            var concrete = System.Type.GetType(((ValueDataNode)typeNode).Value)!;
            var obj = RuntimeHelpers.GetUninitializedObject(concrete);
            foreach (var field in InstanceFields(concrete))
            {
                if (!map.TryGet(field.Name, out var fieldNode))
                    continue;
                field.SetValue(obj, Restore(field.FieldType, fieldNode));
            }
            return obj;
        }

        if (node is SequenceDataNode seq)
        {
            if (IsDictionary(declaredType, out var keyType, out var valType))
            {
                var dict = (IDictionary)System.Activator.CreateInstance(declaredType)!;
                foreach (var pairNode in seq)
                {
                    var pair = (MappingDataNode)pairNode;
                    var key = Restore(keyType, pair["k"])!;
                    var val = Restore(valType, pair["v"]);
                    dict[key] = val;
                }
                return dict;
            }

            if (IsEnumerable(declaredType, out var elemType))
            {
                var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(elemType);
                var list = (IList)System.Activator.CreateInstance(listType)!;
                foreach (var elemNode in seq)
                    list.Add(Restore(elemType, elemNode));

                if (declaredType.IsArray)
                {
                    var arr = System.Array.CreateInstance(elemType, list.Count);
                    list.CopyTo(arr, 0);
                    return arr;
                }
                // HashSet<T>, List<T>, etc. all take an IEnumerable<T> ctor arg.
                return System.Activator.CreateInstance(declaredType, list) ?? list;
            }
        }

        // Native: hand it back to the manager.
        return _serialization.Read(declaredType, node, notNullableOverride: true);
    }

    private static FieldInfo[] InstanceFields(System.Type type)
    {
        var fields = new System.Collections.Generic.List<FieldInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => !f.IsInitOnly || true)); // include readonly-by-ctor state fields too
        }
        return fields.ToArray();
    }

    private static bool IsDictionary(System.Type type, out System.Type keyType, out System.Type valType)
    {
        var iface = new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IDictionary<,>));
        if (iface != null)
        {
            keyType = iface.GetGenericArguments()[0];
            valType = iface.GetGenericArguments()[1];
            return true;
        }
        keyType = valType = typeof(object);
        return false;
    }

    private static bool IsEnumerable(System.Type type, out System.Type elemType)
    {
        if (type.IsArray)
        {
            elemType = type.GetElementType()!;
            return true;
        }
        var iface = new[] { type }.Concat(type.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>));
        if (iface != null)
        {
            elemType = iface.GetGenericArguments()[0];
            return true;
        }
        elemType = typeof(object);
        return false;
    }
}
