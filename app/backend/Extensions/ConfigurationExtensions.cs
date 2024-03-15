using Shared.Models.Settings;

namespace MinimalApi.Extensions;

internal static class ConfigurationExtensions
{
    internal static string GetStorageAccountEndpoint(this AppSettings settings)
    {
        var endpoint = settings.AzureStorageAccountEndpoint;
        ArgumentNullException.ThrowIfNullOrEmpty(endpoint);

        return endpoint;
    }

    internal static string ToCitationBaseUrl(this AppSettings settings)
    {
        var endpoint = settings.GetStorageAccountEndpoint();

        var builder = new UriBuilder(endpoint)
        {
            Path = settings.AzureStorageContainer
        };

        return builder.Uri.AbsoluteUri;
    }
}
