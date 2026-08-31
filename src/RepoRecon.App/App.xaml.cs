using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RepoRecon.App.ViewModels;
using RepoRecon.App.Views;
using RepoRecon.Core.Adapters;
using RepoRecon.Core.Clone;
using RepoRecon.Core.Concurrency;
using RepoRecon.Core.Data;
using RepoRecon.Core.Fetch;
using RepoRecon.Core.Git;
using RepoRecon.Core.Pull;
using RepoRecon.Core.Push;
using RepoRecon.Core.Scanning;
using RepoRecon.Core.Security;
using RepoRecon.Core.Urls;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RepoRecon.App;

public partial class App : Application
{
    private IHost? _host;
    private bool _dbWasRecreated;

    public App()
    {
        // B-6: Global Dispatcher Unhandled Exception Handler
        DispatcherUnhandledException += App_DispatcherUnhandledException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on UI Dispatcher thread: {Message}", e.Exception.Message);
        MessageBox.Show($"예기치 않은 오류가 발생했습니다:\n{e.Exception.Message}", "RepoRecon 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dbPath = DatabasePathResolver.ResolveDatabasePath();
        var envRoot = Environment.GetEnvironmentVariable("REPORECON_PORTABLE_ROOT");
        var baseDir = !string.IsNullOrWhiteSpace(envRoot) 
            ? envRoot 
            : Path.GetDirectoryName(dbPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        var logDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logDir);

        // L-2: default minimum level is Information; verbose DBG output is opt-in
        // (set REPORECON_DEBUG_LOG=1 before launch) so a normal scan of 100+ repos
        // does not write hundreds of per-call DBG lines to disk on every run.
        var debugLoggingRequested = Environment.GetEnvironmentVariable("REPORECON_DEBUG_LOG") == "1";
        var levelSwitch = new LoggingLevelSwitch(debugLoggingRequested ? LogEventLevel.Debug : LogEventLevel.Information);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.File(
                Path.Combine(logDir, "reporecon-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("=== RepoRecon App Starting (Phase 5) === (DBG logging: {DebugLogging})", debugLoggingRequested ? "on (REPORECON_DEBUG_LOG=1)" : "off");

        // B-12: Explicit Content Root (exe directory)
        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppDomain.CurrentDomain.BaseDirectory)
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(Log.Logger);
                services.AddSingleton<IGitRunner, GitRunner>();
                services.AddSingleton<IRepoLockManager, RepoLockManager>();
                services.AddSingleton<SchemaMigrator>();
                services.AddSingleton(sp =>
                {
                    var migrator = sp.GetRequiredService<SchemaMigrator>();
                    // B-4: Track wasRecreated
                    _dbWasRecreated = migrator.EnsureInitialized(dbPath);
                    return new SqliteIndexStore(dbPath);
                });
                services.AddSingleton<HostAliasTable>();
                services.AddSingleton<ScanRootManager>();
                services.AddSingleton<RepoScanner>();
                services.AddSingleton<IFetchEngine, FetchEngine>();
                services.AddSingleton<IPullEngine, PullEngine>();
                services.AddSingleton<ISecureTokenStore, DpapiTokenStore>();
                services.AddSingleton<IPushEngine>(sp => new PushEngine(
                    sp.GetRequiredService<IGitRunner>(),
                    sp.GetRequiredService<SqliteIndexStore>(),
                    sp.GetService<IRepoLockManager>(),
                    sp.GetService<IFetchEngine>(),
                    sp.GetService<ISecureTokenStore>(),
                    logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<PushEngine>>()));
                services.AddSingleton<IServerAdapterFactory, ServerAdapterFactory>();
                services.AddSingleton<ICloneEngine, CloneEngine>();
                services.AddSingleton<ProfileManagementViewModel>();
                services.AddSingleton<CloneTabViewModel>();
                services.AddSingleton<ClonePreviewViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        if (_dbWasRecreated)
        {
            // B-4: Notify UI if database had to be recreated
            viewModel.NotifyDatabaseRecreated();
        }

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("=== RepoRecon App Exiting ===");
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
