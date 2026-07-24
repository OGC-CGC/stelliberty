using SBproxy.Domain.Overrides;
namespace SBproxy.Application.Overrides;

public interface ILocalOverrideFileReader
{
    string ReadAllText(string filePath);
}
