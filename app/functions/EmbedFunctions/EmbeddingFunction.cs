using Microsoft.Azure.Functions.Worker;

public sealed class EmbeddingFunction(
    EmbeddingAggregateService embeddingAggregateService)
{
    [Function(name: nameof(EmbeddingFunction))]
    public Task EmbedAsync(
        [BlobTrigger(
            blobPath: "content/{name}",
            Connection = "AzureWebJobsStorage")] Stream blobStream,
        string name) => embeddingAggregateService.EmbedBlobAsync(blobStream, blobName: name);
}
