using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using IPTVDownloader.Models;
using IPTVDownloader.ViewModels;

namespace IPTVDownloader;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private ScrollViewer? _contentScroll;
    private bool _restoringScroll;
    private bool _closingAfterSave;

    public MainWindow(MainWindowViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        // A barra de título sempre usa a versão real definida no .csproj.
        // Assim não precisamos alterar o texto manualmente a cada nova versão.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var displayVersion = version is null ? "desconhecida" : $"{version.Major}.{version.Minor}.{version.Build}";
        Title = $"IPTV Downloader — v_{displayVersion}";

        _contentScroll = this.FindControl<ScrollViewer>("ContentScroll");

        Opened += OnOpened;
        Closing += OnClosing;
        _vm.ScrollRestoreRequested += RestoreScroll;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnOpened(object? sender, EventArgs e)
    {
        await _vm.InitializeAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterSave) return;

        e.Cancel = true;
        if (_contentScroll is not null)
            _vm.UpdateScrollOffset(_contentScroll.Offset.Y);

        await _vm.SaveStateAsync();
        _closingAfterSave = true;
        Close();
    }

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolha a pasta principal dos downloads",
            AllowMultiple = false
        });

        var folder = result.FirstOrDefault();
        var path = folder?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            await _vm.SetDownloadParentAsync(path);
    }

    private void OnOpenDownloadFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = _vm.DownloadRoot;
            if (string.IsNullOrWhiteSpace(path)) return;

            Directory.CreateDirectory(path);

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", path)
                {
                    UseShellExecute = false
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", path)
                {
                    UseShellExecute = false
                });
            }
        }
        catch (Exception ex)
        {
            _vm.Status = $"Não foi possível abrir a pasta: {ex.Message}";
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SearchCommand.CanExecute(null))
        {
            _vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnContentScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_restoringScroll || _contentScroll is null) return;
        _vm.UpdateScrollOffset(_contentScroll.Offset.Y);
    }

    private async void RestoreScroll(double offset)
    {
        if (_contentScroll is null) return;
        _restoringScroll = true;
        try
        {
            // Os cards mudam de altura quando capa e metadados chegam. Reaplicamos
            // algumas vezes para manter o mesmo ponto visual após a montagem.
            foreach (var delay in new[] { 30, 120, 300, 650 })
            {
                await Task.Delay(delay);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_contentScroll is null) return;
                    var max = Math.Max(0, _contentScroll.Extent.Height - _contentScroll.Viewport.Height);
                    _contentScroll.Offset = new Vector(0, Math.Clamp(offset, 0, max));
                });
            }
        }
        finally
        {
            _restoringScroll = false;
        }
    }

    private void OnAlphaClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is string letter && _vm.AlphaCommand.CanExecute(letter))
            _vm.AlphaCommand.Execute(letter);
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not MediaItem item || !item.CanOpenFromCard)
            return;

        // Se o clique veio de um botão interno, deixa o botão executar sozinho.
        if (e.Source is Visual source)
        {
            Visual? current = source;
            while (current is not null && !ReferenceEquals(current, border))
            {
                if (current is Button) return;
                current = current.GetVisualParent();
            }
        }

        if (item.OpenCommand?.CanExecute(null) == true)
            item.OpenCommand.Execute(null);
    }
}
