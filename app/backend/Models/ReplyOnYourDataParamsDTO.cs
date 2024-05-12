namespace MinimalApi.Models;

internal record ReplyOnYourDataParamsDTO(ChatCompletionsOptions ChatCompletionsOptions, string AIDeploymentName,
    string AIEmbeddingDeploymentName, string AzureSearchServiceEndpoint, string AzureSearchIndex);
