using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using OpenAI.Embeddings;
using OpenAI.Images;
using Shared.Models.Settings;
using Shared.Services.AI.Interface;

namespace Shared.Services.AI;
public class AzureOpenAIClientService : IAIClientService
{
    public OpenAIClient AIClient => _aiClient;

    #region Private Fields

    private readonly AppSettings _appSettings;
    private readonly AzureOpenAIClient _aiClient;

    #endregion Private Fields

    #region Constructor/s

    public AzureOpenAIClientService(AppSettings appSettings)
    {
        _appSettings = appSettings;
        _aiClient = GetAIClient();
    }

    #endregion Constructor/s

    #region Public methods

    public AudioClient GetAudioClient(string deploymentName)
        => _aiClient.GetAudioClient(deploymentName);

    public ChatClient GetChatClient(string deploymentName)
        => _aiClient.GetChatClient(deploymentName);

    public EmbeddingClient GetEmbeddingClient(string deploymentName)
        => _aiClient.GetEmbeddingClient(deploymentName);

    public ImageClient GetImageClient(string deploymentName)
        => _aiClient.GetImageClient(deploymentName);

    #endregion Public methods

    #region Private Methods

    private Azure.AI.OpenAI.AzureOpenAIClient GetAIClient()
    {
        var azureOpenAiServiceEndpoint = _appSettings.AzureOpenAiServiceEndpoint;
        ArgumentNullException.ThrowIfNullOrEmpty(azureOpenAiServiceEndpoint);

        return new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(azureOpenAiServiceEndpoint),
            //new AzureKeyCredential(azureOpenAiApiKey));
            new DefaultAzureCredential());
    }

    #endregion Private Methods
}
