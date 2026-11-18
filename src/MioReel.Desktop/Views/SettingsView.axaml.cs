using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MioReel.Desktop.Services.Hotkeys;
using MioReel.Desktop.ViewModels;

namespace MioReel.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnSettingsKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnSettingsKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.ActiveRecordingAction is null)
            return;

        if (KeyNameMapper.IsPureModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            vm.CancelRecordingHotkey();
            e.Handled = true;
            return;
        }

        if (KeyNameMapper.TryFromAvalonia(e.Key, e.KeyModifiers, out var binding))
        {
            vm.TryCommitRecordedHotkey(binding);
            e.Handled = true;
        }
    }

    private async void OnChooseDownloadDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || DataContext is not MainWindowViewModel vm) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择离线下载目录",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.DownloadDirectory = path;
    }
}
