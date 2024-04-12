using Microsoft.Extensions.Options;
using Shared.Models.Settings;
using Shared.Services;
using Shared.Services.Interfaces;

namespace MinimalApi.Extensions;

internal static class ServiceCollectionExtensions
{
    private static readonly DefaultAzureCredential s_azureCredential = new();

    internal static IServiceCollection AddAzureServices(this IServiceCollection services)
    {
        services.AddSingleton<BlobServiceClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AppSettings>>();

            var azureStorageAccountEndpoint = options.Value.AzureStorageAccountEndpoint;
            ArgumentNullException.ThrowIfNullOrEmpty(azureStorageAccountEndpoint);

            var blobServiceClient = new BlobServiceClient(new Uri(azureStorageAccountEndpoint), s_azureCredential);

            return blobServiceClient;
        });

        services.AddSingleton<BlobContainerClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AppSettings>>();

            var azureStorageContainer = options.Value.AzureStorageContainer;

            return sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(azureStorageContainer);
        });

        services.AddSingleton<ISearchService, AzureSearchService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AppSettings>>();

            var azureSearchServiceEndpoint = options.Value.AzureSearchServiceEndpoint;
            ArgumentNullException.ThrowIfNullOrEmpty(azureSearchServiceEndpoint);

            var azureSearchIndex = options.Value.AzureSearchIndex;
            ArgumentNullException.ThrowIfNullOrEmpty(azureSearchIndex);

            var searchClient = new SearchClient(new Uri(azureSearchServiceEndpoint), azureSearchIndex, s_azureCredential);

            return new AzureSearchService(searchClient);
        });

        services.AddSingleton<DocumentAnalysisClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AppSettings>>();

            var azureFromRecognizerServiceEndpoint = options.Value.AzureFromRecognizerServiceEndpoint ?? throw new ArgumentNullException();

            return new DocumentAnalysisClient(new Uri(azureFromRecognizerServiceEndpoint), s_azureCredential);
        });

        services.AddSingleton<OpenAIClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AppSettings>>();
            var settings = options.Value;

            if (settings.UseAOAI)
            {
                var azureOpenAiServiceEndpoint = settings.AzureOpenAiServiceEndpoint;
                ArgumentNullException.ThrowIfNullOrEmpty(azureOpenAiServiceEndpoint);

                var openAIClient = new OpenAIClient(
                    new Uri(azureOpenAiServiceEndpoint),
                    //new AzureKeyCredential(azureOpenAiApiKey));
                    s_azureCredential);

                return openAIClient;
            }
            else
            {
                var openAIApiKey = settings.OpenAIApiKey;
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

            var options = sp.GetRequiredService<IOptions<AppSettings>>();
            var settings = options.Value;

            if (settings.UseVision)
            {
                var azureComputerVisionServiceEndpoint = settings.AzureComputerVisionServiceEndpoint;
                ArgumentNullException.ThrowIfNullOrEmpty(azureComputerVisionServiceEndpoint);

                var azureComputerVisionServiceApiVersion = settings.AzureComputerVisionServiceApiVersion;
                if (string.IsNullOrWhiteSpace(azureComputerVisionServiceApiVersion))
                {
                    azureComputerVisionServiceApiVersion = "2024-02-01";
                }

                var azureComputerVisionServiceModelVersion = settings.AzureComputerVisionServiceModelVersion;
                if (string.IsNullOrWhiteSpace(azureComputerVisionServiceModelVersion))
                {
                    azureComputerVisionServiceModelVersion = "2023-04-15";
                }

                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();

                var visionService = new AzureComputerVisionService(httpClient, azureComputerVisionServiceEndpoint,
                    azureComputerVisionServiceApiVersion, azureComputerVisionServiceModelVersion, s_azureCredential);
                return new ReadRetrieveReadChatService(logger, searchClient, openAIClient, settings, visionService, s_azureCredential);
            }
            else
            {
                return new ReadRetrieveReadChatService(logger, searchClient, openAIClient, settings, tokenCredential: s_azureCredential);
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
