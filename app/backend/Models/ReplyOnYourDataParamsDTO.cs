using OpenAI.Chat;

namespace MinimalApi.Models;

internal record ReplyOnYourDataParamsDTO(IEnumerable<ChatMessage> ChatMessages, ChatCompletionOptions ChatCompletionsOptions, string AIDeploymentName,
    string AIEmbeddingDeploymentName, string AzureSearchServiceEndpoint, string AzureSearchIndex);
