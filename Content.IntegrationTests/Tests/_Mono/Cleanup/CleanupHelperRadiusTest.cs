using Content.Server._Mono.Cleanup;

namespace Content.IntegrationTests.Tests._Mono.Cleanup;

// Hyperion: regression test for the cleanup zero-radius crash.
// A price-0 entity makes ShouldEntityCleanup compute a search radius of exactly 0
// (sqrt(0/maxValue) == 0), which the entity lookup rejects with
// "Range must be a positive float" (DebugTools.Assert(range > 0)). That assert threw
// inside the cleanup sweep's Update, dirty-disposing the test pool and cascading
// failures across unrelated tests. HasNearbyPlayers must tolerate a non-positive radius.
[TestFixture]
public sealed class CleanupHelperRadiusTest
{
    [Test]
    public async Task HasNearbyPlayers_ZeroRadius_DoesNotThrow()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var cleanup = server.System<CleanupHelperSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.DoesNotThrow(() => cleanup.HasNearbyPlayers(map.GridCoords, 0f));
        });

        await pair.CleanReturnAsync();
    }
}
