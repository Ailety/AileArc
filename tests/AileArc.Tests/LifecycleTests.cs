using AileArc.Core.Lifecycle;
using Xunit;

namespace AileArc.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public void CatalogFindsNormalizedPathsAndHardLinkIdentity()
    {
        var catalog = new WindowCatalog<object>();
        var first = new object();
        catalog.Register(first, @"C:\Archives\a.zip", "volume:file");
        Assert.Same(first, catalog.Find(@"c:\archives\folder\..\a.zip", null));
        Assert.Same(first, catalog.Find(@"C:\Alias\b.zip", "volume:file"));
        var second = new object();
        catalog.Register(second, @"C:\Archives\a.zip", "volume:file");
        Assert.Same(second, catalog.Find(@"C:\Archives\a.zip", null));
        catalog.Remove(second);
        Assert.Same(first, catalog.Find(@"C:\Archives\a.zip", null));
        catalog.Remove(first);
        Assert.Null(catalog.Find(@"C:\Archives\a.zip", "volume:file"));
    }
    [Fact]
    public void LaunchParsingKeepsSpaceContainingPathsAndNormalizesRelativePaths()
    {
        var request = ActivationRequest.Parse(["--new-window", "sub folder/a.zip"], @"C:\Archives");
        Assert.True(request.NewWindow);
        Assert.Equal(@"C:\Archives\sub folder\a.zip", Assert.Single(request.Paths));
        Assert.Throws<ArgumentException>(() => ActivationRequest.Parse(["--unknown"], @"C:\Archives"));
    }
    [Fact]
    public async Task BrokerHasOneOwnerAndForwardsMultipleRequests()
    {
        string key = "AileArc-tests-" + Guid.NewGuid().ToString("N");
        var received = new System.Collections.Concurrent.ConcurrentQueue<ActivationRequest>();
        using (var owner = ActivationBroker.TryRegister(key))
        {
            Assert.NotNull(owner);
            Assert.Null(ActivationBroker.TryRegister(key));
            owner.Listen(r => { received.Enqueue(r); return true; });
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i => ActivationBroker.ForwardAsync(key, new([@"C:\Archives\" + i + ".zip"]))));
            Assert.Equal(4, received.Count);
            Assert.Equal(4, received.Select(r => r.Paths[0]).Distinct().Count());
        }
        using var replacement = ActivationBroker.TryRegister(key);
        Assert.NotNull(replacement);
    }
}
