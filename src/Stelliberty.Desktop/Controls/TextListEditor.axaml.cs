using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Stelliberty.Desktop.Controls;

public sealed partial class TextListEditor : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TextListEditor, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<char> StorageSeparatorProperty =
        AvaloniaProperty.Register<TextListEditor, char>(nameof(StorageSeparator), '\n');

    public static readonly StyledProperty<string?> InputAutomationIdProperty =
        AvaloniaProperty.Register<TextListEditor, string?>(nameof(InputAutomationId));

    private const StringSplitOptions EntryOptions = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
    private readonly ObservableCollection<TextListEntry> _entries = [];
    private bool _initialized;

    public TextListEditor()
    {
        InitializeComponent();
        ItemRows.ItemsSource = _entries;
        _initialized = true;
        ReadStoredText();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public char StorageSeparator
    {
        get => GetValue(StorageSeparatorProperty);
        set => SetValue(StorageSeparatorProperty, value);
    }

    public string? InputAutomationId
    {
        get => GetValue(InputAutomationIdProperty);
        set => SetValue(InputAutomationIdProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_initialized && (change.Property == TextProperty || change.Property == StorageSeparatorProperty))
        {
            ReadStoredText();
        }
    }

    private void ReadStoredText()
    {
        var text = Text ?? string.Empty;
        var values = StorageSeparator == '\n'
            ? text.Split(['\r', '\n'], EntryOptions)
            : text.Split(StorageSeparator, EntryOptions);
        if (_entries.Select(entry => entry.Text).SequenceEqual(values))
        {
            return;
        }

        _entries.Clear();
        foreach (var value in values)
        {
            _entries.Add(new TextListEntry(value));
        }
    }

    private void SaveEntries()
    {
        var separator = StorageSeparator == '\n' ? Environment.NewLine : StorageSeparator.ToString();
        SetCurrentValue(TextProperty, string.Join(separator, _entries.Select(entry => entry.Text)));
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e) => AddEntry();

    private void OnAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddEntry();
        }
    }

    private void AddEntry()
    {
        var value = AddInput.Text?.Trim();
        if (string.IsNullOrEmpty(value) || ContainsSeparator(value))
        {
            return;
        }

        _entries.Add(new TextListEntry(value));
        AddInput.Clear();
        SaveEntries();
    }

    private void OnItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveItem(sender);
        }
    }

    private void OnItemLostFocus(object? sender, FocusChangedEventArgs e) => SaveItem(sender);

    private void SaveItem(object? sender)
    {
        if (sender is not TextBox { DataContext: TextListEntry entry } input || input.Text == entry.Text)
        {
            return;
        }

        if (!_entries.Contains(entry))
        {
            return;
        }

        var value = input.Text?.Trim() ?? string.Empty;
        if (ContainsSeparator(value))
        {
            input.SetCurrentValue(TextBox.TextProperty, entry.Text);
            return;
        }

        if (value.Length == 0)
        {
            _entries.Remove(entry);
        }
        else
        {
            entry.Text = value;
            input.SetCurrentValue(TextBox.TextProperty, entry.Text);
        }

        SaveEntries();
    }

    private void OnRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TextListEntry entry } && _entries.Remove(entry))
        {
            SaveEntries();
        }
    }

    private bool ContainsSeparator(string value) =>
        value.Contains(StorageSeparator) || value.Contains('\r') || value.Contains('\n');
}

public sealed class TextListEntry(string text)
{
    public string Text { get; set; } = text;
}
