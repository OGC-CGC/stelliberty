using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Stelliberty.Desktop.Services;

namespace Stelliberty.Desktop.Controls;

public sealed class ProcessIcon : Image
{
    public static readonly StyledProperty<string?> ProcessPathProperty =
        AvaloniaProperty.Register<ProcessIcon, string?>(nameof(ProcessPath));

    public static readonly DirectProperty<ProcessIcon, bool> HasIconProperty =
        AvaloniaProperty.RegisterDirect<ProcessIcon, bool>(nameof(HasIcon), icon => icon.HasIcon);

    private bool _hasIcon;
    private bool _attached;
    private int _request;

    public string? ProcessPath
    {
        get => GetValue(ProcessPathProperty);
        set => SetValue(ProcessPathProperty, value);
    }

    public bool HasIcon => _hasIcon;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _ = RefreshIconAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _request++;
        Apply(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProcessPathProperty && _attached)
        {
            _ = RefreshIconAsync();
        }
    }

    private async Task RefreshIconAsync()
    {
        var request = ++_request;
        var loading = ProcessIconService.GetAsync(ProcessPath);
        if (loading.IsCompletedSuccessfully)
        {
            Apply(loading.Result);
            return;
        }

        Apply(null);
        var bitmap = await loading.ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (request == _request)
            {
                Apply(bitmap);
            }
        });
    }

    private void Apply(Bitmap? bitmap)
    {
        Source = bitmap;
        SetAndRaise(HasIconProperty, ref _hasIcon, bitmap is not null);
    }
}
