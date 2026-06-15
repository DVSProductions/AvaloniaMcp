using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Keincheck.Client;
using Keincheck.Core;

namespace Keincheck.Avalonia;

/// <summary>
/// <see cref="AppBuilder"/> integration for the thin broker client. Call
/// <see cref="UseMcpClient"/> in <c>Program.cs</c> to connect the app to the hub.
/// Mirrors the embedded <c>UseMcpServer</c> surface but starts a pipe client instead of
/// an in-process Kestrel host. This wires the Avalonia <see cref="AvaloniaUiAdapter"/> +
/// <see cref="AvaloniaUiDispatcher"/> into the framework-free
/// <see cref="BrokerClientHost"/>.
/// </summary>
public static class AppBuilderClientExtensions
{
    /// <summary>
    /// Connects the application produced by <paramref name="builder"/> to the Keincheck
    /// hub over the named pipe. The client starts after framework setup (so the
    /// spine/UI thread exist) and disconnects gracefully on shutdown.
    /// </summary>
    public static AppBuilder UseMcpClient(this AppBuilder builder, Action<McpClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new McpClientOptions();
        configure?.Invoke(options);

        return builder.AfterSetup(b =>
        {
            var app = b.Instance;
            if (app is null)
                return;

            var coreOptions = options.CoreOptions ?? new McpServerOptions();

            // Install the Avalonia binding-error sink (so get_binding_errors works) and
            // build the Avalonia adapter/dispatcher that drive the framework-free client.
            BindingErrorSink? sink = coreOptions.CaptureBindingErrors
                ? BindingErrorSink.Current ?? BindingErrorSink.Install(coreOptions.BindingErrorBufferSize)
                : null;

            var serializer = new PropertyValueSerializer(coreOptions.MaxSerializationDepth);
            var adapter = new AvaloniaUiAdapter(serializer, sink, coreOptions.MaxScreenshotDimension);
            var dispatcher = new AvaloniaUiDispatcher();

            IDisposable? client = BrokerClientHost.Start(adapter, dispatcher, options);

            void WireShutdown()
            {
                if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Exit += (_, _) =>
                    {
                        // Graceful, bounded teardown so exit isn't blocked.
                        var c = client;
                        client = null;
                        c?.Dispose();
                    };
                }
            }

            if (app.ApplicationLifetime is not null)
                WireShutdown();
            else
                global::Avalonia.Threading.Dispatcher.UIThread.Post(WireShutdown);
        });
    }
}
