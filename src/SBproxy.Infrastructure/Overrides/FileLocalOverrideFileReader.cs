using SBproxy.Application.Overrides;
using SBproxy.Domain.Overrides;

namespace SBproxy.Infrastructure.Overrides;

public sealed class FileLocalOverrideFileReader : ILocalOverrideFileReader
{
    public string ReadAllText(string filePath)
    {
        return File.ReadAllText(filePath);
    }
}
