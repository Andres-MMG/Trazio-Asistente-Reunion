using System.Windows;

namespace Trazio.AsistenteReunion.App;

public partial class VisualCaptureConsentWindow : Window
{
    public VisualCaptureConsentWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Authorize_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
