using ClipboardSync;
using ClipboardSync.Core;
using ClipboardSync.Service;
using ClipboardSync.Tray;
using ClipboardSync.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Resolve log dir and logger first — before any DI
var appDataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ClipboardSync");
var logDir = Path.Combine(appDataDir, "logs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"clipboardsync_{DateTime.Now:yyyyMMdd}.log");
var logger = new FileLogger(logPath);
logger.Info("ClipboardSync starting...");

try
{
    var host = Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.SetBasePath(AppContext.BaseDirectory);
            cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        })
        .ConfigureServices((_, services) =>
        {
            services.AddSingleton(logger);

            // AppConfig from appsettings.json
            services.AddSingleton(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                return new AppConfig
                {
                    Discovery = new DiscoveryConfig
                    {
                        UdpPort = config.GetValue<int>("Discovery:UdpPort", 51234),
                        BroadcastIntervalSeconds = config.GetValue<int>("Discovery:BroadcastIntervalSeconds", 5),
                        PeerTimeoutSeconds = config.GetValue<int>("Discovery:PeerTimeoutSeconds", 30)
                    },
                    Transfer = new TransferConfig
                    {
                        TcpPort = config.GetValue<int>("Transfer:TcpPort", 51235)
                    },
                    Sync = new SyncConfig
                    {
                        Enabled = config.GetValue<bool>("Sync:Enabled", true),
                        SyncText = config.GetValue<bool>("Sync:SyncText", true),
                        SyncImages = config.GetValue<bool>("Sync:SyncImages", true),
                        SyncFiles = config.GetValue<bool>("Sync:SyncFiles", false)
                    },
                    Auth = new AuthConfig
                    {
                        Token = config.GetValue<string>("Auth:Token")
                    }
                };
            });

            services.AddSingleton<ClipboardMonitor>();
            services.AddSingleton<PeerDiscovery>();
            services.AddSingleton<PeerManager>();
            services.AddSingleton<TcpTransfer>();
            services.AddSingleton<TrayIconManager>();
            services.AddHostedService<ClipboardSyncHostedService>();
        })
        .Build();

    var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        logger.Info("Process exiting...");
        cts.Cancel();
    };

    logger.Info("Host built, starting services...");
    await host.RunAsync(cts.Token);
}
catch (Exception ex)
{
    logger.Error("Unhandled exception", ex);
    MessageBox.Show(
        $"ClipboardSync could not start.\n\n{ex.Message}\n\nSee logs in:\n{logDir}",
        "ClipboardSync",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
}
finally
{
    logger.Info("ClipboardSync stopped.");
}
