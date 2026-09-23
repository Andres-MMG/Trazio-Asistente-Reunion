using System.Windows;

namespace Trazio.AsistenteReunion.App;

public partial class AnonymousVisualAnalysisConsentWindow : Window
{
    public AnonymousVisualAnalysisConsentWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Authorize_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
