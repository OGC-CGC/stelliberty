using Stelliberty.Application.Overrides;
using Stelliberty.Domain.Subscriptions;
using Stelliberty.Application.Diagnostics;

namespace Stelliberty.Application.Subscriptions;

public sealed class SubscriptionOverrideSelectionUpdater(ISubscriptionStore store, IOverrideStore? overrideStore = null)
{
    // 从覆写侧修改关联，只改变当前覆写，保留各配置的其他选择和排序。
    public IReadOnlyList<string> SaveOverrideSubscriptions(string overrideId, IReadOnlyList<string> subscriptionIds)
    {
        var selected = subscriptionIds.ToHashSet(StringComparer.Ordinal);
        var affectedSubscriptionIds = new List<string>();
        foreach (var subscription in store.LoadSubscriptions())
        {
            var wasSelected = subscription.OverrideIds.Contains(overrideId, StringComparer.Ordinal);
            var isSelected = selected.Contains(subscription.Id);
            if (wasSelected == isSelected)
            {
                continue;
            }

            var overrideIds = subscription.OverrideIds.Where(id => id != overrideId).ToList();
            var sortPreference = subscription.OverrideSortPreference.ToList();
            if (isSelected)
            {
                overrideIds.Add(overrideId);
                if (!sortPreference.Contains(overrideId, StringComparer.Ordinal))
                {
                    // 无历史位置时，追加到所有已启用覆写之后。
                    sortPreference.AddRange(subscription.OverrideIds.Where(id => !sortPreference.Contains(id, StringComparer.Ordinal)));
                    sortPreference.Add(overrideId);
                }
            }

            SaveSelection(subscription.Id, overrideIds, sortPreference);
            affectedSubscriptionIds.Add(subscription.Id);
        }

        return affectedSubscriptionIds;
    }

    public Subscription SaveSelection(
        string subscriptionId,
        IReadOnlyList<string> selectedOverrideIds,
        IReadOnlyList<string> overrideSortPreference)
    {
        var subscription = store.LoadSubscriptions().FirstOrDefault(item => item.Id == subscriptionId)
            ?? throw new InvalidOperationException($"Subscription not found: {subscriptionId}");
        var updated = subscription with
        {
            OverrideIds = selectedOverrideIds.ToList(),
            OverrideSortPreference = overrideSortPreference.ToList()
        };

        store.UpdateSubscription(updated);
        AppLogger.Info($"Subscription override selection saved: {subscription.Name}");
        return updated;
    }

    // 新选择排在前面；旧的未选中项保持相对顺序。
    public Subscription SaveValidatedSelection(string subscriptionId, IReadOnlyList<string> overrideIds)
    {
        var subscription = store.LoadSubscriptions().FirstOrDefault(item => item.Id == subscriptionId)
            ?? throw new InvalidOperationException($"Subscription not found: {subscriptionId}");
        var selectedOverrideIds = overrideIds
            .Where(overrideId => !string.IsNullOrWhiteSpace(overrideId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (selectedOverrideIds.Count > 0)
        {
            var availableOverrideIds = overrideStore?.LoadOverrides().Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
                ?? throw new InvalidOperationException("Override list is unavailable");
            var missingOverrideId = selectedOverrideIds.FirstOrDefault(overrideId => !availableOverrideIds.Contains(overrideId));
            if (missingOverrideId is not null)
            {
                throw new InvalidOperationException($"Override not found: {missingOverrideId}");
            }
        }

        var selected = selectedOverrideIds.ToHashSet(StringComparer.Ordinal);
        var sortPreference = selectedOverrideIds
            .Concat(subscription.OverrideSortPreference.Where(overrideId => !selected.Contains(overrideId)))
            .ToList();
        return SaveSelection(subscriptionId, selectedOverrideIds, sortPreference);
    }

    public Subscription? DisableOverridesForSubscription(string subscriptionId)
    {
        var subscription = store.LoadSubscriptions().FirstOrDefault(item => item.Id == subscriptionId);
        if (subscription is null || subscription.OverrideIds.Count == 0)
        {
            return null;
        }

        return SaveSelection(subscriptionId, [], subscription.OverrideSortPreference);
    }
}
