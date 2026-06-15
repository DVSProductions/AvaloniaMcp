using System.Windows;
using Keincheck.Wpf;

namespace Keincheck.Wpf.Demo;

/// <summary>
/// WPF entry point for the Keincheck demo. The analog of the Avalonia
/// <c>Keincheck.Demo</c> <c>Program.UseMcpClient</c> wiring: on startup it attaches the
/// thin broker client via <see cref="ApplicationClientExtensions.UseKeincheckClient"/>,
/// registering this app with the hub as <c>"wpfdemo"</c> over the named pipe. The 22
/// Core tools then drive this window through the <c>WpfUiAdapter</c> exactly like an
/// Avalonia client (it appears in <c>hub_list_clients</c>, <c>hub_select_client</c>
/// targets it, and the inspection/automation/input tools operate on it).
/// </summary>
public partial class App : Application
{
    private IDisposable? _keincheck;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Connect to the Keincheck hub over the named pipe and register as "wpfdemo".
        // The returned handle disconnects gracefully on app exit (wired internally).
        _keincheck = this.UseKeincheckClient(o => o.AppId = "wpfdemo");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keincheck?.Dispose();
        _keincheck = null;
        base.OnExit(e);
    }
}
