using SBproxy.Domain.Subscriptions;
namespace SBproxy.Application.Subscriptions;

public sealed record SelectedSubscriptionRuntimePaths(string OriginalContentPath, string RuntimeConfigPath);
