using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.IntegrationTests.Tests._Hyperion;

[TestFixture]
public sealed class PhysicsStressCommandsTest
{
    private static int CountDynamicBodies(Robust.Shared.GameObjects.IEntityManager entMan)
    {
        var n = 0;
        var query = entMan.AllEntityQueryEnumerator<PhysicsComponent>();
        while (query.MoveNext(out _, out var body))
        {
            if (body.BodyType == Robust.Shared.Physics.BodyType.Dynamic)
                n++;
        }
        return n;
    }

    [Test]
    public async Task ShrapnelSpawnsFreeBodies()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var consoleHost = server.ResolveDependency<IConsoleHost>();

        // Create a real map + grid so the command has somewhere to spawn on.
        var testMap = await pair.CreateTestMap();

        var before = 0;
        await server.WaitAssertion(() => before = CountDynamicBodies(entMan));

        await server.WaitAssertion(() => consoleHost.ExecuteCommand("stressshrapnel 50"));
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var after = CountDynamicBodies(entMan);
            Assert.That(after - before, Is.GreaterThanOrEqualTo(50),
                "stressshrapnel 50 should add at least 50 dynamic bodies");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FleetSpawnsRammingGrids()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var consoleHost = server.ResolveDependency<IConsoleHost>();

        // Create a real map + grid so the command has a map to load ships onto.
        var testMap = await pair.CreateTestMap();

        var before = 0;
        await server.WaitAssertion(() =>
        {
            var q = entMan.AllEntityQueryEnumerator<MapGridComponent>();
            while (q.MoveNext(out _, out _)) before++;
        });

        // 1 per side = 2 small ship grids closing on each other.
        await server.WaitAssertion(() => consoleHost.ExecuteCommand("stressfleet 1 ramdronesmall"));
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var after = 0;
            var q = entMan.AllEntityQueryEnumerator<MapGridComponent>();
            while (q.MoveNext(out _, out _)) after++;
            Assert.That(after - before, Is.GreaterThanOrEqualTo(2),
                "stressfleet 1 should add at least 2 ship grids");
        });

        await pair.CleanReturnAsync();
    }
}
