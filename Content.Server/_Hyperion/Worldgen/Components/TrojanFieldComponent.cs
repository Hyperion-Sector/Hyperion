// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System.Numerics;
using Content.Server.Worldgen.Tools;
using Content.Shared.Storage;
using Robust.Shared.Prototypes;

namespace Content.Server._Hyperion.Worldgen.Components;

/// <summary>
///     Round-global trojan loot pockets for the worldgen-v1 belt (design repo Part 3).
///     The inverse of the NF distance carver: instead of thinning debris near points of
///     importance, this marks a few fixed pockets where the debris selector is overridden with a
///     rich loot table, so a handful of learnable spots in the Wall reliably carry jackpot rock.
///
///     Lives on the worldgen map (added via the worldgenConfig bundle). Pocket centers are rolled
///     once at startup and persisted, so a save/reload keeps the same geography under
///     already-spawned rock. Enrich-only by design: it biases <em>what</em> spawns where rock
///     already survives the density thinning; it does not add points (the placer thins before any
///     event fires and offers no add-a-point hook).
/// </summary>
[RegisterComponent]
public sealed partial class TrojanFieldComponent : Component
{
    /// <summary>How many pockets to roll.</summary>
    [DataField]
    public int Count = 3;

    /// <summary>
    ///     World-radius band the pocket centers roll within. Defaults inside the Wall's rich zone
    ///     (9.5-19.5k) so pockets sit in the mining peak and don't bleed into neighbouring bands.
    /// </summary>
    [DataField]
    public Vector2 RadiusRange = new(11000f, 18000f);

    /// <summary>Radius (world tiles) of each pocket's loot-override disc.</summary>
    [DataField]
    public float PocketRadius = 150f;

    /// <summary>
    ///     The rich debris table rolled for points inside a pocket. Kept to a single orGroup so
    ///     exactly one debris proto is chosen per point (the placer spawns one debris per point).
    /// </summary>
    [DataField]
    public List<EntitySpawnEntry> LootTable = new()
    {
        // M-type metal jackpot (radar-loud) plus large precious-metal rock: a pocket reads as a
        // distinct, valuable cluster against the Wall's basalt, which is the point of "learnable".
        new EntitySpawnEntry { PrototypeId = "HyperionAsteroidMType", SpawnProbability = 0.35f, GroupId = "trojan" },
        new EntitySpawnEntry { PrototypeId = "NFAsteroidAndesiteDebrisExtraLarge", SpawnProbability = 0.35f, GroupId = "trojan" },
        new EntitySpawnEntry { PrototypeId = "NFAsteroidAndesiteDebrisLarge", SpawnProbability = 0.30f, GroupId = "trojan" },
    };

    /// <summary>The rolled pocket centers. Populated once at startup; persisted.</summary>
    [DataField]
    public List<TrojanPocket> Pockets = new();

    /// <summary>Guards against re-rolling if the component starts up more than once.</summary>
    [DataField]
    public bool Rolled;

    private EntitySpawnCollectionCache? _cache;

    /// <summary>Cached spawn collection built lazily from <see cref="LootTable"/>.</summary>
    public EntitySpawnCollectionCache CachedLootTable => _cache ??= new EntitySpawnCollectionCache(LootTable);
}

/// <summary>A single rolled trojan pocket: a loot-override disc centered on the sector plane.</summary>
[DataDefinition]
public sealed partial class TrojanPocket
{
    [DataField]
    public Vector2 Center;
}
