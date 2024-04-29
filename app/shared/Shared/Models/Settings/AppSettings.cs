namespace Shared.Models.Settings;

public sealed class AppSettings
{
    // The KeyVault with all the secrets
    public string? AZURE_KEY_VAULT_ENDPOINT { get; set; }

    public bool UseAOAI { get; set; }
    // Open AI
    public string? OpenAIApiKey { get; set; }
    public string? OpenAiChatGptDeployment { get; set; }
    public string? OpenAiEmbeddingDeployment { get; set; }    
    // Azure Open AI
    public string? AzureOpenAiChatGptDeployment { get; set; }
    public string? AzureOpenAiEmbeddingDeployment { get; set; }
    public string? AzureOpenAiServiceEndpoint { get; set; }

    // Azure Open AI GPT4 with Vision
    public bool UseVision { get; set; }
    public string? AzureComputerVisionServiceEndpoint { get; set; }
    public string? AzureComputerVisionServiceApiVersion { get; set; }
    public string? AzureComputerVisionServiceModelVersion { get; set; }

    public string? AzureFromRecognizerServiceEndpoint { get; set; }

    public string? AzureStorageAccountEndpoint { get; set; }
    public string? AzureStorageContainer { get; set; }

    public string? AzureSearchServiceEndpoint { get; set; }
    public string? AzureSearchIndex { get; set; }
}
