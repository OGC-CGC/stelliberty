namespace Stelliberty.Application.Runtime;

public sealed class OutboundInterfaceUnavailableException()
    : InvalidOperationException("No available outbound interface matches the selected policy.")
{
    public const string ErrorCode = "core.outbound_interface_unavailable";
}
