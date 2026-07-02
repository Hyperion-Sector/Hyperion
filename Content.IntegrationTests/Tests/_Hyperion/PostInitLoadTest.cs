// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Content.Server.Atmos.Piping.Unary.Components;
using Content.Server.DeviceNetwork.Systems;
using Content.Shared.DeviceNetwork.Components;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Hyperion;

/// <summary>
///     Verifies two ship-persistence fidelity fixes together, using the engine's own grid save/load (no
///     database involved):
///     <list type="bullet">
///     <item>
///         Fix A - previously <c>[ViewVariables]</c>-only atmos device settings (e.g.
///         <see cref="GasVentPumpComponent.Enabled"/>) are now <c>[DataField]</c> and survive a grid round-trip
///         through the map serializer.
///     </item>
///     <item>
///         Fix B - a device that joins its <see cref="DeviceNetworkComponent"/> network via
///         <c>DeviceNetworkSystem.OnMapInit</c> on a fresh spawn still re-joins its network when its grid is
///         loaded post-map-init (where <see cref="MapInitEvent"/> never fires).
///     </item>
///     </list>
/// </summary>
[TestFixture]
[TestOf(typeof(GasVentPumpComponent))]
[TestOf(typeof(DeviceNetworkSystem))]
public sealed class PostInitLoadTest
{
    [Test]
    public async Task DeviceSettingsAndNetworkSurvivePostInitLoad()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var mapLoader = entMan.System<MapLoaderSystem>();
        var deviceNetSystem = entMan.System<DeviceNetworkSystem>();

        var path = new ResPath($"/{nameof(PostInitLoadTest)}.yml");

        EntityUid vent = default;
        EntityUid grid = default;
        EntityUid oldMap = default;
        string ventAddress = null!;

        // 1. Create a map + grid, set a tile, spawn a networked atmos device on it, and flip a newly-tagged
        // field to a non-default value.
        await server.WaitPost(() =>
        {
            oldMap = mapSys.CreateMap(out var mapId, runMapInit: false);
            var gridEnt = mapSys.CreateGridEntity(mapId);
            grid = gridEnt.Owner;
            mapSys.SetTile(gridEnt, new Vector2i(0, 0), new Tile(1));

            var coords = new EntityCoordinates(grid, 0.5f, 0.5f);
            vent = entMan.SpawnEntity("GasVentPump", coords);

            // Fresh spawns aren't map-initialized until the map is. Do that now so the entity goes through the
            // normal ComponentStartup -> MapInit -> DeviceNetworkSystem.OnMapInit path, exactly like a live
            // round would, and picks up a real network address before we save it.
            mapSys.InitializeMap(mapId);

            var ventComp = entMan.GetComponent<GasVentPumpComponent>(vent);
            Assert.That(ventComp.Enabled, Is.True, "Test assumes GasVentPump defaults to Enabled.");
            ventComp.Enabled = false; // Hyperion: non-default value the save/load round-trip must preserve

            var device = entMan.GetComponent<DeviceNetworkComponent>(vent);
            Assert.That(device.Address, Is.Not.Empty, "Vent should have joined its device network on map init.");
            Assert.That(deviceNetSystem.IsDeviceConnected(vent, device), Is.True);
            ventAddress = device.Address;
        });

        // 2. Save the grid, then delete it so the load path is exercised cleanly.
        await server.WaitPost(() =>
        {
            Assert.That(mapLoader.TrySaveGrid(grid, path));
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(oldMap);
        });

        // 3. Load the YAML back onto a brand new (already-initialized) map. Since the saved grid was
        // post-map-init, MapInitEvent must NOT re-fire for the loaded entities.
        Entity<MapGridComponent>? loadedGrid = null;
        await server.WaitPost(() =>
        {
            mapSys.CreateMap(out var newMapId);
            Assert.That(mapLoader.TryLoadGrid(newMapId, path, out loadedGrid));
        });

        // Give DeviceNetworkSystem's post-init fallback (queued on ComponentStartup, drained on the following
        // Update()) a tick to run.
        await server.WaitRunTicks(1);
        await server.WaitIdleAsync();

        await server.WaitAssertion(() =>
        {
            Assert.That(loadedGrid, Is.Not.Null);

            EntityUid? loadedVent = null;
            var query = entMan.EntityQueryEnumerator<GasVentPumpComponent, DeviceNetworkComponent>();
            while (query.MoveNext(out var uid, out _, out _))
            {
                if (entMan.GetComponent<TransformComponent>(uid).GridUid == loadedGrid.Value.Owner)
                {
                    loadedVent = uid;
                    break;
                }
            }

            Assert.That(loadedVent, Is.Not.Null, "Loaded grid should contain the saved GasVentPump.");

            // The loaded entity must not have re-run MapInit (that's the whole point of a post-init load).
            var meta = entMan.GetComponent<MetaDataComponent>(loadedVent!.Value);
            Assert.That(meta.EntityLifeStage, Is.EqualTo(EntityLifeStage.MapInitialized));

            // (a) Fix A: the tagged field kept its non-default value across the save/load round-trip.
            var loadedVentComp = entMan.GetComponent<GasVentPumpComponent>(loadedVent.Value);
            Assert.That(loadedVentComp.Enabled, Is.False);

            // (b) Fix B: the device re-joined its network on the post-init load, without MapInitEvent firing.
            var loadedDevice = entMan.GetComponent<DeviceNetworkComponent>(loadedVent.Value);
            Assert.That(loadedDevice.Address, Is.EqualTo(ventAddress), "Address should be preserved from the save.");
            Assert.That(deviceNetSystem.IsDeviceConnected(loadedVent.Value, loadedDevice), Is.True,
                "Device should have re-registered on its network via the post-mapinit fallback.");
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(loadedGrid!.Value.Owner);
        });

        await pair.CleanReturnAsync();
    }
}
