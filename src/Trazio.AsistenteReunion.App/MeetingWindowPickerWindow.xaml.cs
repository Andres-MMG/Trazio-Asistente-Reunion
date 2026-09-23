using System.Windows;
using System.Windows.Controls;

namespace Trazio.AsistenteReunion.App;

public partial class MeetingWindowPickerWindow : Window
{
    private readonly IMeetingWindowCatalog _catalog;

    public MeetingWindowPickerWindow(IMeetingWindowCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();
        Loaded += (_, _) => RefreshWindowsButton.Focus();
        Closed += (_, _) => ClearCandidates();
    }

    public MeetingWindowSelection? SelectedSelection { get; private set; }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidates = _catalog.Enumerate();
            WindowList.ItemsSource = candidates;
            WindowList.SelectedItem = null;
            PickerStatusText.Text = candidates.Count == 0
                ? "No se encontraron ventanas superiores visibles. Abre la reunión y vuelve a actualizar."
                : $"Se encontraron {candidates.Count} ventanas. Selecciona una para asociarla.";
            if (candidates.Count > 0) WindowList.Focus();
        }
        catch (Exception)
        {
            WindowList.ItemsSource = null;
            PickerStatusText.Text = "No se pudo consultar la lista de ventanas. Intenta nuevamente.";
        }
    }

    private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AssociateWindowButton.IsEnabled = WindowList.SelectedItem is MeetingWindowCandidate;

    private void AssociateWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowList.SelectedItem is not MeetingWindowCandidate candidate) return;
        SelectedSelection = new(candidate.Handle, candidate.ProcessId, candidate.Provider);
        ClearCandidates();
        DialogResult = true;
    }

    private void ClearCandidates()
    {
        WindowList.SelectedItem = null;
        WindowList.ItemsSource = null;
    }
}
