using Content.Server.Administration.Logs;
using Content.Server.Cargo.Systems;
using Content.Server.Storage.Components;
using Content.Shared.Database;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using static Content.Shared.Storage.EntitySpawnCollection;

namespace Content.Server.Storage.EntitySystems
{
    public sealed partial class SpawnItemsOnUseSystem : EntitySystem
    {
        [Dependency] private IRobustRandom _random = default!;
        [Dependency] private IAdminLogManager _adminLogger = default!;
        [Dependency] private SharedHandsSystem _hands = default!;
        [Dependency] private IPrototypeManager _proto = default!; // Hyperion: invalidate the loot-price cache on prototype reload
        [Dependency] private PricingSystem _pricing = default!;
        [Dependency] private SharedAudioSystem _audio = default!;
        [Dependency] private SharedTransformSystem _transform = default!;

        // Hyperion: cache the accurate (spawn-based) loot value per bag prototype.
        // The true value of a loot bag requires spawning its table and pricing the resulting tree
        // (GetEstimatedPrice can't see nested or contained loot). That's correct but expensive, and
        // SpaceCleanupSystem calls GetPrice on every entity every sweep, so a debris field of loot bags
        // re-spawned whole loot tables per tick (measured ~44% of server CPU, 2026-06-29). The expected
        // value is constant per prototype, so we compute it once and reuse it; cleared on prototype reload.
        // Keyed by the bag's prototype id: entities with per-instance-overridden Items (rare for loot bags)
        // resolve to the prototype's value, which is an acceptable approximation for that edge case.
        private readonly Dictionary<string, double> _lootPriceCache = new();

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<SpawnItemsOnUseComponent, UseInHandEvent>(OnUseInHand);
            SubscribeLocalEvent<SpawnItemsOnUseComponent, PriceCalculationEvent>(CalculatePrice, before: new[] { typeof(PricingSystem) });

            _proto.PrototypesReloaded += OnPrototypesReloaded; // Hyperion: keep the loot-price cache fresh
        }

        public override void Shutdown()
        {
            base.Shutdown();

            _proto.PrototypesReloaded -= OnPrototypesReloaded; // Hyperion
        }

        // Hyperion: a prototype change can move a loot table's value, so drop the cache and let it rebuild lazily.
        private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
        {
            _lootPriceCache.Clear();
        }

        private void CalculatePrice(EntityUid uid, SpawnItemsOnUseComponent component, ref PriceCalculationEvent args)
        {
            // Hyperion: the loot value is constant per bag prototype, so cache it and skip the spawn-and-price
            // work on repeat lookups (the SpaceCleanupSystem hot path). See the cache field note above.
            var protoId = MetaData(uid).EntityPrototype?.ID;
            if (protoId != null && _lootPriceCache.TryGetValue(protoId, out var cached))
            {
                args.Price += cached;
                args.Handled = true;
                return;
            }

            var ungrouped = CollectOrGroups(component.Items, out var orGroups);
            var price = 0.0;

            foreach (var entry in ungrouped)
            {
                var protUid = Spawn(entry.PrototypeId, MapCoordinates.Nullspace);

                // Calculate the average price of the possible spawned items
                price += _pricing.GetPrice(protUid) * entry.SpawnProbability * entry.GetAmount(getAverage: true);

                EntityManager.DeleteEntity(protUid);
            }

            foreach (var group in orGroups)
            {
                foreach (var entry in group.Entries)
                {
                    var protUid = Spawn(entry.PrototypeId, MapCoordinates.Nullspace);

                    // Calculate the average price of the possible spawned items
                    price += _pricing.GetPrice(protUid) *
                                  (entry.SpawnProbability / group.CumulativeProbability) *
                                  entry.GetAmount(getAverage: true);

                    EntityManager.DeleteEntity(protUid);
                }
            }

            // Hyperion: cache so the cleanup hot path doesn't re-spawn the table on the next sweep.
            if (protoId != null)
                _lootPriceCache[protoId] = price;

            args.Price += price;
            args.Handled = true;
        }

        private void OnUseInHand(EntityUid uid, SpawnItemsOnUseComponent component, UseInHandEvent args)
        {
            if (args.Handled)
                return;

            // If starting with zero or less uses, this component is a no-op
            if (component.Uses <= 0)
                return;

            var coords = Transform(args.User).Coordinates;
            var spawnEntities = GetSpawns(component.Items, _random);
            EntityUid? entityToPlaceInHands = null;

            foreach (var proto in spawnEntities)
            {
                entityToPlaceInHands = SpawnAtPosition(proto, coords); // Frontier: Spawn<SpawnAtPosition
                _adminLogger.Add(LogType.EntitySpawn, LogImpact.Low, $"{ToPrettyString(args.User)} used {ToPrettyString(uid)} which spawned {ToPrettyString(entityToPlaceInHands.Value)}");
            }

            // The entity is often deleted, so play the sound at its position rather than parenting
            if (component.Sound != null)
                _audio.PlayPvs(component.Sound, coords);

            component.Uses--;

            // Delete entity only if component was successfully used
            if (component.Uses <= 0)
            {
                // Don't delete the entity in the event bus, so we queue it for deletion.
                // We need the free hand for the new item, so we send it to nullspace.
                _transform.DetachEntity(uid, Transform(uid));
                QueueDel(uid);
            }

            if (entityToPlaceInHands != null)
                _hands.PickupOrDrop(args.User, entityToPlaceInHands.Value);

            args.Handled = true;
        }
    }
}
