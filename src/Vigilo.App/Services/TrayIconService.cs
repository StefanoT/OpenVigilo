using System.Drawing;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Vigilo.App.ViewModels;
using Vigilo.Core;
using WinForms = System.Windows.Forms;

namespace Vigilo.App.Services;

public sealed class TrayIconService(
    IServiceScopeFactory scopeFactory,
    IEmailScanQueue scanQueue,
    ILocalAiRuntimeSupervisor runtimeSupervisor) : IDisposable
{
    private WinForms.NotifyIcon? _notifyIcon;
    private Icon? _applicationIcon;
    private bool _exitRequested;
    private MainWindow? _mainWindow;
    private IServiceScope? _mainWindowScope;
    private WinForms.ToolStripMenuItem? _runtimeStatusItem;
    private bool _setupNotificationShown;

    public void Initialize()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        _applicationIcon = LoadApplicationIcon();
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "Vigilo",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        runtimeSupervisor.StatusChanged += OnRuntimeStatusChanged;
        UpdateRuntimeStatus(runtimeSupervisor.CurrentStatus);
    }

    private static Icon LoadApplicationIcon()
    {
        var streamInfo = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Vigilo.App;component/Assets/AppIcon.ico", UriKind.Absolute));
        if (streamInfo?.Stream is null)
        {
            throw new InvalidOperationException("The Vigilo application icon resource could not be loaded.");
        }

        using var stream = streamInfo.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    public void ShowMainWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(async () =>
        {
            if (_mainWindow is null)
            {
                _mainWindowScope = scopeFactory.CreateScope();
                _mainWindow = _mainWindowScope.ServiceProvider.GetRequiredService<MainWindow>();
                WindowsTaskbarIdentity.Apply(_mainWindow);
                _mainWindow.Closing += (_, args) =>
                {
                    if (_exitRequested)
                    {
                        WindowsTaskbarIdentity.Clear(_mainWindow);
                        return;
                    }

                    args.Cancel = true;
                    _mainWindow.Hide();
                };

                if (_mainWindow.DataContext is MainWindowViewModel viewModel)
                {
                    await viewModel.InitializeAsync(CancellationToken.None);
                }
            }

            _mainWindow.Show();
            _mainWindow.Activate();
        });
    }

    public void ShowBalloon(string title, string text)
    {
        _notifyIcon?.ShowBalloonTip(6000, title, text, WinForms.ToolTipIcon.Info);
    }

    public async Task RefreshRuntimeStatusAsync(bool showSetupNotification)
    {
        await runtimeSupervisor.EnsureReadyAsync(CancellationToken.None);
        var status = runtimeSupervisor.CurrentStatus;
        UpdateRuntimeStatus(status);
        if (showSetupNotification
            && !_setupNotificationShown
            && status.State is LocalAiRuntimeState.SetupRequired or LocalAiRuntimeState.Unavailable)
        {
            _setupNotificationShown = true;
            ShowBalloon("Vigilo setup required", status.Message);
        }
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Vigilo", null, (_, _) => ShowMainWindow());
        _runtimeStatusItem = new WinForms.ToolStripMenuItem("Local AI: checking") { Enabled = false };
        menu.Items.Add(_runtimeStatusItem);
        menu.Items.Add("Exit", null, (_, _) => Exit());
        return menu;
    }

    private void OnRuntimeStatusChanged(object? sender, LocalAiRuntimeStatus status) => UpdateRuntimeStatus(status);

    private void UpdateRuntimeStatus(LocalAiRuntimeStatus status)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => UpdateRuntimeStatus(status));
            return;
        }

        if (_runtimeStatusItem is null)
        {
            return;
        }

        var label = status.State switch
        {
            LocalAiRuntimeState.Ready => "Local AI: ready",
            LocalAiRuntimeState.Starting => "Local AI: starting",
            LocalAiRuntimeState.SetupRequired => "Local AI: setup required",
            LocalAiRuntimeState.Unavailable => "Local AI: unavailable",
            _ => "Local AI: stopped"
        };
        _runtimeStatusItem.Text = label;
        _runtimeStatusItem.ToolTipText = status.Message;
    }

    private async Task RunScanAsync()
    {
        await scanQueue.QueueScanAsync(CancellationToken.None);
    }

    private void Exit()
    {
        _exitRequested = true;
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        runtimeSupervisor.StatusChanged -= OnRuntimeStatusChanged;
        _notifyIcon?.Dispose();
        _applicationIcon?.Dispose();
        _mainWindowScope?.Dispose();
        _mainWindowScope = null;
    }
}
