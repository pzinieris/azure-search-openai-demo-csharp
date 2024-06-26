﻿namespace PrepareDocs;

internal record class AppOptions(
    string Files,
    string? Category,
    bool SkipBlobs,
    string? StorageServiceBlobEndpoint,
    string? Container,
    string? TenantId,
    string? SearchServiceEndpoint,
    string? AzureOpenAIServiceEndpoint,
    string? SearchIndexName,
    string? EmbeddingModelName,
    bool Remove,
    bool RemoveAll,
    string? FormRecognizerServiceEndpoint,
    string? ComputerVisionServiceEndpoint,
    string? ComputerVisionServiceApiVersion,
    string? ComputerVisionServiceModelVersion,
    bool Verbose,
    IConsole Console) : AppConsole(Console);

internal record class AppConsole(IConsole Console);
