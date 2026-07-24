using SBproxy.Domain.Overrides;
namespace SBproxy.Application.Overrides;

public sealed record OverrideDeleteResult(string DeletedOverrideId, IReadOnlyList<string> AffectedSubscriptionIds);
