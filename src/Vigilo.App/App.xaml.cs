using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigilo.App.Services;
using Vigilo.App.ViewModels;
using Vigilo.Classification;
using Vigilo.Configuration;
using Vigilo.Core;
using Vigilo.Email;
using Vigilo.LocalAi;
using Vigilo.Outlook;
using Vigilo.Storage;

namespace Vigilo.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\Vigilo.App.SingleInstance";
    private const string ActivationEventName = @"Local\Vigilo.App.Activate";

    private IHost? _host;
    private string? _logDirectory;
    private string? _logPath;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var launchOptions = ApplicationLaunchOptions.Parse(e.Args);
        Marshal.ThrowExceptionForHR(
            SetCurrentProcessExplicitAppUserModelID(WindowsTaskbarIdentity.AppUserModelId));

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (!launchOptions.Background)
            {
                try
                {
                    using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                    activationEvent.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // The first instance is still completing early startup.
                }
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        var appData = GetAppDataDirectory();
        _logDirectory = Path.Combine(appData, "logs");
        _logPath = CreateRunLogPath(_logDirectory);
#if DEBUG
        AppContext.SetData("Vigilo.DebugInferenceCapturePath", Path.ChangeExtension(_logPath, ".debug-inferences.jsonl"));
        AppContext.SetData("Vigilo.DebugDecisionCapturePath", Path.ChangeExtension(_logPath, ".debug-decisions.jsonl"));
#endif
        WriteLatestRunMarker(_logDirectory, _logPath);
        RegisterGlobalExceptionHandlers();

        try
        {
            base.OnStartup(e);

            _host = BuildHost(e.Args, appData, _logPath);
            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation(
                "Vigilo startup began. AppDataPath={AppDataPath} LogDirectory={LogDirectory} LogPath={LogPath}",
                appData,
                _logDirectory,
                _logPath);

            await using (var scope = _host.Services.CreateAsyncScope())
            {
                foreach (var bootstrapper in scope.ServiceProvider.GetServices<IAppBootstrapper>())
                {
                    await bootstrapper.InitializeAsync(CancellationToken.None);
                }
            }

            await _host.StartAsync();
            var tray = _host.Services.GetRequiredService<TrayIconService>();
            tray.Initialize();
            StartActivationListener(tray);
            if (launchOptions.Background)
            {
                _ = tray.RefreshRuntimeStatusAsync(showSetupNotification: true);
            }
            else
            {
                tray.ShowMainWindow();
            }
            logger.LogInformation("Vigilo startup completed.");
        }
        catch (Exception ex)
        {
            LogCritical("Vigilo startup failed.", ex);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                _host.Services.GetRequiredService<ILogger<App>>().LogInformation("Vigilo shutdown began.");
                _host.Services.GetRequiredService<TrayIconService>().Dispose();
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
        }
        finally
        {
            _activationCancellation?.Cancel();
            _activationEvent?.Set();
            _activationEvent?.Dispose();
            _activationCancellation?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            base.OnExit(e);
        }
    }

    private void StartActivationListener(TrayIconService tray)
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationCancellation = new CancellationTokenSource();
        var cancellationToken = _activationCancellation.Token;
        _ = Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _activationEvent.WaitOne();
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        tray.ShowMainWindow();
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }, cancellationToken);
    }

    private static IHost BuildHost(string[] args, string appData, string logPath)
    {
        Directory.CreateDirectory(appData);
        var logDirectory = Path.Combine(appData, "logs");
        var modelSettingsPath = Path.Combine(appData, "model-settings.json");
        Directory.CreateDirectory(logDirectory);
        LocalFileLoggerProvider.DeleteOldLogs(logDirectory, retentionDays: 14);
        WriteLatestRunMarker(logDirectory, logPath);
        var appLogStore = new AppLogStore();
        var fileLoggerProvider = new LocalFileLoggerProvider(logPath, appLogStore);

        return Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(config =>
            {
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                config.AddJsonFile(modelSettingsPath, optional: true, reloadOnChange: true);
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                logging.AddProvider(fileLoggerProvider);
            })
            .ConfigureServices((context, services) =>
            {
                var responseCachePath = Path.Combine(appData, "response-cache.json");
                var storageOptions = new StorageOptions
                {
                    DatabasePath = Path.Combine(appData, "vigilo.db"),
                    ProtectedSettingsPath = Path.Combine(appData, "protected-settings.json"),
                    ModelSettingsPath = modelSettingsPath,
                    SettingsTransactionJournalPath = Path.Combine(appData, "settings-transaction.json"),
                    ClassificationResponseCachePath = responseCachePath
                };

                var modelOptions = context.Configuration.GetSection("Model").Get<ModelOptions>() ?? new ModelOptions();
                modelOptions.ModelBaseDirectory = Path.Combine(appData, "Models");

                services.AddLogging();
                services.AddSingleton(appLogStore);
                services.AddSingleton<IAtomicConfigurationRepository, AtomicConfigurationRepository>();
                services.AddVigiloStorage(storageOptions);
                services.AddVigiloLocalAi(modelOptions, modelSettingsPath);
                services.AddVigiloClassification(responseCachePath);
                services.AddVigiloEmail();
                services.AddVigiloOutlook();

                services.AddSingleton<TrayIconService>();
                services.AddSingleton<IUserApprovalService, WpfUserApprovalService>();
                services.AddScoped<INotificationService, WpfNotificationService>();
                services.AddTransient<MainWindow>();
                services.AddTransient<MainWindowViewModel>();
            })
            .Build();
    }

    private static string GetAppDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vigilo");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    private static string CreateRunLogPath(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        var processId = Environment.ProcessId;
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        return Path.Combine(logDirectory, $"vigilo-run-{timestamp}-pid{processId}.log");
    }

    private static void WriteLatestRunMarker(string logDirectory, string logPath)
    {
        try
        {
            File.WriteAllText(Path.Combine(logDirectory, "latest-run.txt"), logPath);
        }
        catch
        {
            // The marker is only a convenience; the run log itself is authoritative.
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCritical("Unhandled WPF dispatcher exception.", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogCritical("Unhandled AppDomain exception.", ex);
            }
            else
            {
                LogCritical($"Unhandled AppDomain exception object: {args.ExceptionObject}", null);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCritical("Unobserved task exception.", args.Exception);
        };
    }

    private void LogCritical(string message, Exception? exception)
    {
        var logged = false;
        try
        {
            var logger = _host?.Services.GetService<ILogger<App>>();
            if (logger is not null)
            {
                logger.LogCritical(exception, "{Message}", message);
                logged = true;
            }
        }
        catch
        {
            // Fallback below.
        }

        if (!logged)
        {
            WriteFallbackLog(message, exception);
        }
    }

    private void WriteFallbackLog(string message, Exception? exception)
    {
        if (_logDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_logDirectory);
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [Critical] Vigilo.App.App: ")
                .AppendLine(message);
            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            File.AppendAllText(
                _logPath ?? Path.Combine(_logDirectory, $"vigilo-run-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-pid{Environment.ProcessId}.log"),
                builder.ToString());
        }
        catch
        {
            // There is nowhere else reliable to report startup logging failures.
        }
    }
}
