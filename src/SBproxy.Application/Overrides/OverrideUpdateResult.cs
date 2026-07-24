using SBproxy.Domain.Overrides;
namespace SBproxy.Application.Overrides;

public sealed record OverrideUpdateResult(
    IReadOnlyList<string> UpdatedOverrideIds,
    IReadOnlyList<string> SkippedOverrideIds);
