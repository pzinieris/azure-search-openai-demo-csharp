using Shared.Enum;
using Shared.Factory;

namespace EmbedFunctions.Services;

public sealed class EmbeddingAggregateService(
    EmbedServiceFactory embedServiceFactory,
    BlobContainerClientFactory blobContainerClientFactory,
    ILogger<EmbeddingAggregateService> logger)
{
    internal async Task EmbedBlobAsync(Stream blobStream, string blobName)
    {
        try
        {
            // Because below we update the source blob metadata, causing the BlobTrigger to be triggered again,
            // we fist need make sure that this is not a re-triggering before continuing with the intexing
            var sourceStorageContainer = Environment.GetEnvironmentVariable("AzureStorageContainer");
            var sourceBlobContainerClient = await blobContainerClientFactory.GetBlobContainerClientAsync(BlobContainerName.Custom, sourceStorageContainer);

            var blobClient = sourceBlobContainerClient.GetBlobClient(blobName);
            var properties = await blobClient.GetPropertiesAsync();
            var metadata = properties.Value.Metadata;

            if (metadata.TryGetValue(nameof(DocumentProcessingStatus), out var status))
            {
                logger.LogInformation("""Method {MethodName} has finished becasue the blob already has been intexed and has the status '{DocumentProcessingStatus}'""", nameof(EmbedBlobAsync), status);
                return;
            }

            var embeddingType = GetEmbeddingType();
            var embedService = embedServiceFactory.GetEmbedService(embeddingType);

            var result = await embedService.EmbedPDFBlobAsync(blobStream, blobName);

            // TODO: IMPORTANT
            // This below will update the source blob metadata, causing the BlobTrigger to be triggered once again
            var documentProcessingStatus = result ? DocumentProcessingStatus.Succeeded : DocumentProcessingStatus.Failed;
            await embedService.SetBlobMetadataAsync(blobClient, documentProcessingStatus);

            logger.LogInformation("""Method {MethodName} has finished and returned value '{MethodResponse}'""", nameof(embedService.EmbedPDFBlobAsync), result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to embed: {Name}, error: {Message}", blobName, ex.Message);
        }
    }

    private static EmbeddingType GetEmbeddingType() => Environment.GetEnvironmentVariable("EMBEDDING_TYPE") is string type &&
            Enum.TryParse<EmbeddingType>(type, out EmbeddingType embeddingType)
            ? embeddingType
            : EmbeddingType.AzureSearch;
}
