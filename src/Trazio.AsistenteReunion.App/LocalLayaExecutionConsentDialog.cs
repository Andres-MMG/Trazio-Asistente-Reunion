using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Trazio.AsistenteReunion.App;

internal sealed class LocalLayaExecutionConsentDialog : Window
{
    public LocalLayaExecutionConsentDialog(LocalLayaExecutionPreview preview)
    {
        Title = "Autorizar evaluación local con Laya";
        Width = 790;
        Height = 520;
        MinWidth = 600;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock
        {
            Text = "Confirma qué código local ejecutará la evaluación",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(heading, 0);
        layout.Children.Add(heading);
        var warning = new TextBlock
        {
            Text = LocalLayaExecutionPreview.TrustWarning,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(117, 58, 11)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(warning, 1);
        layout.Children.Add(warning);
        var details = new StringBuilder()
            .AppendLine("Ejecutable Node:").AppendLine(preview.NodeExecutablePath).AppendLine()
            .AppendLine("Script del sidecar:").AppendLine(preview.ScriptPath).AppendLine()
            .AppendLine("Dependencias:").AppendLine(preview.DependenciesDirectory).AppendLine()
            .AppendLine("Carpeta del modelo:").AppendLine(preview.ModelDirectory).AppendLine()
            .AppendLine("Huella SHA-256 del paquete (no es una certificación):")
            .Append(preview.BundleFingerprint).ToString();
        var detailBox = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetName(detailBox, "Rutas exactas y huella del código local que se ejecutará");
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
            Content = "Autorizar esta evaluación", MinWidth = 170,
            Margin = new Thickness(8, 0, 0, 0), IsDefault = true
        };
        authorize.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(authorize);
        Grid.SetRow(actions, 3);
        layout.Children.Add(actions);
        Content = layout;
    }
}
