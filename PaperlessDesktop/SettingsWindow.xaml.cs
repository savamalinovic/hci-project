using System.IO;
using System.Windows;
using PaperlessDesktop.ViewModels;

namespace PaperlessDesktop;

public partial class SettingsWindow : Window
{
    private readonly App app;
    private readonly SettingsViewModel viewModel;

    internal SettingsWindow(App app)
    {
        InitializeComponent();
        this.app = app;
        viewModel = new SettingsViewModel(app.Settings);
        DataContext = viewModel;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            app.SaveSettings(viewModel.GetSettings());
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SaveError.Visibility = Visibility.Visible;
        }
    }
}
