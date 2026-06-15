using System.Windows;

namespace Keincheck.Wpf.Demo;

/// <summary>
/// Code-behind for the WPF demo window. Sets a real <see cref="MainViewModel"/> as the
/// <c>DataContext</c> so <c>get_data_context</c> has something to read and the two-way
/// bindings have a backing model, and handles the Save button click as an observable,
/// bindable side effect for the invoke/click tools.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    /// <summary>
    /// Click handler for the "Save" button: mutates a bound ViewModel property
    /// (<see cref="MainViewModel.ClickCount"/>) so invoking the button via the MCP tools
    /// produces an observable, bindable side effect.
    /// </summary>
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ClickCount++;
        _viewModel.StatusMessage = $"Saved {_viewModel.ClickCount} time(s).";
    }
}
