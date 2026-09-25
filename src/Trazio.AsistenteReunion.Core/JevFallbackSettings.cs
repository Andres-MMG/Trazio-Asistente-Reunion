namespace Trazio.AsistenteReunion.Core;

public sealed record JevFallbackSettings(string ApiKey);

public static class JevFallbackSettingsPolicy
{
    public static JevFallbackSettings Create(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Ingresa la clave API de Jev.", nameof(apiKey));
        var trimmed = apiKey.Trim();
        if (trimmed.Length > 4096 || trimmed.Any(char.IsControl))
            throw new ArgumentException("La clave API de Jev no es válida.", nameof(apiKey));
        return new(trimmed);
    }
}
