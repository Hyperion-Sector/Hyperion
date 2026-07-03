// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Server.Station.Systems;
using Content.Shared.Maps;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Shared scaffolding for ship-storage integration tests: a minimal storable grid
    /// (one floored tile + ShuttleComponent, so retrieve's dock step accepts it) and a
    /// requesting station built from a real fork StationConfig with one dock-target grid.
    /// </summary>
    public static class ShipStorageTestHelpers
    {
        /// <summary>
        /// Vessel id whose GameMapPrototype ships with the fork
        /// (Resources/Prototypes/_Mono/Shipyard/archer.yml). Reused as the real
        /// StationConfig for requesting stations and the station-recreate test.
        /// </summary>
        public const string VesselWithStation = "Archer";

        public static EntityUid CreateStorableGrid(IEntityManager entManager, IMapManager mapManager,
            SharedMapSystem mapSystem, out MapId mapId)
        {
            mapSystem.CreateMap(out mapId);
            var grid = mapManager.CreateGridEntity(mapId);
            mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
            entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid.Owner));
            // Retrieve treats a blob without ShuttleComponent as a load failure; every
            // storable test grid carries one, like every real ship does.
            entManager.EnsureComponent<ShuttleComponent>(grid.Owner);
            return grid.Owner;
        }

        /// <summary>
        /// A bare container-bearing entity plus a loose item, both parented to
        /// <paramref name="gridUid"/>. Spawned at the tile CENTER (0.5, 0.5): a corner
        /// spawn at (0,0) straddles the tile edge and the engine reparents it to the map,
        /// silently taking the entity off-grid (see the Cycle 4 test-harness lesson).
        /// </summary>
        public static (EntityUid Box, EntityUid Item) SpawnContainerAndLooseItem(
            IEntityManager entManager, EntityUid gridUid)
        {
            var coords = new EntityCoordinates(gridUid, new Vector2(0.5f, 0.5f));
            var box = entManager.SpawnEntity(null, coords);
            entManager.System<SharedContainerSystem>().EnsureContainer<Container>(box, "test-container");
            var item = entManager.SpawnEntity(null, coords);
            return (box, item);
        }

        public static EntityUid CreateRequestingStation(IEntityManager entManager, IMapManager mapManager,
            SharedMapSystem mapSystem, IPrototypeManager protoMan, out EntityUid dockGrid)
        {
            mapSystem.CreateMap(out var mapId);
            var grid = mapManager.CreateGridEntity(mapId);
            mapSystem.SetTile(grid.Owner, grid.Comp, Vector2i.Zero, new Tile(1));
            entManager.RunMapInit(grid.Owner, entManager.GetComponent<MetaDataComponent>(grid.Owner));
            dockGrid = grid.Owner;

            var stationConfig = protoMan.Index<Content.Shared.Maps.GameMapPrototype>(VesselWithStation)
                .Stations[VesselWithStation];
            var stationSystem = entManager.System<StationSystem>();
            return stationSystem.InitializeNewStation(stationConfig, new[] { dockGrid }, "Requesting Station");
        }
    }
}
