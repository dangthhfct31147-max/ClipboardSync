using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
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
    private FileLogger? _logger;
    public static bool IsServiceProcess { get; internal set; }

    public ClipboardSyncService()
    {
        var logDir = Path.Combine(GetAppBasePath(), "logs");
        try
        {
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"clipboardsync_{DateTime.Now:yyyyMMdd}.log");
            _logger = new FileLogger(logPath);
            _logger.Info("ClipboardSyncService created.");
        }
        catch (Exception ex)
        {
            WriteToEventLog($"Constructor failed: {ex}");
            throw;
        }
        ServiceName = "ClipboardSync";
        CanStop = true;
        CanShutdown = true;
    }

    public void StartService()
    {
        OnStart([]);
    }

    public void StopService()
    {
        OnStop();
    }

    protected override void OnStart(string[] args)
    {
        base.OnStart(args);
        IsServiceProcess = true;

        var thread = new Thread(() =>
        {
            _logger?.Info("Service background thread starting...");
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
                        services.AddSingleton(_logger!);
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
                    .UseConsoleLifetime(options => options.SuppressStatusMessages = true)
                    .Build();

                _logger?.Info("Host built, starting...");
                _host.Run();
            }
            catch (Exception ex)
            {
                _logger?.Error("Unhandled exception in service background thread", ex);
                WriteToEventLog($"Unhandled: {ex}");
            }
        })
        {
            IsBackground = true,
            Name = "ClipboardSync.Host"
        };

        thread.Start();
        _logger?.Info("Service starting...");
    }

    private static void WriteToEventLog(string message)
    {
        try
        {
            var source = "ClipboardSync";
            if (!System.Diagnostics.EventLog.SourceExists(source))
                System.Diagnostics.EventLog.CreateEventSource(source, "Application");
            System.Diagnostics.EventLog.WriteEntry(source, message, System.Diagnostics.EventLogEntryType.Error);
        }
        catch { }
    }

    protected override void OnStop()
    {
        _logger?.Info("Service stopping...");
        Task.Run(async () =>
        {
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
                _host = null;
            }
            _logger?.Info("Service stopped.");
        }).GetAwaiter().GetResult();
    }

    protected override void OnShutdown()
    {
        OnStop();
        base.OnShutdown();
    }

    private static string GetAppBasePath()
    {
        return AppContext.BaseDirectory;
    }
}
