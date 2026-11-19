using System.ComponentModel;
using System.Windows;
using PaperlessDesktop.Services;
using PaperlessDesktop.ViewModels;

namespace PaperlessDesktop;

public partial class TagManagerWindow : Window
{
    private readonly TagManagerViewModel viewModel;

    internal TagManagerWindow(PaperlessClient client, Uri server, string token)
    {
        InitializeComponent();
        viewModel = new TagManagerViewModel(client, server, token);
        DataContext = viewModel;
    }

    internal bool TagsChanged => viewModel.TagsChanged;

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await viewModel.LoadAsync();
    }

    private void NewTag_Click(object sender, RoutedEventArgs e)
    {
        viewModel.StartNewTag();
        NameInput.Focus();
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanDelete || viewModel.SelectedTag is not { } tag)
        {
            return;
        }

        var choice = MessageBox.Show(this,
            $"Obrisati tag „{tag.Name}“? Obrisat će se i njegovi podtagovi, ako ih ima, a dodjele će biti uklonjene s dokumenata.",
            "Brisanje taga", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice == MessageBoxResult.Yes)
        {
            await viewModel.DeleteAsync(tag);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!viewModel.CanClose)
        {
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        base.OnClosed(e);
    }
}
