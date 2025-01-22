using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using OpenAI.Embeddings;
using OpenAI.Images;

namespace Shared.Services.AI.Interface;
public interface IAIClientService
{
    OpenAIClient AIClient { get; }

    AudioClient GetAudioClient(string deploymentName);
    ChatClient GetChatClient(string deploymentName);
    EmbeddingClient GetEmbeddingClient(string deploymentName);
    ImageClient GetImageClient(string deploymentName);
}
