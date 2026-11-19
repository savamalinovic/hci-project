using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using PaperlessDesktop.Services;
using PaperlessDesktop.ViewModels;

namespace PaperlessDesktop;

public partial class DocumentUploadWindow : Window
{
    private readonly DocumentUploadViewModel viewModel;

    internal DocumentUploadWindow(PaperlessClient client, Uri server, string token, DocumentsViewModel documents)
    {
        InitializeComponent();
        viewModel = new DocumentUploadViewModel(client, server, token, documents.Tags, documents.Correspondents,
            documents.DocumentTypes, documents.PendingUploadTaskId);
        DataContext = viewModel;
    }

    internal bool IsCompleted => viewModel.IsCompleted;
    internal int? ImportedDocumentId => viewModel.ImportedDocumentId;
    internal Guid? PendingTaskId => viewModel.PendingTaskId;

    private void PickFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Izaberite dokument",
            CheckFileExists = true,
            Filter = "Dokumenti i slike|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp;*.txt;*.docx|Svi fajlovi|*.*"
        };
        if (picker.ShowDialog(this) == true)
        {
            viewModel.SelectFile(picker.FileName);
            TitleTextBox.Focus();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!viewModel.CanClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        base.OnClosed(e);
    }
}
