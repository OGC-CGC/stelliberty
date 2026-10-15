using Stelliberty.Application.Runtime;
using Xunit;

namespace Stelliberty.Settings.Tests;

public sealed class BackgroundOperationsTests
{
    [Fact]
    public void SubscriptionUpdateBlocksDeleteFromAnotherClient()
    {
        var registry = new BackgroundOperationRegistry();
        var ui = Guid.NewGuid();
        var cli = Guid.NewGuid();
        var update = registry.TryAcquire(ui, new(BackgroundOperationRequest.Subscriptions, ["one"], true));
        Assert.NotNull(update);
        Assert.True(Assert.Single(registry.Snapshot).Request.IsUpdate);
        Assert.Null(registry.TryAcquire(cli, new(BackgroundOperationRequest.Subscriptions, ["one"])));
        registry.Release(ui, update!.Value);
        Assert.NotNull(registry.TryAcquire(cli, new(BackgroundOperationRequest.Subscriptions, ["one"])));
    }

    [Fact]
    public void AnotherClientCannotReleaseAnOperation()
    {
        var registry = new BackgroundOperationRegistry();
        var owner = Guid.NewGuid();
        var id = registry.TryAcquire(owner, new(BackgroundOperationRequest.Subscriptions, []))!.Value;
        registry.Release(Guid.NewGuid(), id);
        Assert.Single(registry.Snapshot);
        registry.Release(owner, id);
        Assert.Empty(registry.Snapshot);
    }

    [Fact]
    public void UiDelayTestsCanCooperateButBlockCliUntilAllFinish()
    {
        var registry = new BackgroundOperationRegistry();
        var ui = Guid.NewGuid();
        var cli = Guid.NewGuid();
        var first = registry.TryAcquire(ui, new(BackgroundOperationRequest.Delay, []))!.Value;
        var second = registry.TryAcquire(ui, new(BackgroundOperationRequest.Delay, []))!.Value;
        registry.Release(ui, first);
        Assert.Null(registry.TryAcquire(cli, new(BackgroundOperationRequest.Delay, [])));
        registry.Release(ui, second);
        Assert.NotNull(registry.TryAcquire(cli, new(BackgroundOperationRequest.Delay, [])));
    }

    [Fact]
    public void DisconnectReleasesOnlyThatClientsOperations()
    {
        var registry = new BackgroundOperationRegistry();
        var ui = Guid.NewGuid();
        var background = Guid.NewGuid();
        registry.TryAcquire(ui, new(BackgroundOperationRequest.Delay, []));
        registry.TryAcquire(background, new(BackgroundOperationRequest.Subscriptions, ["one"], true));
        registry.ReleaseOwner(ui);
        Assert.Equal(background, Assert.Single(registry.Snapshot).Owner);
        Assert.NotNull(registry.TryAcquire(Guid.NewGuid(), new(BackgroundOperationRequest.Delay, [])));
    }

    [Fact]
    public async Task SimultaneousSubscriptionWritersHaveOneWinner()
    {
        var registry = new BackgroundOperationRegistry();
        var grants = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            registry.TryAcquire(Guid.NewGuid(), new(BackgroundOperationRequest.Subscriptions, [])))));
        Assert.Single(grants, id => id.HasValue);
    }
}
