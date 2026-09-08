using System.Windows;
using System.Windows.Controls;
using Vigilo.App.ViewModels;

namespace Vigilo.App;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public SettingsWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.SaveSettingsAsync(AppPasswordBox.Password, CancellationToken.None);
            AppPasswordBox.Clear();
        }
        catch (Exception ex)
        {
            _viewModel.NotifySettingsSaveFailed(ex);
        }
    }

    private void AccountSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AppPasswordBox.Clear();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
