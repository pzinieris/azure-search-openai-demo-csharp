﻿using Shared.Services;
using Shared.Services.Interfaces;

namespace MinimalApi.Extensions;

internal static class ServiceCollectionExtensions
{
    private static readonly DefaultAzureCredential s_azureCredential = new();

    internal static IServiceCollection AddAzureServices(this IServiceCollection services)
    {
        services.AddSingleton<BlobServiceClient>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var azureStorageAccountEndpoint = config["AzureStorageAccountEndpoint"];
            ArgumentNullException.ThrowIfNullOrEmpty(azureStorageAccountEndpoint);

            var blobServiceClient = new BlobServiceClient(
                new Uri(azureStorageAccountEndpoint), s_azureCredential);

            return blobServiceClient;
        });

        services.AddSingleton<BlobContainerClient>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var azureStorageContainer = config["AzureStorageContainer"];
            return sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(azureStorageContainer);
        });

        services.AddSingleton<ISearchService, AzureSearchService>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var azureSearchServiceEndpoint = config["AzureSearchServiceEndpoint"];
            ArgumentNullException.ThrowIfNullOrEmpty(azureSearchServiceEndpoint);

            var azureSearchIndex = config["AzureSearchIndex"];
            ArgumentNullException.ThrowIfNullOrEmpty(azureSearchIndex);

            var searchClient = new SearchClient(
                               new Uri(azureSearchServiceEndpoint), azureSearchIndex, s_azureCredential);

            return new AzureSearchService(searchClient);
        });

        services.AddSingleton<DocumentAnalysisClient>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var azureOpenAiServiceEndpoint = config["AzureOpenAiServiceEndpoint"] ?? throw new ArgumentNullException();

            var documentAnalysisClient = new DocumentAnalysisClient(
                new Uri(azureOpenAiServiceEndpoint), s_azureCredential);
            return documentAnalysisClient;
        });

        services.AddSingleton<OpenAIClient>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var useAOAI = config["UseAOAI"] == "true";
            if (useAOAI)
            {
                var azureOpenAiServiceEndpoint = config["AzureOpenAiServiceEndpoint"];
                ArgumentNullException.ThrowIfNullOrEmpty(azureOpenAiServiceEndpoint);

                var openAIClient = new OpenAIClient(
                    new Uri(azureOpenAiServiceEndpoint),
                    //new AzureKeyCredential(azureOpenAiApiKey));
                    s_azureCredential);

                return openAIClient;
            }
            else
            {
                var openAIApiKey = config["OpenAIApiKey"];
                ArgumentNullException.ThrowIfNullOrEmpty(openAIApiKey);

                var openAIClient = new OpenAIClient(openAIApiKey);
                return openAIClient;
            }
        });

        services.AddSingleton<AzureBlobStorageService>();
        services.AddSingleton<ReadRetrieveReadChatService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ReadRetrieveReadChatService>>();
            var openAIClient = sp.GetRequiredService<OpenAIClient>();
            var searchClient = sp.GetRequiredService<ISearchService>();

            var config = sp.GetRequiredService<IConfiguration>();
            var useGPT4V = config["UseGPT4V"] == "true";
            if (useGPT4V)
            {
                var azureComputerVisionServiceEndpoint = config["AzureComputerVisionServiceEndpoint"];
                ArgumentNullException.ThrowIfNullOrEmpty(azureComputerVisionServiceEndpoint);

                var azureComputerVisionServiceApiVersion = config["AzureComputerVisionServiceApiVersion"];
                if (string.IsNullOrWhiteSpace(azureComputerVisionServiceApiVersion))
                {
                    azureComputerVisionServiceApiVersion = "2024-02-01";
                }

                var azureComputerVisionServiceModelVersion = config["AzureComputerVisionServiceModelVersion"];
                if (string.IsNullOrWhiteSpace(azureComputerVisionServiceModelVersion))
                {
                    azureComputerVisionServiceModelVersion = "2023-04-15";
                }

                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();

                var visionService = new AzureComputerVisionService(httpClient, azureComputerVisionServiceEndpoint,
                    azureComputerVisionServiceApiVersion, azureComputerVisionServiceModelVersion, s_azureCredential);
                return new ReadRetrieveReadChatService(logger, searchClient, openAIClient, config, visionService, s_azureCredential);
            }
            else
            {
                return new ReadRetrieveReadChatService(logger, searchClient, openAIClient, config, tokenCredential: s_azureCredential);
            }
        });

        return services;
    }

    internal static IServiceCollection AddCrossOriginResourceSharing(this IServiceCollection services)
    {
        services.AddCors(
            options =>
                options.AddDefaultPolicy(
                    policy =>
                        policy.AllowAnyOrigin()
                            .AllowAnyHeader()
                            .AllowAnyMethod()));

        return services;
    }
}
