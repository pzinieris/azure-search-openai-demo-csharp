using Azure.Extensions.AspNetCore.Configuration.Secrets;

namespace MinimalApi.Extensions;

internal static class KeyVaultConfigurationBuilderExtensions
{
    internal static ConfigurationManager ConfigureAzureKeyVault(this ConfigurationManager builder)
    {
        var azureKeyVaultEndpoint  = builder["AZURE_KEY_VAULT_ENDPOINT"];
        //var azureKeyVaultEndpoint = Environment.GetEnvironmentVariable("AZURE_KEY_VAULT_ENDPOINT");
        ArgumentNullException.ThrowIfNullOrEmpty(azureKeyVaultEndpoint);

        builder.AddAzureKeyVault(
            new Uri(azureKeyVaultEndpoint), new DefaultAzureCredential(), new AzureKeyVaultConfigurationOptions
            {
                Manager = new KeyVaultSecretManager(),
                // Reload the KeyVauld secrets once every day
                ReloadInterval = TimeSpan.FromDays(1)
            });

        return builder;
    }
}
