using Azure.AI.OpenAI;
using Shared.Enum;
using Shared.Factory;
using Shared.Services;
using Shared.Services.Interfaces;

var host = new HostBuilder()
    .ConfigureServices(services =>
    {
        static Uri GetUriFromEnvironment(string variable) =>
            Environment.GetEnvironmentVariable(variable) is string value
            && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri is not null
                ? uri
                : throw new ArgumentException($"Unable to parse URI from environment variable: {variable}");

        var credential = new DefaultAzureCredential();

        services.AddHttpClient();

        services.AddAzureClients(builder =>
        {
            builder.AddDocumentAnalysisClient(
                GetUriFromEnvironment("AZURE_FORMRECOGNIZER_SERVICE_ENDPOINT"));
        });

        services.AddSingleton<SearchClient>(_ =>
        {
            return new SearchClient(
                GetUriFromEnvironment("AZURE_SEARCH_SERVICE_ENDPOINT"),
                Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX"),
                credential);
        });

        services.AddSingleton<SearchIndexClient>(_ =>
        {
            return new SearchIndexClient(
                GetUriFromEnvironment("AZURE_SEARCH_SERVICE_ENDPOINT"),
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
                GetUriFromEnvironment("AZURE_STORAGE_BLOB_ENDPOINT"),
                credential);

            return blobServiceClient;
        });
        services.AddSingleton<BlobContainerClientFactory>();

        services.AddSingleton<EmbedServiceFactory>();
        services.AddSingleton<EmbeddingAggregateService>();

        services.AddSingleton<IEmbedService, AzureSearchEmbedService>(provider =>
        {
            var searchIndexName = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX") ?? throw new ArgumentNullException("AZURE_SEARCH_INDEX is null");
            var embeddingModelName = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? throw new ArgumentNullException("AZURE_OPENAI_EMBEDDING_DEPLOYMENT is null");
            var openaiEndPoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new ArgumentNullException("AZURE_OPENAI_ENDPOINT is null");

            var openAIClient = new OpenAIClient(new Uri(openaiEndPoint), credential);

            var searchClient = provider.GetRequiredService<SearchClient>();
            var searchIndexClient = provider.GetRequiredService<SearchIndexClient>();
            //var blobContainerClient = provider.GetRequiredService<BlobContainerClient>();
            var documentClient = provider.GetRequiredService<DocumentAnalysisClient>();
            var logger = provider.GetRequiredService<ILogger<AzureSearchEmbedService>>();

            var blobContainerClientFactory = provider.GetRequiredService<BlobContainerClientFactory>();
            var corpusContainerClient = blobContainerClientFactory.GetBlobContainerClient(BlobContainerName.Corpus);

            // Vision Service
            var azureComputerVisionServiceEndpoint = Environment.GetEnvironmentVariable("AzureComputerVisionServiceEndpoint");
            ArgumentNullException.ThrowIfNullOrEmpty(azureComputerVisionServiceEndpoint);

            var azureComputerVisionServiceApiVersion = Environment.GetEnvironmentVariable("AzureComputerVisionServiceApiVersion");
            if (string.IsNullOrWhiteSpace(azureComputerVisionServiceApiVersion))
            {
                azureComputerVisionServiceApiVersion = "2024-02-01";
            }

            var azureComputerVisionServiceModelVersion = Environment.GetEnvironmentVariable("AzureComputerVisionServiceModelVersion");
            if (string.IsNullOrWhiteSpace(azureComputerVisionServiceModelVersion))
            {
                azureComputerVisionServiceModelVersion = "2023-04-15";
            }

            var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient();

            var visionService = new AzureComputerVisionService(httpClient, azureComputerVisionServiceEndpoint,
                azureComputerVisionServiceApiVersion, azureComputerVisionServiceModelVersion, credential);
            
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
