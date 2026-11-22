using System.Windows;
using PaperlessDesktop.Models;
using PaperlessDesktop.Services;
using PaperlessDesktop.ViewModels;

namespace PaperlessDesktop;

public partial class NaturalSearchWindow : Window
{
    private readonly NaturalSearchViewModel viewModel;

    internal NaturalSearchWindow(DocumentsViewModel documents, GeminiSearchInterpreter? gemini)
    {
        InitializeComponent();
        viewModel = new NaturalSearchViewModel(documents, gemini);
        DataContext = viewModel;
    }

    internal DocumentSearchCriteria? Criteria { get; private set; }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.TryGetCriteria(out var criteria))
        {
            Criteria = criteria;
            DialogResult = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        base.OnClosed(e);
    }
}
