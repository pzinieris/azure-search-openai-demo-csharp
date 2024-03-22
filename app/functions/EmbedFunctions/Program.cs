using Azure.AI.OpenAI;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Microsoft.Extensions.Configuration;
using Shared.Enum;
using Shared.Factory;
using Shared.Models.Settings;
using Shared.Services;
using Shared.Services.Interfaces;

var host = new HostBuilder()
    .ConfigureAppConfiguration(configurationBuilder =>
    {
        var builtConfig = configurationBuilder.Build();
        var azureKeyVaultEndpoint = builtConfig["AZURE_KEY_VAULT_ENDPOINT"];

        ArgumentNullException.ThrowIfNullOrEmpty(azureKeyVaultEndpoint);

        configurationBuilder.AddAzureKeyVault(
            new Uri(azureKeyVaultEndpoint), new DefaultAzureCredential(), new AzureKeyVaultConfigurationOptions
            {
                Manager = new KeyVaultSecretManager(),
                // Reload the KeyVauld secrets once every day
                ReloadInterval = TimeSpan.FromDays(1)
            });

        configurationBuilder.Build();
    })
    .ConfigureServices((hostContext, services) =>
    {
        static Uri GetUriFromValue(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri is not null
                ? uri
                : throw new ArgumentException($"Unable to parse URI from value: {(value != null ? value : "null")}");

        var credential = new DefaultAzureCredential();

        #region AppSettings

        services
            .AddOptions<AppSettings>()
            .Configure<IConfiguration>((settings, config) =>
            {
                config.Bind(settings);
            });

        var appSettings = hostContext.Configuration.Get<AppSettings>();
        services.AddSingleton(appSettings);

        #endregion AppSettings

        services.AddHttpClient();

        services.AddAzureClients(builder =>
        {
            builder.AddDocumentAnalysisClient(
                GetUriFromValue(appSettings.AzureFromRecognizerServiceEndpoint));
        });

        services.AddSingleton<SearchClient>(_ =>
        {
            return new SearchClient(
                GetUriFromValue(appSettings.AzureSearchServiceEndpoint),
                appSettings.AzureSearchIndex,
                credential);
        });

        services.AddSingleton<SearchIndexClient>(_ =>
        {
            return new SearchIndexClient(
                GetUriFromValue(appSettings.AzureSearchServiceEndpoint),
                credential);
        });

        //services.AddSingleton<BlobContainerClient>(_ =>
        //{
        //    var blobServiceClient = new BlobServiceClient(
        //        GetUriFromEnvironment("AZURE_STORAGE_BLOB_ENDPOINT"),
        //        credential);

        //    var containerClient = blobServiceClient.GetBlobContainerClient("corpus");

        //    containerClient.CreateIfNotExists();

        //    return containerClient;
        //});
        services.AddSingleton<BlobServiceClient>(_ =>
        {
            var blobServiceClient = new BlobServiceClient(
                GetUriFromValue(appSettings.AzureStorageAccountEndpoint),
                credential);

            return blobServiceClient;
        });
        services.AddSingleton<BlobContainerClientFactory>();

        services.AddSingleton<EmbedServiceFactory>();
        services.AddSingleton<EmbeddingAggregateService>();

        services.AddSingleton<IEmbedService, AzureSearchEmbedService>(provider =>
        {
            var searchIndexName = appSettings.AzureSearchIndex;
            ArgumentNullException.ThrowIfNullOrWhiteSpace(searchIndexName);

            var embeddingModelName = appSettings.AzureOpenAiEmbeddingDeployment;
            ArgumentNullException.ThrowIfNullOrWhiteSpace(embeddingModelName);

            var openaiEndPoint = appSettings.AzureOpenAiServiceEndpoint;
            ArgumentNullException.ThrowIfNullOrWhiteSpace(openaiEndPoint);

            var openAIClient = new OpenAIClient(new Uri(openaiEndPoint), credential);

            var searchClient = provider.GetRequiredService<SearchClient>();
            var searchIndexClient = provider.GetRequiredService<SearchIndexClient>();
            //var blobContainerClient = provider.GetRequiredService<BlobContainerClient>();
            var documentClient = provider.GetRequiredService<DocumentAnalysisClient>();
            var logger = provider.GetRequiredService<ILogger<AzureSearchEmbedService>>();

            var blobContainerClientFactory = provider.GetRequiredService<BlobContainerClientFactory>();
            var corpusContainerClient = blobContainerClientFactory.GetBlobContainerClient(BlobContainerName.Corpus);

            // Vision Service
            AzureComputerVisionService? visionService = null;
            if (appSettings.UseVision)
            {
                var azureComputerVisionServiceEndpoint = appSettings.AzureComputerVisionServiceEndpoint;
                ArgumentNullException.ThrowIfNullOrWhiteSpace(azureComputerVisionServiceEndpoint);

                var azureComputerVisionServiceApiVersion = appSettings.AzureComputerVisionServiceApiVersion;
                if (string.IsNullOrWhiteSpace(azureComputerVisionServiceApiVersion))
                {
                    azureComputerVisionServiceApiVersion = "2024-02-01";
                }

                var azureComputerVisionServiceModelVersion = appSettings.AzureComputerVisionServiceModelVersion;
                if (string.IsNullOrWhiteSpace(azureComputerVisionServiceModelVersion))
                {
                    azureComputerVisionServiceModelVersion = "2023-04-15";
                }

                var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient();

                visionService = new AzureComputerVisionService(httpClient, azureComputerVisionServiceEndpoint,
                    azureComputerVisionServiceApiVersion, azureComputerVisionServiceModelVersion, credential);
            }

            return new AzureSearchEmbedService(
                openAIClient: openAIClient,
                embeddingModelName: embeddingModelName,
                searchClient: searchClient,
                searchIndexName: searchIndexName,
                searchIndexClient: searchIndexClient,
                documentAnalysisClient: documentClient,
                corpusContainerClient: corpusContainerClient,
                computerVisionService: visionService,
                includeImageEmbeddingsField: true,
                logger: logger);
        });
    })
    .ConfigureFunctionsWorkerDefaults()
    .Build();

host.Run();
