using System.Windows;
using System.Windows.Threading;
using Keincheck.Core;

namespace Keincheck.Wpf;

/// <summary>
/// <see cref="IUiDispatcher"/> over WPF's <c>System.Windows.Application.Current.Dispatcher</c>.
/// Marshals tool work onto the WPF UI thread; runs synchronously when already on it.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private static Dispatcher Ui =>
        Application.Current?.Dispatcher
        ?? Dispatcher.CurrentDispatcher;

    /// <inheritdoc />
    public Task<T> Run<T>(Func<T> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        var d = Ui;
        if (d.CheckAccess())
            return Task.FromResult(fn());
        return d.InvokeAsync(fn).Task;
    }

    /// <inheritdoc />
    public Task Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var d = Ui;
        if (d.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return d.InvokeAsync(action).Task;
    }

    /// <inheritdoc />
    public Task<T> RunAsync<T>(Func<Task<T>> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        var d = Ui;
        if (d.CheckAccess())
            return fn();
        return d.InvokeAsync(fn).Task.Unwrap();
    }

    /// <inheritdoc />
    public Task RunAsync(Func<Task> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        var d = Ui;
        if (d.CheckAccess())
            return fn();
        return d.InvokeAsync(fn).Task.Unwrap();
    }
}
