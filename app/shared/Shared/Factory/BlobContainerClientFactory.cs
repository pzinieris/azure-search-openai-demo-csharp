using Azure.Storage.Blobs;
using Shared.Enum;

namespace Shared.Factory;

public sealed class BlobContainerClientFactory(BlobServiceClient blobServiceClient)
{
    #region Public Methods

    public async ValueTask<BlobContainerClient> GetBlobContainerClientAsync(BlobContainerName blobContainerName, string? customBlobContainerName = null)
    {
        var containerClient = GetClient(blobContainerName, customBlobContainerName);
        await containerClient.CreateIfNotExistsAsync();

        return containerClient;
    }

    public BlobContainerClient GetBlobContainerClient(BlobContainerName blobContainerName, string? customBlobContainerName = null)
    {
        var containerClient = GetClient(blobContainerName, customBlobContainerName);
        containerClient.CreateIfNotExists();

        return containerClient;
    }

    #endregion Public Methods

    #region Private Methods

    private BlobContainerClient GetClient(BlobContainerName blobContainerName, string? customBlobContainerName)
    {
        string? containerName = null;

        switch (blobContainerName)
        {
            case BlobContainerName.Corpus:
                containerName = "corpus";
                break;
            case BlobContainerName.Custom:
                containerName = customBlobContainerName;
                break;
            default:
                throw new NotImplementedException($"Unsupported BlobContainerName: {blobContainerName}");
        }

        return blobServiceClient.GetBlobContainerClient(containerName);
    }

    #endregion Private Methods
}
