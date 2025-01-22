using Microsoft.Extensions.Configuration;
using Shared.Enum;
using Shared.Factory;
using Shared.Models.Settings;

namespace EmbedFunctions.Services;

public class EmbeddingAggregateService
{
    #region Fields

    private readonly EmbedServiceFactory _embedServiceFactory;
    private readonly BlobContainerClientFactory _blobContainerClientFactory;
    private readonly ILogger<EmbeddingAggregateService> _logger;
    private readonly AppSettings _appSettings;

    #endregion Fields

    #region Contructor/s

    public EmbeddingAggregateService(EmbedServiceFactory embedServiceFactory, BlobContainerClientFactory blobContainerClientFactory,
        ILogger<EmbeddingAggregateService> logger, IConfiguration config, AppSettings appSettings)
    {
        _embedServiceFactory = embedServiceFactory;
        _blobContainerClientFactory = blobContainerClientFactory;
        _logger = logger;
        _appSettings = appSettings;
    }

    #endregion Contructor/s

    internal async Task EmbedBlobAsync(Stream blobStream, string blobName)
    {
        try
        {
            // Because below we update the source blob metadata, causing the BlobTrigger to be triggered again,
            // we fist need make sure that this is not a re-triggering before continuing with the indexing
            var sourceStorageContainer = _appSettings.AzureStorageContainer;
            var sourceBlobContainerClient = await _blobContainerClientFactory.GetBlobContainerClientAsync(BlobContainerName.Custom, sourceStorageContainer);

            var blobClient = sourceBlobContainerClient.GetBlobClient(blobName);
            var properties = await blobClient.GetPropertiesAsync();
            var metadata = properties.Value.Metadata;
            DocumentProcessingStatus? currentDocumentProcessingStatus = null;

            if (metadata.TryGetValue(nameof(DocumentProcessingStatus), out var status))
            {
                if (Enum.TryParse(status, out DocumentProcessingStatus enumStatus)
                    && enumStatus == DocumentProcessingStatus.NotProcessed_ToBeDeleted)
                {
                    // Allow the method to continue
                    currentDocumentProcessingStatus = enumStatus;
                }
                else
                {
                    _logger.LogInformation("""Method {MethodName} has finished because the blob already has been indexed and has the status '{DocumentProcessingStatus}'""", nameof(EmbedBlobAsync), status);
                    return;
                }
            }

            var embeddingType = GetEmbeddingType();
            var embedService = _embedServiceFactory.GetEmbedService(embeddingType);

            bool documentEmbeded;
            if (currentDocumentProcessingStatus.HasValue
                && currentDocumentProcessingStatus.Value == DocumentProcessingStatus.NotProcessed_ToBeDeleted)
            {
                documentEmbeded = await embedService.EmbedPDFBlobPagesWithSplitTablesAsync(blobStream, blobName);

                // Finally, delete the source blob
                await blobClient.DeleteIfExistsAsync();
            }
            else
            {
                documentEmbeded = await embedService.EmbedPDFBlobAsync(blobStream, blobName);

                // TODO: IMPORTANT
                // This below will update the source blob metadata, causing the BlobTrigger to be triggered once again
                var documentProcessingStatus = documentEmbeded ? DocumentProcessingStatus.Succeeded : DocumentProcessingStatus.Failed;
                await embedService.SetBlobMetadataAsync(blobClient, documentProcessingStatus);
            }

            _logger.LogInformation("""Method {MethodName} has finished and returned value '{MethodResponse}'""", nameof(embedService.EmbedPDFBlobAsync), documentEmbeded);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to embed: {Name}, error: {Message}", blobName, ex.ToString());
        }
    }

    internal void TryIdentifyHeadersAndFootersAsync()
    {
        var embeddingType = GetEmbeddingType();
        var embedService = _embedServiceFactory.GetEmbedService(embeddingType);

        string filePath = @"C:\Workspace\git\AzureSearchOpenai\azure-search-openai-demo-csharp\app\shared\Shared\Services\DocumentParser\DemoDocs\test 1.pdf";

        MemoryStream memoryStream = new MemoryStream();
        using FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        {
            fileStream.CopyTo(memoryStream);
        }

        memoryStream.Position = 0;

        embedService.EmbedPDFBlobPagesWithSplitTablesAsync(memoryStream, "JCCgatewaySubscriptionPayments_18Mar2022_v2.1-4.pdf");
    }

    private static EmbeddingType GetEmbeddingType() => Environment.GetEnvironmentVariable("EMBEDDING_TYPE") is string type &&
            Enum.TryParse<EmbeddingType>(type, out EmbeddingType embeddingType)
            ? embeddingType
            : EmbeddingType.AzureSearch;
}
