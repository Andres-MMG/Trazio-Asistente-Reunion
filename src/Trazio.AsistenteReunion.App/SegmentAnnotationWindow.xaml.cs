using System.Windows;
using System.Windows.Controls;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public partial class SegmentAnnotationWindow : Window
{
    public SegmentAnnotationWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => AnnotationText.Focus();
    }

    public SegmentAnnotationKind SelectedKind { get; private set; } = SegmentAnnotationKind.Note;
    public string Annotation { get; private set; } = string.Empty;

    private void AnnotationText_TextChanged(object sender, TextChangedEventArgs e)
    {
        CharacterCountText.Text = $"{AnnotationText.Text.Length:N0} / {SegmentAnnotationLimits.MaximumTextLength:N0}";
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(AnnotationText.Text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SelectedKind = (KindSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Decision" => SegmentAnnotationKind.Decision,
                "FollowUp" => SegmentAnnotationKind.FollowUp,
                _ => SegmentAnnotationKind.Note
            };
            Annotation = SegmentAnnotationLimits.NormalizeText(AnnotationText.Text);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "No se pudo guardar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
