// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Content.Shared._NF.Market;
using Content.Shared.Lathe;
using Content.Shared.VendingMachines;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;

namespace Content.Server._Hyperion.ShipStorage;

/// <summary>
/// The fidelity layer: the serializer owns the ship's STRUCTURE, this owns the STATE it can't
/// faithfully round-trip. At store it walks the grid and, for every populated [DataField] the
/// engine serializer can't write, either CAPTURES it (a reviewed manifest of player-modified
/// state) into a <see cref="ShipCapturedStateComponent"/> sidecar or STRIPS it (everything else
/// — transient / round-scoped state that regenerates); either way it clears the field so the
/// serializer can write the grid. At retrieve it re-applies the captured state over the reborn
/// entities. Content-agnostic and drop-in: touches zero content, and the only fork-specific
/// knob is <see cref="CaptureLeafTypes"/> (the "keep this" allow-list; the safe default for
/// anything unserializable is strip).
/// </summary>
public sealed class ShipStateFidelitySystem : EntitySystem
{
    [Dependency] private readonly ISerializationManager _serialization = default!;

    private ReflectiveStateCapture _capture = default!;

    // The probe context: mirrors the map serializer's ONE extra capability over the base
    // serialization manager — writing EntityUid references — without its logging or uid-map
    // side effects. The base WriteValue can't write an EntityUid (no serializer registered
    // for it standalone), so every field that ultimately touches one (Transform._parent,
    // container graphs, action-entity lists, deed uids) reads as "unserializable" to a naked
    // probe — a false negative, since the map serializer round-trips all of them via exactly
    // this entity context. Probing WITH this context leaves those alone and flags only the
    // GENUINE gaps: types with no serializer anywhere (vending stock, market inventory, …).
    private EntityRefProbeContext _probe = default!;

    // Definitive "this type can't be written" results (structural, so cacheable). Successes are
    // only cached when proven on a non-empty value (an empty collection writes fine even when
    // its element type can't).
    private readonly Dictionary<System.Type, bool> _serializable = new();

    /// <summary>
    /// The reviewed manifest of unserializable state worth PRESERVING (player-modified). Anything
    /// unserializable NOT reaching one of these is stripped. Drop-in: a new fork adds its own
    /// keep-worthy types here; it never edits content.
    /// </summary>
    private static readonly HashSet<System.Type> CaptureLeafTypes = new()
    {
        typeof(VendingMachineInventoryEntry),
        typeof(MarketData),
        typeof(LatheRecipeBatch),
    };

    public override void Initialize()
    {
        base.Initialize();
        _capture = new ReflectiveStateCapture(_serialization);
        _probe = new EntityRefProbeContext();
    }

    /// <summary>
    /// Store step (call BEFORE serialize): capture-or-strip every unserializable populated
    /// [DataField] across the grid so the engine serializer can write it. Mutates live
    /// components; the grid despawns on a successful store, and the caller's abort path is
    /// responsible for restoring on failure (same discipline as the gas/damage sidecars —
    /// pass the returned <see cref="FidelityCapture"/> to <see cref="RestoreSnapshot"/>).
    /// <para>
    /// Unlike the gas/damage sidecars, which copy live state and leave the field intact,
    /// this must CLEAR the live field — the serializer chokes on it otherwise (that's the
    /// whole reason it's captured). So the abort snapshot holds the original in-memory
    /// values (both the captured bucket and the stripped bucket) to put straight back on
    /// failure, no serialize round-trip involved.
    /// </para>
    /// </summary>
    public FidelityCapture CaptureAndStrip(EntityUid grid)
    {
        var capture = new FidelityCapture();

        foreach (var uid in GridTree(grid))
        {
            ShipCapturedStateComponent? sidecar = null;

            foreach (var comp in EntityManager.GetComponents(uid).ToList())
            {
                if (comp is ShipCapturedStateComponent)
                    continue;

                var compType = comp.GetType();
                foreach (var member in DataFields(compType))
                {
                    var value = GetMember(comp, member);
                    if (value == null)
                        continue;

                    var memberType = MemberType(member);
                    if (IsSerializable(memberType, value))
                        continue;

                    if (IsCaptureType(memberType) && _capture.TryCapture(value) is { } node)
                    {
                        if (sidecar == null)
                        {
                            sidecar = EnsureComp<ShipCapturedStateComponent>(uid);
                            capture.Sidecarred.Add(uid);
                        }

                        sidecar.Fields[$"{compType.Name}|{member.Name}"] =
                            System.Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToString()));
                    }

                    // Snapshot the live value BEFORE clearing so an aborted store can put it
                    // back exactly (captured or stripped — both were cleared, both restore).
                    capture.Snapshot.Add((uid, comp, member, value));

                    // Capture or strip, the live field must go so the serializer can write the grid.
                    ClearMember(comp, member, memberType);
                    Dirty(uid, comp);
                }
            }
        }

        return capture;
    }

    /// <summary>
    /// Abort-path restore (call from the store's failure branch): puts every field
    /// <see cref="CaptureAndStrip"/> cleared back to its original live value and removes
    /// the sidecars it added, leaving the still-live ship exactly as usable as before PREP
    /// touched it. Operates on the SAME live entities (no serialize round-trip); contrast
    /// <see cref="RestoreCaptured"/>, which rebuilds state from the sidecar onto reborn
    /// entities on the retrieve path.
    /// </summary>
    public void RestoreSnapshot(FidelityCapture capture)
    {
        foreach (var (uid, comp, member, original) in capture.Snapshot)
        {
            SetMember(comp, member, original);
            Dirty(uid, comp);
        }

        foreach (var uid in capture.Sidecarred)
            RemComp<ShipCapturedStateComponent>(uid);
    }

    /// <summary>
    /// Retrieve step (call AFTER the grid is reloaded): re-apply captured state over the reborn
    /// entities, then remove the sidecars.
    /// </summary>
    public void RestoreCaptured(EntityUid grid)
    {
        foreach (var uid in GridTree(grid).ToList())
        {
            if (!TryComp<ShipCapturedStateComponent>(uid, out var sidecar))
                continue;

            foreach (var (key, b64) in sidecar.Fields)
            {
                var sep = key.IndexOf('|');
                var compName = key[..sep];
                var fieldName = key[(sep + 1)..];

                var comp = EntityManager.GetComponents(uid).FirstOrDefault(c => c.GetType().Name == compName);
                if (comp == null)
                    continue;
                var member = DataFields(comp.GetType()).FirstOrDefault(m => m.Name == fieldName);
                if (member == null)
                    continue;

                var yaml = Encoding.UTF8.GetString(System.Convert.FromBase64String(b64));
                using var reader = new StringReader(yaml);
                var node = DataNodeParser.ParseYamlStream(reader).First().Root;
                var value = _capture.Restore(MemberType(member), node);
                SetMember(comp, member, value);
                Dirty(uid, comp);
            }

            RemComp<ShipCapturedStateComponent>(uid);
        }
    }

    private bool IsSerializable(System.Type type, object value)
    {
        if (_serializable.TryGetValue(type, out var cached))
            return cached;

        try
        {
            // Probe WITH the entity-ref context so EntityUid-bearing fields (which the map
            // serializer round-trips and a naked probe would falsely reject) write cleanly;
            // only a type with no serializer anywhere still throws below.
            _serialization.WriteValue(type, value, alwaysWrite: true, context: _probe);
            // Only cache a success if it was proven on a non-empty value.
            if (value is not ICollection { Count: 0 })
                _serializable[type] = true;
            return true;
        }
        catch (System.InvalidOperationException e) when (e.Message.Contains("No data definition found"))
        {
            _serializable[type] = false;
            return false;
        }
        catch
        {
            return true; // some other failure — not a serializability gap, leave it to the serializer.
        }
    }

    private static bool IsCaptureType(System.Type type)
    {
        type = System.Nullable.GetUnderlyingType(type) ?? type;
        if (CaptureLeafTypes.Contains(type))
            return true;
        if (type.IsArray)
            return IsCaptureType(type.GetElementType()!);
        return type.IsGenericType && type.GetGenericArguments().Any(IsCaptureType);
    }

    private IEnumerable<EntityUid> GridTree(EntityUid grid)
    {
        var stack = new Stack<EntityUid>();
        stack.Push(grid);
        while (stack.Count > 0)
        {
            var uid = stack.Pop();
            yield return uid;
            var xform = Transform(uid);
            var children = xform.ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push(child);
        }
    }

    private static IEnumerable<MemberInfo> DataFields(System.Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var m in t.GetFields(flags).Cast<MemberInfo>().Concat(t.GetProperties(flags)))
            {
                var attr = m.GetCustomAttribute<DataFieldBaseAttribute>();
                if (attr != null && attr.CustomTypeSerializer == null)
                    yield return m;
            }
        }
    }

    private static System.Type MemberType(MemberInfo m) =>
        m is FieldInfo fi ? fi.FieldType : ((PropertyInfo)m).PropertyType;

    private static object? GetMember(object obj, MemberInfo m) =>
        m is FieldInfo fi ? fi.GetValue(obj) : ((PropertyInfo)m).GetValue(obj);

    private static void SetMember(object obj, MemberInfo m, object? value)
    {
        if (m is FieldInfo fi)
            fi.SetValue(obj, value);
        else if (m is PropertyInfo pi && pi.CanWrite)
            pi.SetValue(obj, value);
    }

    private static void ClearMember(object obj, MemberInfo m, System.Type type)
    {
        object? cleared;
        if (type.IsValueType)
            cleared = System.Activator.CreateInstance(type);
        else if (typeof(IEnumerable).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(System.Type.EmptyTypes) != null)
            cleared = System.Activator.CreateInstance(type); // empty collection, not null: no NRE + serializes fine.
        else
            cleared = null;
        SetMember(obj, m, cleared);
    }
}

/// <summary>
/// The abort-rollback ledger a single <see cref="ShipStateFidelitySystem.CaptureAndStrip"/>
/// call hands back: the live field values it cleared (to put back verbatim on failure) plus
/// the entities it added a <see cref="ShipCapturedStateComponent"/> sidecar to (to strip back
/// off). Discarded on a successful store — the grid despawns, so nothing to undo.
/// </summary>
public sealed class FidelityCapture
{
    /// <summary>Every field cleared, with the exact live value to restore on abort.</summary>
    public readonly List<(EntityUid Uid, IComponent Comp, MemberInfo Member, object? Original)> Snapshot = new();

    /// <summary>Entities that received a fresh sidecar (removed on abort).</summary>
    public readonly List<EntityUid> Sidecarred = new();
}

/// <summary>
/// A minimal serialization context whose only job is to answer "can the MAP serializer write
/// this?" faithfully. The map serializer (<c>EntitySerializer</c>) supplies its own
/// <see cref="EntityUid"/> writer as the write context; the base serialization manager has no
/// standalone EntityUid serializer, so a naked <c>WriteValue</c> probe throws on any field that
/// touches an entity reference (transform parents, container graphs, action lists, deed uids) —
/// a false "unserializable" that would make the fidelity pass strip state the serializer
/// actually round-trips. This context registers a no-op EntityUid writer (returns a dummy node,
/// no logging, no uid mapping), so the probe succeeds on exactly what the map serializer
/// succeeds on and throws only on genuine gaps (a type with no serializer registered anywhere).
/// </summary>
internal sealed class EntityRefProbeContext : ISerializationContext, ITypeWriter<EntityUid>
{
    private static readonly ValueDataNode Stub = new("0");

    public SerializationManager.SerializerProvider SerializerProvider { get; }
    public bool WritingReadingPrototypes => false;

    public EntityRefProbeContext()
    {
        SerializerProvider = new();
        SerializerProvider.RegisterSerializer(this);
    }

    public DataNode Write(
        ISerializationManager serializationManager,
        EntityUid value,
        IDependencyCollection dependencies,
        bool alwaysWrite = false,
        ISerializationContext? context = null) => Stub;
}
