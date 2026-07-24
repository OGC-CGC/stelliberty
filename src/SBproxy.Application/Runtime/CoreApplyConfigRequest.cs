namespace SBproxy.Application.Runtime;

public sealed record CoreApplyConfigRequest(string RuntimeYamlPath, string SubscriptionId);
