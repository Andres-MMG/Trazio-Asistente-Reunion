using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Trazio.AsistenteReunion.App;

internal sealed class RefinementOutboundConsentDialog : Window
{
    internal RefinementOutboundConsentDialog(RefinementOutboundPreview preview, string? explanation = null)
    {
        Title = "Autorizar envío de transcripción";
        Width = 760;
        Height = 590;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var notice = new TextBlock
        {
            Text = "Trazio enviará exactamente el siguiente texto a una API externa. Revísalo completo antes de autorizar. La clave API no aparece aquí." +
                (string.IsNullOrWhiteSpace(explanation) ? string.Empty : " " + explanation),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        grid.Children.Add(notice);
        var endpoint = new TextBlock { Text = $"Destino: {preview.Endpoint}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
        Grid.SetRow(endpoint, 1);
        grid.Children.Add(endpoint);
        var model = new TextBlock { Text = $"Modelo: {preview.Model}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) };
        Grid.SetRow(model, 2);
        grid.Children.Add(model);
        var body = new TextBox
        {
            Text = preview.RequestBody,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12
        };
        System.Windows.Automation.AutomationProperties.SetName(body, "Contenido JSON exacto que se enviará al proveedor externo");
        Grid.SetRow(body, 3);
        grid.Children.Add(body);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancelar", MinWidth = 100, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        actions.Children.Add(cancel);
        var authorize = new Button { Content = "Autorizar este envío", MinWidth = 150 };
        authorize.Click += (_, _) => { DialogResult = true; Close(); };
        actions.Children.Add(authorize);
        Grid.SetRow(actions, 4);
        grid.Children.Add(actions);
        Content = grid;
    }
}
