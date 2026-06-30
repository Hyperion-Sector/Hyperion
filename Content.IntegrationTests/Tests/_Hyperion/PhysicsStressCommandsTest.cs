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

        // The command requires a grid to spawn on. If the default test pool map has no grid,
        // Assert.Ignore so the test is skipped rather than failing a condition we can't satisfy.
        var hasGrid = false;
        await server.WaitAssertion(() =>
        {
            var gridQuery = entMan.AllEntityQueryEnumerator<MapGridComponent>();
            hasGrid = gridQuery.MoveNext(out _, out _);
        });

        if (!hasGrid)
        {
            Assert.Ignore("Default test pool map has no grid; stressshrapnel cannot spawn bodies. " +
                          "Run this test against a map with at least one grid.");
            return;
        }

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
}
