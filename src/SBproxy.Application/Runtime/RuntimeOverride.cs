using SBproxy.Application.Overrides;
using SBproxy.Domain.Overrides;

namespace SBproxy.Application.Runtime;

public sealed record RuntimeOverride(
    string Id,
    string Name,
    OverrideFormat Format,
    string Content);
