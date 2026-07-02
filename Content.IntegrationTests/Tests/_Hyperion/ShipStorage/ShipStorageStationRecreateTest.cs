// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

#nullable enable

using System;
using System.Threading.Tasks;
using Content.Server._Hyperion.ShipStorage;
using Content.Server._NF.SectorServices;
using Content.Server._NF.ShuttleRecords;
using Content.Server._NF.ShuttleRecords.Components;
using Content.Server._NF.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.Station.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Hyperion.ShipStorage
{
    /// <summary>
    /// Cycle 4 station recreate: a retrieved vessel whose record names a vessel proto
    /// with a GameMapPrototype becomes a station again (named after the ship, vessel
    /// info restored) and gets a fresh sector shuttle record; a stationless blob
    /// retrieves clean with no station and no dangling StationMember.
    /// </summary>
    [TestFixture]
    public sealed class ShipStorageStationRecreateTest
    {
        [Test]
        public async Task RetrieveRecreatesStationAndRecord()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var stationSystem = entManager.System<StationSystem>();
            var metaSystem = entManager.System<MetaDataSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();
            var sectorService = entManager.System<SectorServiceSystem>();
            var records = entManager.System<ShuttleRecordsSystem>();

            const string shipName = "HSV Testy";
            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;
            EntityUid station = default;
            EntityUid oldShipStation = default;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out _);

                var config = protoMan.Index<Content.Shared.Maps.GameMapPrototype>(ShipStorageTestHelpers.VesselWithStation)
                    .Stations[ShipStorageTestHelpers.VesselWithStation];
                oldShipStation = stationSystem.InitializeNewStation(config, new[] { gridUid });
                var info = entManager.EnsureComponent<ExtraShuttleInformationComponent>(oldShipStation);
                info.Vessel = ShipStorageTestHelpers.VesselWithStation;

                // Rename AFTER station init, like a player would: AddGridToStation
                // stamps the station's generated name onto the grid, so a rename
                // before init would be clobbered.
                metaSystem.SetEntityName(gridUid, shipName);

                // Bare integration pairs spawn no sector-services host station, so the
                // service entity doesn't exist yet; give the requesting station the
                // host component (its ComponentInit spawns the service entity), then
                // ensure the records data component so AddRecord has somewhere to file.
                entManager.EnsureComponent<StationSectorServiceHostComponent>(station);
                entManager.EnsureComponent<SectorShuttleRecordsComponent>(sectorService.GetServiceEntity());
            });

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var (result, shipId) = await storeTask;
            Assert.That(result, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(shipId!.Value, ownerId, station));
            var retrieved = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(retrieved, Is.Not.Null);
                var newGrid = retrieved!.Value;

                Assert.That(entManager.TryGetComponent<StationMemberComponent>(newGrid, out var member), Is.True,
                    "A station-configured vessel must be a station member again after retrieve.");
                var newStation = member!.Station;
                Assert.That(entManager.EntityExists(newStation), Is.True);
                Assert.That(newStation, Is.Not.EqualTo(oldShipStation),
                    "The station is recreated, not resurrected.");
                Assert.That(entManager.GetComponent<MetaDataComponent>(newStation).EntityName, Is.EqualTo(shipName),
                    "The recreated station carries the ship's name, not the proto generator's.");

                Assert.That(entManager.TryGetComponent<ExtraShuttleInformationComponent>(newStation, out var info), Is.True);
                Assert.That(info!.Vessel, Is.Not.Null);
                Assert.That(info.Vessel!.Value.Id, Is.EqualTo(ShipStorageTestHelpers.VesselWithStation));

                Assert.That(records.TryGetRecord(entManager.GetNetEntity(newGrid), out _), Is.True,
                    "The retrieved ship must be re-listed in the sector shuttle records.");
            });

            await pair.CleanReturnAsync();
        }

        [Test]
        public async Task StationlessBlobRetrievesWithoutStation()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var entManager = server.ResolveDependency<IEntityManager>();
            var mapManager = server.ResolveDependency<IMapManager>();
            var protoMan = server.ResolveDependency<IPrototypeManager>();
            var mapSystem = entManager.System<SharedMapSystem>();
            var shipStorage = entManager.System<ShipStorageSystem>();

            var ownerId = Guid.NewGuid();
            EntityUid gridUid = default;
            EntityUid station = default;

            await server.WaitPost(() =>
            {
                gridUid = ShipStorageTestHelpers.CreateStorableGrid(entManager, mapManager, mapSystem, out _);
                station = ShipStorageTestHelpers.CreateRequestingStation(entManager, mapManager, mapSystem, protoMan, out _);
            });

            Task<(ShipStorageResult Result, Guid? ShipId)> storeTask = null!;
            await server.WaitPost(() => storeTask = shipStorage.TryStoreShip(gridUid, ownerId));
            var (result, shipId) = await storeTask;
            Assert.That(result, Is.EqualTo(ShipStorageResult.Success));

            server.RunTicks(1);
            await server.WaitIdleAsync();

            Task<EntityUid?> retrieveTask = null!;
            await server.WaitPost(() => retrieveTask = shipStorage.TryRetrieveShip(shipId!.Value, ownerId, station));
            var retrieved = await retrieveTask;

            server.RunTicks(2);
            await server.WaitIdleAsync();

            await server.WaitAssertion(() =>
            {
                Assert.That(retrieved, Is.Not.Null, "Empty VesselProto must not block retrieve.");
                Assert.That(entManager.HasComponent<StationMemberComponent>(retrieved!.Value), Is.False,
                    "A stationless blob must not come back wearing a dangling StationMember.");
            });

            await pair.CleanReturnAsync();
        }
    }
}
