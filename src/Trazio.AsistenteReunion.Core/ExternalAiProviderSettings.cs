using System.Text.Json.Serialization;

namespace Trazio.AsistenteReunion.Core;

public sealed record ExternalAiProviderSettings(
    string Endpoint,
    string Model,
    string? ApiKey)
{
    [JsonIgnore]
    public bool IsLoopback => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);
}

public static class ExternalAiProviderPolicy
{
    public const int MaximumEndpointLength = 2_048;
    public const int MaximumModelLength = 128;
    public const int MaximumApiKeyLength = 4_096;

    public static ExternalAiProviderSettings Create(string endpoint, string model, string? apiKey)
    {
        var normalizedEndpoint = NormalizeRequired(endpoint, MaximumEndpointLength, "Ingresa la URL de la API.");
        var normalizedModel = NormalizeRequired(model, MaximumModelLength, "Ingresa el modelo remoto.");
        var normalizedApiKey = NormalizeOptionalSecret(apiKey);

        if (!Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("La URL debe ser HTTP o HTTPS y debe incluir el destino completo.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("La URL no puede incluir usuario ni contraseña.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("La URL no puede incluir parámetros ni fragmentos.");
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            throw new InvalidOperationException("Las API remotas requieren HTTPS. HTTP solo se permite en este equipo.");
        if (!uri.IsLoopback && string.IsNullOrEmpty(normalizedApiKey))
            throw new InvalidOperationException("Ingresa una clave API para el proveedor remoto.");

        return new(uri.AbsoluteUri.TrimEnd('/'), normalizedModel, normalizedApiKey);
    }

    public static string? ResolveApiKey(ExternalAiProviderSettings? existing, string? replacement)
    {
        var normalizedReplacement = NormalizeOptionalSecret(replacement);
        return normalizedReplacement ?? existing?.ApiKey;
    }

    private static string NormalizeRequired(string value, int maximumLength, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(missingMessage);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
            throw new InvalidOperationException($"El valor supera el límite de {maximumLength:N0} caracteres.");
        if (normalized.Any(char.IsControl))
            throw new InvalidOperationException("El valor contiene caracteres de control no permitidos.");
        return normalized;
    }

    private static string? NormalizeOptionalSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > MaximumApiKeyLength)
            throw new InvalidOperationException($"La clave supera el límite de {MaximumApiKeyLength:N0} caracteres.");
        if (normalized.Any(char.IsControl))
            throw new InvalidOperationException("La clave contiene caracteres de control no permitidos.");
        return normalized;
    }
}
