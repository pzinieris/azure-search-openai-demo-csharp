using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Shared.Models;
using Shared.Services.Interfaces;

namespace Shared.Services;

public sealed class PineconeEmbedService : AzureFormRecognizerDocumentParserService, IEmbedService
{
    #region Private Fields

    private readonly ILogger? _logger;
    private readonly DocumentAnalysisClient _documentAnalysisClient;

    #endregion Private Fields

    #region Contructor/s

    public PineconeEmbedService(ILogger? logger, DocumentAnalysisClient documentAnalysisClient)
        : base(logger, documentAnalysisClient)
    {
        _logger = logger;
        _documentAnalysisClient = documentAnalysisClient;
    }

    #endregion Contructor/s

    #region Public Methods

    public Task<bool> EmbedPDFBlobAsync(Stream blobStream, string blobName) => throw new NotImplementedException();

    public Task<bool> EmbedImageBlobAsync(Stream imageStream, string imageUrl, string imageName, CancellationToken ct = default) => throw new NotImplementedException();

    public Task CreateSearchIndexAsync(string searchIndexName, CancellationToken ct = default) => throw new NotImplementedException();

    public Task EnsureSearchIndexAsync(string searchIndexName, CancellationToken ct = default) => throw new NotImplementedException();

    public Task SetBlobMetadataAsync(BlobClient blobClient, DocumentProcessingStatus documentProcessingStatus) => throw new NotImplementedException();

    #endregion Public Methods
}
