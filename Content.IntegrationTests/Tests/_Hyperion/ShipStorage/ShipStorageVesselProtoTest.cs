// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server._NF.Station.Components;
using Content.Server.Station.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 4 store metadata: a grid that is a member of a station whose
    /// ExtraShuttleInformation names a vessel prototype stores that vessel id in its
    /// DB record; a stationless grid stores an empty one.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageVesselProtoTest
    {
        [Test]
        public async Task StoreCapturesVesselProtoFromStation()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var stationSystem = entManager.System<StationSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);

                // Give the stored grid its own vessel station, the way a purchase does.
                var config = protoMan.Index<Content.Shared.Maps.GameMapPrototype>(ShipStorageTestHelpers.VesselWithStation)
                    .Stations[ShipStorageTestHelpers.VesselWithStation];
                var shipStation = stationSystem.InitializeNewStation(config, new[] { gridUid });
                var info = entManager.EnsureComponent<ExtraShuttleInformationComponent>(shipStation);
                info.Vessel = ShipStorageTestHelpers.VesselWithStation;
            });

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var (result, shipId) = await storeTask;
            Assert.That(result, Is.EqualTo(ShipStorageResult.Success));

            var ships = await shipStorage.GetStoredShips(ownerId);
            Assert.That(ships, Has.Count.EqualTo(1));
            Assert.That(ships[0].VesselProto, Is.EqualTo(ShipStorageTestHelpers.VesselWithStation),
                "Store must capture the vessel prototype id from the grid's station.");

            await pair.CleanReturnAsync();
        }
    }
}
