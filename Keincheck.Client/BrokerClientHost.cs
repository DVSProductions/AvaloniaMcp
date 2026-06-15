using Keincheck.Core;

namespace Keincheck.Client;

/// <summary>
/// The framework-free entry point the per-toolkit glue calls to start the broker
/// client. Given an <see cref="IUiAdapter"/> + <see cref="IUiDispatcher"/> (supplied by
/// the Avalonia / WPF integration package) and client options, it builds the tool host
/// and starts the connect/serve loop, returning an <see cref="IDisposable"/> that
/// tears the client down gracefully on dispose.
/// </summary>
public static class BrokerClientHost
{
    /// <summary>
    /// Starts a broker client driven by <paramref name="adapter"/> and
    /// <paramref name="dispatcher"/>. The returned handle disposes the client
    /// (graceful, bounded) when disposed — wire it to your app's shutdown.
    /// </summary>
    public static IDisposable Start(IUiAdapter adapter, IUiDispatcher dispatcher, McpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);

        var client = BrokerClient.Start(adapter, dispatcher, options);
        return new Handle(client);
    }

    /// <summary>
    /// Disposable wrapper that bounds the graceful teardown so app exit is never
    /// blocked by a wedged pipe.
    /// </summary>
    private sealed class Handle : IDisposable
    {
        private BrokerClient? _client;

        public Handle(BrokerClient client) => _client = client;

        public void Dispose()
        {
            var c = _client;
            _client = null;
            if (c is not null)
                c.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
    }
}
