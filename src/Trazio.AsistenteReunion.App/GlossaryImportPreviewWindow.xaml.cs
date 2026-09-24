using System.Windows;

namespace Trazio.AsistenteReunion.App;

public partial class GlossaryImportPreviewWindow : Window
{
    public GlossaryImportPreviewWindow(GlossaryImportPreviewPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        InitializeComponent();
        CountsText.Text = presentation.CountsLabel;
        RowsList.ItemsSource = presentation.Rows;
        ImportButton.Content = presentation.ImportButtonLabel;
        ImportButton.IsEnabled = presentation.NewCount > 0;
        ImportButton.Visibility = presentation.NewCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
