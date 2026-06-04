using System.ServiceProcess;
using ClipboardSync.Service;

var isService = Environment.UserInteractive == false
    && !args.Contains("--console");

if (isService)
{
    ClipboardSyncService.IsServiceProcess = true;
    ServiceBase.Run(new ClipboardSyncService());
}
else
{
    Console.WriteLine("ClipboardSync - Console Mode");
    Console.WriteLine("Press Ctrl+C to stop, or run as a Windows Service for background operation.");
    Console.WriteLine();

    var service = new ClipboardSyncService();
    var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        service.StopService();
    };

    try
    {
        service.StartService();
        Console.WriteLine("Service started. Press Ctrl+C to stop...");
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Stopping service...");
    }
    finally
    {
        service.StopService();
        Console.WriteLine("Service stopped.");
    }
}
