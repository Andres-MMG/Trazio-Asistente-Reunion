using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

internal sealed class LocalAsrExecutionConsentDialog : Window
{
    public LocalAsrExecutionConsentDialog(
        LocalAsrExecutionPreview preview, AudioSourceKind source, TimeSpan start, TimeSpan end)
    {
        Title = "Autorizar segunda transcripción con Qwen3-ASR";
        Width = 800;
        Height = 600;
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock
        {
            Text = "Confirma el fragmento y los archivos antes de ejecutar Qwen3-ASR",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(heading, 0);
        layout.Children.Add(heading);
        var warning = new TextBlock
        {
            Text = LocalAsrExecutionPreview.TrustWarning,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(117, 58, 11)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(warning, 1);
        layout.Children.Add(warning);
        var details = BuildDetails(preview, source, start, end);
        var detailBox = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetName(detailBox, "Fuente, intervalo, rutas y huellas del proceso de Qwen3-ASR");
        Grid.SetRow(detailBox, 2);
        layout.Children.Add(detailBox);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "Cancelar", MinWidth = 96, IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var authorize = new Button
        {
            Content = "Autorizar esta transcripción",
            MinWidth = 190,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true
        };
        authorize.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(authorize);
        Grid.SetRow(actions, 3);
        layout.Children.Add(actions);
        Content = layout;
    }
    internal static string BuildDetails(
        LocalAsrExecutionPreview preview, AudioSourceKind source, TimeSpan start, TimeSpan end) =>
        new StringBuilder()
            .AppendLine("Fuente de audio:")
            .AppendLine(source == AudioSourceKind.Microphone ? "Micrófono" : "Audio del equipo")
            .AppendLine($"Intervalo: {start:hh\\:mm\\:ss\\.fff}–{end:hh\\:mm\\:ss\\.fff}").AppendLine()
            .AppendLine("Ejecutable llama-server:").AppendLine(preview.ServerExecutablePath)
            .AppendLine("SHA-256: " + preview.ServerSha256).AppendLine()
            .AppendLine("Modelo Qwen3-ASR:").AppendLine(preview.ModelPath)
            .AppendLine("SHA-256: " + preview.ModelSha256).AppendLine()
            .AppendLine("Proyector mmproj:").AppendLine(preview.MultimodalProjectorPath)
            .AppendLine("SHA-256: " + preview.MultimodalProjectorSha256).ToString();
}
