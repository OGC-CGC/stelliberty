using SBproxy.Application.Platform;

namespace SBproxy.Desktop.Services;

internal sealed class AgentGlobalHotkeyService : IGlobalHotkeyService
{
    private readonly Dictionary<GlobalHotkeyAction, string> _gestures = [];

    public GlobalHotkeyApplyResult Apply(GlobalHotkeyAction action, string gesture)
    {
        var normalized = gesture.Trim();
        if (normalized.Length == 0)
        {
            _gestures.Remove(action);
            return GlobalHotkeyApplyResult.Success();
        }

        if (!IsValid(normalized))
        {
            return GlobalHotkeyApplyResult.Failure(GlobalHotkeyApplyError.Invalid);
        }

        if (_gestures.Any(pair =>
                pair.Key != action
                && string.Equals(pair.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return GlobalHotkeyApplyResult.Failure(GlobalHotkeyApplyError.Duplicate);
        }

        _gestures[action] = normalized;
        return GlobalHotkeyApplyResult.Success();
    }

    public void SetActivationSuppressed(bool isSuppressed)
    {
    }

#if DEBUG
    public bool SimulateActivation(GlobalHotkeyAction action) => false;
#endif

    public void Dispose()
    {
        _gestures.Clear();
    }

    private static bool IsValid(string gesture)
    {
        var tokens = gesture.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is < 2 or > 3)
        {
            return false;
        }

        return tokens[..^1].All(token =>
                token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Alt", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            && (tokens[^1].Length == 1 && char.IsAsciiLetterOrDigit(tokens[^1][0])
                || tokens[^1].Length is 2 or 3
                && tokens[^1][0] is 'F' or 'f'
                && int.TryParse(tokens[^1][1..], out var functionKey)
                && functionKey is >= 1 and <= 12);
    }
}
