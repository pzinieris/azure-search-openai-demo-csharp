using Shared.Models;

namespace Shared.Services.Interfaces;

public interface IDocumentParserService
{
    Task<IReadOnlyList<PageDetail>> GetDocumentTextAsync(Stream blobStream, string blobName);
    Task<IReadOnlyList<PageDetail>> GetDocumentTextForPagesWithSplitTablesAsync(Stream blobStream, string blobName);
    //void TryIdentifyHeadersAndFooters();
}
