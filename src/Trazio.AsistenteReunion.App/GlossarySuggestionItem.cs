namespace Trazio.AsistenteReunion.App;

public sealed class GlossarySuggestionItem
{
    public GlossarySuggestionItem(string mistakenForm, string preferredTerm)
    {
        MistakenForm = mistakenForm;
        PreferredTerm = preferredTerm;
    }

    public bool IsSelected { get; set; } = true;
    public string MistakenForm { get; set; }
    public string PreferredTerm { get; set; }
}
