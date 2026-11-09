using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PosSystem.Frontend.Data;
using PosSystem.Frontend.Hardware;
using PosSystem.Frontend.Services;
using PosSystem.Frontend.ViewModels;

namespace PosSystem.Frontend;

public partial class App : Application
{
    private readonly IHost _host;

    public App()
    {
        var culture = new CultureInfo("en-NG");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        RegisterGlobalCrashShield();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddDbContext<LocalDbContext>(options =>
                {
                    options.UseSqlite($"Data Source={LocalDbContext.GetDatabasePath()};Pooling=True");
                });

                services.AddSingleton<ITokenVault, WindowsDpapiTokenVault>();
                services.AddSingleton<ISessionService, SessionService>();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<AuthenticationService>();
                services.AddSingleton<CartExecutionEngine>();
                services.AddSingleton<DatabaseMaintenanceService>();

                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<LoginViewModel>();
                services.AddTransient<CheckoutViewModel>();

                services.AddSingleton<ReceiptPrinterService>();
                services.AddSingleton<BarcodeScannerListener>();

                services.AddHostedService<OutboxSyncService>();
                services.AddHttpClient();
            })
            .Build();

#if MANUAL_XAML_LOADER
        Startup += OnStartup;
#endif
    }

    private void RegisterGlobalCrashShield()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteCrashLog("AppDomainUnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
    }

    public static string WriteCrashLog(string source, Exception exception)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PosSystem",
                "Logs");
            Directory.CreateDirectory(logDir);

            string logFile = Path.Combine(logDir, $"pos_error_{DateTime.UtcNow:yyyyMMdd}.log");
            string entry = $"[{DateTime.UtcNow:O}] [{source}] {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}{Environment.NewLine}--------------------------------------------------{Environment.NewLine}";
            File.AppendAllText(logFile, entry);
            return logFile;
        }
        catch
        {
            return string.Empty;
        }
    }

#if MANUAL_XAML_LOADER
    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.Run();
    }
#endif

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var navService = _host.Services.GetRequiredService<INavigationService>();
        var sessionService = _host.Services.GetRequiredService<ISessionService>();

        // Ensure SQLite schema upgrade & seed is 100% complete before any ViewModel queries it
        await Task.Run(() =>
        {
            using var scope = _host.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            dbContext.InitializeAndSeed();
        });

        if (sessionService.IsLoggedIn)
        {
            try
            {
                navService.NavigateTo<CheckoutViewModel>();
            }
            catch (Exception ex)
            {
                WriteCrashLog("StartupSessionRestore", ex);
                sessionService.Logout();
                navService.NavigateTo<LoginViewModel>();
            }
        }
        else
        {
            navService.NavigateTo<LoginViewModel>();
        }

        mainWindow.Show();
        mainWindow.Activate();

        _ = _host.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
