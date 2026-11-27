using System.Threading;
using System.Windows;
using MixMoghimi.Services;

namespace MixMoghimi;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(x => string.Equals(x, "--restore-proxy", StringComparison.OrdinalIgnoreCase)))
        {
            try { WindowsProxyManager.RestoreAfterUnexpectedExit(); } catch { }
            Shutdown(0);
            return;
        }

        _singleInstance = new Mutex(true, "Local\\MIX-MOGHIMI-Windows-x64", out bool createdNew);
        DispatcherUnhandledException += (_, _) => SafeRestoreProxy();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => SafeRestoreProxy();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeRestoreProxy();
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        // If Windows or the app crashed while a proxy session was active, restore
        // the exact user proxy settings before showing the UI.
        try
        {
            WindowsProxyManager.RestoreAfterUnexpectedExit();
            SingBoxService.RemoveStaleRuntimeFiles();
        }
        catch
        {
            // Recovery must not prevent the UI from opening. A visible warning is
            // surfaced by MainWindow if a subsequent network operation fails.
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SafeRestoreProxy();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void SafeRestoreProxy()
    {
        try { WindowsProxyManager.RestoreAfterUnexpectedExit(); }
        catch { /* Preserve the encrypted backup so a later startup can retry recovery. */ }
    }
}
