using Azure.Storage.Blobs;
using Shared.Models;

namespace Shared.Services.Interfaces;

public interface IEmbedService : IDocumentParserService
{
    /// <summary>
    /// Embeds the given pdf blob into the embedding service.
    /// </summary>
    /// <param name="blobStream">The stream from the blob to embed.</param>
    /// <param name="blobName">The name of the blob.</param>
    /// <returns>
    /// An asynchronous operation that yields <c>true</c>
    /// when successfully embedded, otherwise <c>false</c>.
    /// </returns>
    Task<bool> EmbedPDFBlobAsync(Stream blobStream, string blobName);

    /// <summary>
    /// Embeds the pages that have tables that are split into multiple pages, and then deletes the source document.
    /// </summary>
    /// <param name="pdfBlobStream"></param>
    /// <param name="blobName"></param>
    /// <returns></returns>
    Task<bool> EmbedPDFBlobPagesWithSplitTablesAsync(Stream pdfBlobStream, string blobName);

    /// <summary>
    /// Embeds the given image blob into the embedding service.
    /// </summary>
    Task<bool> EmbedImageBlobAsync(Stream imageStream, string imageUrl, string imageName, CancellationToken ct = default);

    Task CreateSearchIndexAsync(string searchIndexName, CancellationToken ct = default);

    Task EnsureSearchIndexAsync(string searchIndexName, CancellationToken ct = default);

    Task SetBlobMetadataAsync(BlobClient blobClient, DocumentProcessingStatus documentProcessingStatus);
}
