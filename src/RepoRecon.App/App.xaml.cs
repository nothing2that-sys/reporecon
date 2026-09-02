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
using RepoRecon.Core.Export;
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
        string baseDir;
#if DEBUG
        // 검증 스크립트 전용 우회 경로 (§9.3 미문서화, §12 기록). DatabasePathResolver와
        // 같은 조건으로 감싼다 — 그러지 않으면 DB는 정상 경로에, 로그만 이 변수를 따라가는
        // 불일치가 Release 빌드에서 생긴다.
        var envRoot = Environment.GetEnvironmentVariable("REPORECON_PORTABLE_ROOT");
        baseDir = !string.IsNullOrWhiteSpace(envRoot)
            ? envRoot
            : Path.GetDirectoryName(dbPath) ?? AppDomain.CurrentDomain.BaseDirectory;
#else
        baseDir = Path.GetDirectoryName(dbPath) ?? AppDomain.CurrentDomain.BaseDirectory;
#endif
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
                // §9.4 [확정]: 보관 기본값 "14일"이지 14개 파일이 아니다. retainedFileCountLimit는
                // 파일 개수 기준이라 앱을 며칠 안 켜면(파일이 그날그날 생기지 않으므로) 14개가
                // 14일보다 훨씬 긴 기간을 덮는다. 롤링 구간 종료 시점부터의 경과 시간을 기준으로
                // 지우는 retainedFileTimeLimit이 실제 "14일" 요구에 맞는 파라미터다.
                retainedFileTimeLimit: TimeSpan.FromDays(14),
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
                services.AddSingleton<IRemoteUrlService, RemoteUrlService>();
                services.AddSingleton<IRollbackService, RollbackService>();
                services.AddSingleton<IExportService, ExportService>();
                services.AddSingleton<ProfileManagementViewModel>();
                services.AddSingleton<CloneTabViewModel>();
                services.AddSingleton<ClonePreviewViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // §2.3/§4.2 (4), v4.1: 스캔·카탈로그 동기화가 시작되기 전에
        // server_profiles.aliases_json을 HostAliasTable에 적재해야 정규화 매칭이 된다.
        var indexStore = _host.Services.GetRequiredService<SqliteIndexStore>();
        var aliasTable = _host.Services.GetRequiredService<HostAliasTable>();
        aliasTable.ReloadFromProfiles(await indexStore.GetServerProfilesAsync());

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
