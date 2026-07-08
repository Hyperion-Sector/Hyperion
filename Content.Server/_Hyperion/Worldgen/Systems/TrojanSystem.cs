// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Numerics;
using Content.Server._Hyperion.Worldgen.Components;
using Content.Server.Worldgen.Components;
using Content.Server.Worldgen.Systems.Debris;
using Robust.Shared.Random;

namespace Content.Server._Hyperion.Worldgen.Systems;

/// <summary>
///     Rolls the round's trojan loot pockets (see <see cref="TrojanFieldComponent"/>) and, for
///     debris points that land inside one, overrides the band's debris selector with the pocket's
///     rich loot table. Enrich-only: it changes what spawns where rock already survives, it does
///     not add points. The inverse of the NF distance carver, which thins spawns near a point set;
///     this enriches them near a rolled point set instead.
/// </summary>
public sealed class TrojanSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IRobustRandom _random = default!;

    /// <inheritdoc />
    public override void Initialize()
    {
        SubscribeLocalEvent<TrojanFieldComponent, ComponentStartup>(OnFieldStartup);
        SubscribeLocalEvent<TrojanCarverComponent, TryGetPlaceableDebrisFeatureEvent>(OnTrySelectDebris);
    }

    /// <summary>
    ///     Rolls the round's pockets once, when the worldgen config drops the field onto the map.
    /// </summary>
    private void OnFieldStartup(EntityUid uid, TrojanFieldComponent component, ComponentStartup args)
    {
        if (component.Rolled)
            return;

        component.Pockets.Clear();
        for (var i = 0; i < component.Count; i++)
        {
            var radius = component.RadiusRange.X
                + (component.RadiusRange.Y - component.RadiusRange.X) * _random.NextFloat();
            var bearing = _random.NextFloat() * MathF.Tau;

            component.Pockets.Add(new TrojanPocket
            {
                Center = new Vector2(radius * MathF.Cos(bearing), radius * MathF.Sin(bearing)),
            });
        }

        component.Rolled = true;
    }

    private void OnTrySelectDebris(EntityUid uid, TrojanCarverComponent component,
        ref TryGetPlaceableDebrisFeatureEvent args)
    {
        // Pockets live on the map, not the chunk. Chunks sit in nullspace, so the map comes off the
        // chunk component, not the transform.
        if (!TryComp<WorldChunkComponent>(uid, out var chunk))
            return;

        if (!TryComp<TrojanFieldComponent>(chunk.Map, out var field) || !field.Rolled)
            return;

        // Sector center is the map origin, so the point's polar coords are just its world position.
        if (!InPocket(field, _transform.ToMapCoordinates(args.Coords).Position))
            return;

        // Override unconditionally: whichever band selector ran first, the trojan wins in its
        // pocket. The loot table is a single orGroup, so exactly one proto comes back.
        var rolled = new List<string?>(1);
        field.CachedLootTable.GetSpawns(_random, ref rolled);
        if (rolled.Count > 0 && rolled[0] is { } proto)
            args.DebrisProto = proto;
    }

    /// <summary>
    ///     Whether a world position (relative to the sector center at the map origin) falls inside
    ///     any rolled pocket. Pure geometry, split out so pocket membership is testable without the
    ///     worldgen chunk/map plumbing.
    /// </summary>
    public static bool InPocket(TrojanFieldComponent field, Vector2 pos)
    {
        var radiusSquared = field.PocketRadius * field.PocketRadius;
        foreach (var pocket in field.Pockets)
        {
            if (Vector2.DistanceSquared(pos, pocket.Center) <= radiusSquared)
                return true;
        }

        return false;
    }
}
