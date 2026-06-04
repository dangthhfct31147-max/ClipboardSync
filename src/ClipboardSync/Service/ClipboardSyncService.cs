using System.ServiceProcess;
using ClipboardSync.Tray;
using ClipboardSync.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using ClipboardSync.Core;

namespace ClipboardSync.Service;

public sealed class ClipboardSyncService : ServiceBase
{
    private IHost? _host;
    private readonly FileLogger _logger;

    public ClipboardSyncService()
    {
        var logPath = GetLogPath();
        _logger = new FileLogger(logPath);
        ServiceName = "ClipboardSync";
        CanStop = true;
        CanShutdown = true;
        _logger.Info("ClipboardSyncService created.");
    }

    public void StartService()
    {
        OnStart([]);
    }

    public void StopService()
    {
        OnStop();
    }

    protected override async void OnStart(string[] args)
    {
        _logger.Info("Service starting...");
        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.SetBasePath(GetAppBasePath());
                    cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                })
                .ConfigureServices((_, services) =>
                {
                    services.AddSingleton(_logger);
                    services.AddSingleton<AppConfig>(sp =>
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
                .UseConsoleLifetime(options => options.SuppressStatusMessages = true)
                .Build();

            await _host.StartAsync();
            _logger.Info("Service started successfully.");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start service", ex);
            throw;
        }
    }

    protected override async void OnStop()
    {
        _logger.Info("Service stopping...");
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
        _logger.Info("Service stopped.");
    }

    protected override void OnShutdown()
    {
        OnStop();
        base.OnShutdown();
    }

    private static string GetLogPath()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipboardSync", "logs");
        Directory.CreateDirectory(logDir);
        return Path.Combine(logDir, $"clipboardsync_{DateTime.Now:yyyyMMdd}.log");
    }

    private static string GetAppBasePath()
    {
        return AppContext.BaseDirectory;
    }
}
