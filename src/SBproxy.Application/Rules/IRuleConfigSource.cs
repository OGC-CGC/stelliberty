using SBproxy.Domain.Rules;
namespace SBproxy.Application.Rules;

public interface IRuleConfigSource
{
    string ReadRuntimeConfig();
}
