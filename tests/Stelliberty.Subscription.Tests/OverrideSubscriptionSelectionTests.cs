using Stelliberty.Application.Subscriptions;
using Xunit;
using Subscription = Stelliberty.Domain.Subscriptions.Subscription;

namespace Stelliberty.Subscription.Tests;

public sealed class OverrideSubscriptionSelectionTests
{
    [Fact]
    public void ChangesOnlyTargetOverrideAndPreservesHistoricalPosition()
    {
        var enabled = Create("enabled") with { OverrideIds = ["a", "target", "b"], OverrideSortPreference = ["b", "target", "a"] };
        var disabled = Create("disabled") with { OverrideIds = ["a", "b"], OverrideSortPreference = ["b", "target", "a"] };
        var untouched = Create("untouched") with { OverrideIds = ["other"] };
        var store = new MemorySubscriptionStore([enabled, disabled, untouched]);
        var updater = new SubscriptionOverrideSelectionUpdater(store);

        var affected = updater.SaveOverrideSubscriptions("target", ["disabled"]);

        Assert.Equal(["enabled", "disabled"], affected);
        Assert.Equal(["a", "b"], store.Find("enabled").OverrideIds);
        Assert.Equal(enabled.OverrideSortPreference, store.Find("enabled").OverrideSortPreference);
        Assert.Equal(["a", "b", "target"], store.Find("disabled").OverrideIds);
        Assert.Equal(disabled.OverrideSortPreference, store.Find("disabled").OverrideSortPreference);
        Assert.Same(untouched, store.Find("untouched"));
        Assert.Equal(["enabled", "disabled"], store.UpdatedIds);
    }

    [Fact]
    public void FirstSelectionAppendsAfterExistingOverridesWithoutSortPreference()
    {
        var store = new MemorySubscriptionStore([Create("config") with { OverrideIds = ["a", "b"] }]);
        var updater = new SubscriptionOverrideSelectionUpdater(store);

        updater.SaveOverrideSubscriptions("target", ["config"]);

        Assert.Equal(["a", "b", "target"], store.Find("config").OverrideIds);
        Assert.Equal(["a", "b", "target"], store.Find("config").OverrideSortPreference);
        Assert.Empty(updater.SaveOverrideSubscriptions("target", ["config"]));
        Assert.Single(store.UpdatedIds);
    }

    [Fact]
    public void EmptySelectionDisablesEveryAssociationAndKeepsOtherSettings()
    {
        var first = Create("first") with { OverrideIds = ["target", "other"], OverrideSortPreference = ["target", "other"], UserAgent = "custom-agent" };
        var second = Create("second") with { OverrideIds = ["target"] };
        var store = new MemorySubscriptionStore([first, second]);

        var affected = new SubscriptionOverrideSelectionUpdater(store).SaveOverrideSubscriptions("target", []);

        Assert.Equal(["first", "second"], affected);
        Assert.Equal(["other"], store.Find("first").OverrideIds);
        Assert.Equal(first.OverrideSortPreference, store.Find("first").OverrideSortPreference);
        Assert.Equal("custom-agent", store.Find("first").UserAgent);
        Assert.Empty(store.Find("second").OverrideIds);
    }

    private static Subscription Create(string id) => new(id, id, "local.yaml", true, DateTimeOffset.UnixEpoch);

    private sealed class MemorySubscriptionStore(IReadOnlyList<Subscription> subscriptions) : ISubscriptionStore
    {
        private readonly List<Subscription> _subscriptions = subscriptions.ToList();
        public List<string> UpdatedIds { get; } = [];
        public Subscription Find(string id) => _subscriptions.Single(item => item.Id == id);
        public IReadOnlyList<Subscription> LoadSubscriptions() => _subscriptions.ToList();

        public void UpdateSubscription(Subscription subscription)
        {
            _subscriptions[_subscriptions.FindIndex(item => item.Id == subscription.Id)] = subscription;
            UpdatedIds.Add(subscription.Id);
        }

        public void Save(Subscription subscription, string originalContent) => throw new NotSupportedException();
        public void SaveSubscriptions(IReadOnlyList<Subscription> items) => throw new NotSupportedException();
        public void SaveContent(string subscriptionId, string originalContent) => throw new NotSupportedException();
        public string ReadContent(string subscriptionId) => throw new NotSupportedException();
        public string GetContentPath(string subscriptionId) => throw new NotSupportedException();
        public void Delete(string subscriptionId) => throw new NotSupportedException();
    }
}
