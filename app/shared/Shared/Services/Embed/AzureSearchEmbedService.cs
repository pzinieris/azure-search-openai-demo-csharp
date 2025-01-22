using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;
using Shared.Domain;
using Shared.Enum;
using Shared.Models;
using Shared.Services.AI.Interface;
using Shared.Services.Interfaces;

namespace Shared.Services;

public sealed partial class AzureSearchEmbedService : AzureFormRecognizerDocumentParserService, IEmbedService
{
    #region Private Fields

    private readonly IAIClientService _clientService;
    private readonly string _embeddingModelName;
    private readonly SearchClient _searchClient;
    private readonly string _searchIndexName;
    private readonly SearchIndexClient _searchIndexClient;
    private readonly DocumentAnalysisClient _documentAnalysisClient;
    private readonly BlobContainerClient _corpusContainerClient;
    private readonly BlobContainerClient _documentsStorageContainerClient;
    private readonly IComputerVisionService? _computerVisionService;
    private readonly bool _includeImageEmbeddingsField;
    private readonly ILogger<AzureSearchEmbedService>? _logger;


    #endregion Private Fields

    #region Contructor/s

    public AzureSearchEmbedService(IAIClientService clientService, string embeddingModelName, SearchClient searchClient, string searchIndexName, SearchIndexClient searchIndexClient,
        DocumentAnalysisClient documentAnalysisClient, BlobContainerClient corpusContainerClient, BlobContainerClient documentsStorageContainerClient,
        IComputerVisionService? computerVisionService = null, bool includeImageEmbeddingsField = false, ILogger<AzureSearchEmbedService>? logger = null)
        : base(logger, documentAnalysisClient)
    {
        _clientService = clientService;
        _embeddingModelName = embeddingModelName;
        _searchClient = searchClient;
        _searchIndexName = searchIndexName;
        _searchIndexClient = searchIndexClient;
        _documentAnalysisClient = documentAnalysisClient;
        _corpusContainerClient = corpusContainerClient;
        _documentsStorageContainerClient = documentsStorageContainerClient;
        _computerVisionService = computerVisionService;
        _includeImageEmbeddingsField = includeImageEmbeddingsField;
        _logger = logger;
    }

    #endregion Contructor/s

    #region Public Methods

    public async Task<bool> EmbedPDFBlobAsync(Stream pdfBlobStream, string blobName)
    {
        try
        {
            await EnsureSearchIndexAsync(_searchIndexName);
            Console.WriteLine($"""Embedding blob '{blobName}'""");
            var pageMap = await GetDocumentTextAsync(pdfBlobStream, blobName);

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(blobName);

            var corpusNames = new List<string>();
            // Create corpus from page map and upload to blob
            // Corpus name format: fileName-{page}.txt
            foreach (var page in pageMap)
            {
                var corpusName = $"""{fileNameWithoutExtension}.txt""";
                await UploadCorpusAsync(corpusName, page.Text);

                corpusNames.Add(corpusName);
            }

            // Split blob into sections
            var sections = CreateSections(pageMap, blobName);

            _logger?.LogInformation("""Indexing sections from '{BlobName}' into search index '{SearchIndexName}'""", blobName, _searchIndexName);

            // Index the sections into AzureSearch service
            await IndexSectionsAsync(sections);

            // Finally, the blob metadata for all files added into the blob
            await SetCorpusBlobMetadataAsSucceededStatusAsync(corpusNames);

            return true;
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, """Failed to embed blob '{BlobName}'""", blobName);

            throw;
        }
    }

    public async Task<bool> EmbedPDFBlobPagesWithSplitTablesAsync(Stream pdfBlobStream, string blobName)
    {
        try
        {
            await EnsureSearchIndexAsync(_searchIndexName);
            Console.WriteLine($"""Embedding blob '{blobName}'""");
            var pageMap = await GetDocumentTextForPagesWithSplitTablesAsync(pdfBlobStream, blobName);

            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(blobName);
            var fileExtension = Path.GetExtension(blobName);

            var corpusNames = new List<string>();
            IList<Section> pagesToIndex = new List<Section>();
            // Create content document and upload to blob
            // Create corpus from page map and upload to blob
            // Corpus name format: fileName-{fromPage}to{toPage}.txt
            foreach (var page in pageMap)
            {
                var corpusName = $"""{fileNameWithoutExtension}-{page.FromPageNumber}to{page.ToPageNumber}.txt""";
                await UploadCorpusAsync(corpusName, page.Text);

                corpusNames.Add(corpusName);

                // We are creating the sourceFile string with all the blob files referenced by the AI search index
                string blobNames = "";
                for (var x = page.FromPageNumber; x <= page.ToPageNumber; x++)
                {
                    if (blobNames != "")
                    {
                        blobNames += ",";
                    }

                    blobNames += $"""{fileNameWithoutExtension}-{x}{fileExtension}""";
                }

                // Here also we are creating the Indexing sections for the AI Search
                pagesToIndex.Add(new Section(
                    Id: MatchInSetRegex().Replace($"""{blobName}-{page.FromPageNumber}to{page.ToPageNumber}""", """_""").TrimStart('_'),
                    Content: page.Text,
                    SourcePage: BlobNameFromFilePage(blobNames, FindPage(pageMap, 0)),
                    SourceFile: blobNames));
            }

            _logger?.LogInformation("""Indexing sections from '{BlobName}' into search index '{SearchIndexName}'""", blobName, _searchIndexName);

            // Index the sections into AzureSearch service
            await IndexSectionsAsync(pagesToIndex);

            // Update the corpus blob metadata for all files added into the blob
            await SetCorpusBlobMetadataAsSucceededStatusAsync(corpusNames);

            return true;
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, """Failed to embed blob '{BlobName}'""", blobName);

            throw;
        }
    }

    public async Task<bool> EmbedImageBlobAsync(
        Stream imageStream,
        string imageUrl,
        string imageName,
        CancellationToken ct = default)
    {
        if (_includeImageEmbeddingsField == false || _computerVisionService is null)
        {
            throw new InvalidOperationException(
                """Computer Vision service is required to include image embeddings field, please enable GPT_4V support""");
        }

        var embeddings = await _computerVisionService.VectorizeImageAsync(imageUrl, ct);

        // id can only contain letters, digits, underscore (_), dash (-), or equal sign (=).
        var imageId = MatchInSetRegex().Replace(imageUrl, """_""").TrimStart('_');
        // step 3
        // index image embeddings
        var indexAction = new IndexDocumentsAction<VectorizeSearchEntity>(
            IndexActionType.MergeOrUpload,
            VectorizeSearchEntity.CreateImage(imageId, imageName, imageUrl, embeddings.vector));

        var batch = new IndexDocumentsBatch<VectorizeSearchEntity>();
        batch.Actions.Add(indexAction);
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: ct);

        return true;
    }

    public async Task CreateSearchIndexAsync(string searchIndexName, CancellationToken ct = default)
    {
        string vectorSearchConfigName = """my-vector-config""";
        string vectorSearchProfile = """my-vector-profile""";

        var index = new SearchIndex(searchIndexName)
        {
            VectorSearch = new()
            {
                Algorithms =
                {
                    new HnswAlgorithmConfiguration(vectorSearchConfigName)
                },
                Profiles =
                {
                    new VectorSearchProfile(vectorSearchProfile, vectorSearchConfigName)
                }
            },
            Fields =
            {
                new SimpleField(VectorizeSearchEntity.IdAsJsonPropertyName(), SearchFieldDataType.String) { IsKey = true },
                new SearchableField(VectorizeSearchEntity.ContentAsJsonPropertyName()) { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SimpleField(VectorizeSearchEntity.CategoryAsJsonPropertyName(), SearchFieldDataType.String) { IsFacetable = true },
                new SimpleField(VectorizeSearchEntity.SourcePageAsJsonPropertyName(), SearchFieldDataType.String) { IsFacetable = true },
                new SimpleField(VectorizeSearchEntity.SourceFileAsJsonPropertyName(), SearchFieldDataType.String) { IsFacetable = true },
                new SearchField(VectorizeSearchEntity.EmbeddingAsJsonPropertyName(), SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    VectorSearchDimensions = 1536,
                    IsSearchable = true,
                    VectorSearchProfileName = vectorSearchProfile,
                }
            },
            SemanticSearch = new()
            {
                Configurations =
                {
                    new SemanticConfiguration("""default""", new()
                    {
                        ContentFields =
                        {
                            new SemanticField("""content""")
                        }
                    })
                }
            }
        };

        _logger?.LogInformation("""Creating '{SearchIndexName}' search index""", searchIndexName);

        if (_includeImageEmbeddingsField)
        {
            if (_computerVisionService is null)
            {
                throw new InvalidOperationException("""Computer Vision service is required to include image embeddings field""");
            }

            index.Fields.Add(new SearchField(VectorizeSearchEntity.ImageEmbeddingAsJsonPropertyName(), SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = _computerVisionService.Dimension,
                IsSearchable = true,
                VectorSearchProfileName = vectorSearchProfile,
            });
        }

        try
        {
            await _searchIndexClient.CreateIndexAsync(index);
        }
        catch (RequestFailedException failedException)
        {
            // This can happen on an initial creation of the index, were multiple blobs are trying to create the same index at the same time
            if (failedException.Status == (int)HttpStatusCode.Conflict)
            {
                _logger?.LogWarning("""'{MethodName}' failed with the response code '{ResponseCode}' and message: '{ResponseMessage}'. Exception: {Exception}""",
                    nameof(_searchIndexClient.CreateIndexAsync), failedException.Status, failedException.Message, failedException.ToString());
            }
            else
            {
                throw;
            }
        }
        catch
        {
            throw;
        }
    }

    public async Task EnsureSearchIndexAsync(string searchIndexName, CancellationToken ct = default)
    {
        var indexNames = _searchIndexClient.GetIndexNamesAsync();
        await foreach (var page in indexNames.AsPages())
        {
            if (page.Values.Any(indexName => indexName == searchIndexName))
            {
                _logger?.LogInformation("""Search index '{SearchIndexName}' already exists""", searchIndexName);

                return;
            }
        }

        await CreateSearchIndexAsync(searchIndexName, ct);
    }

    public Task SetBlobMetadataAsync(BlobClient blobClient, DocumentProcessingStatus documentProcessingStatus)
    {
        return blobClient.SetMetadataAsync(new Dictionary<string, string>
        {
            [nameof(DocumentProcessingStatus)] = documentProcessingStatus.ToString(),
            [nameof(EmbeddingType)] = EmbeddingType.AzureSearch.ToString()
        });
    }

    #endregion Public Methods

    #region Private Methods

    [GeneratedRegex("""[^0-9a-zA-Z_-]""")]
    private static partial Regex MatchInSetRegex();

    #endregion Private Methods

    private async Task UploadCorpusAsync(string corpusBlobName, string text)
    {
        var blobClient = _corpusContainerClient.GetBlobClient(corpusBlobName);
        if (await blobClient.ExistsAsync())
        {
            return;
        }

        _logger?.LogInformation("""Uploading corpus '{CorpusBlobName}'""", corpusBlobName);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await blobClient.UploadAsync(stream, new BlobHttpHeaders
        {
            ContentType = """text/plain""",
        });

        await SetBlobMetadataAsync(blobClient, DocumentProcessingStatus.NotProcessed);
    }

    private IEnumerable<Section> CreateSections(IReadOnlyList<PageDetail> pageMap, string blobName)
    {
        const int MaxSectionLength = 1_000;
        const int SentenceSearchLimit = 100;
        const int SectionOverlap = 100;

        var sentenceEndings = new[] { '.', '!', '?' };
        var wordBreaks = new[] { ',', ';', ':', ' ', '(', ')', '[', ']', '{', '}', '\t', '\n' };
        var allText = string.Concat(pageMap.Select(p => p.Text));
        var length = allText.Length;
        var start = 0;
        var end = length;

        _logger?.LogInformation("""Splitting '{BlobName}' into sections""", blobName);

        while (start + SectionOverlap < length)
        {
            var lastWord = -1;
            end = start + MaxSectionLength;

            if (end > length)
            {
                end = length;
            }
            else
            {
                // Try to find the end of the sentence
                while (end < length && (end - start - MaxSectionLength) < SentenceSearchLimit && !sentenceEndings.Contains(allText[end]))
                {
                    if (wordBreaks.Contains(allText[end]))
                    {
                        lastWord = end;
                    }
                    end++;
                }

                if (end < length && !sentenceEndings.Contains(allText[end]) && lastWord > 0)
                {
                    end = lastWord; // Fall back to at least keeping a whole word
                }
            }

            if (end < length)
            {
                end++;
            }

            // Try to find the start of the sentence or at least a whole word boundary
            lastWord = -1;
            while (start > 0 && start > end - MaxSectionLength -
                (2 * SentenceSearchLimit) && !sentenceEndings.Contains(allText[start]))
            {
                if (wordBreaks.Contains(allText[start]))
                {
                    lastWord = start;
                }
                start--;
            }

            if (!sentenceEndings.Contains(allText[start]) && lastWord > 0)
            {
                start = lastWord;
            }
            if (start > 0)
            {
                start++;
            }

            var sectionText = allText[start..end];

            yield return new Section(
                Id: MatchInSetRegex().Replace($"""{blobName}-{start}""", """_""").TrimStart('_'),
                Content: sectionText,
                SourcePage: BlobNameFromFilePage(blobName, FindPage(pageMap, start)),
                SourceFile: blobName);

            var lastTableStart = sectionText.LastIndexOf("""<table""", StringComparison.Ordinal);
            if (lastTableStart > 2 * SentenceSearchLimit && lastTableStart > sectionText.LastIndexOf("""</table""", StringComparison.Ordinal))
            {
                // If the section ends with an unclosed table, we need to start the next section with the table.
                // If table starts inside SentenceSearchLimit, we ignore it, as that will cause an infinite loop for tables longer than MaxSectionLength
                // If last table starts inside SectionOverlap, keep overlapping
                _logger?.LogWarning(
                    """Section ends with unclosed table, starting next section with the table at page {Offset} offset {Start} table start {LastTableStart}""",
                    FindPage(pageMap, start), start, lastTableStart);

                start = Math.Min(end - SectionOverlap, start + lastTableStart);
            }
            else
            {
                start = end - SectionOverlap;
            }
        }

        if (start + SectionOverlap < end)
        {
            yield return new Section(
                Id: MatchInSetRegex().Replace($"""{blobName}-{start}""", """_""").TrimStart('_'),
                Content: allText[start..end],
                SourcePage: BlobNameFromFilePage(blobName, FindPage(pageMap, start)),
                SourceFile: blobName);
        }
    }

    private async Task IndexSectionsAsync(IEnumerable<Section> sections)
    {
        var iteration = 0;
        var batch = new IndexDocumentsBatch<VectorizeSearchEntity>();

        _logger?.LogInformation("""Starting {MethodName} with {SectionsCount} total sections.""", nameof(IndexSectionsAsync), sections.Count());

        foreach (var section in sections)
        {
            ClientResult<OpenAIEmbedding> embeddingsResult = await _clientService
                .GetEmbeddingClient(_embeddingModelName)
                .GenerateEmbeddingAsync(section.Content.Replace('\r', ' '));

            ReadOnlyMemory<float> embedding = embeddingsResult?.Value?.ToFloats() ?? null;

            batch.Actions.Add(new IndexDocumentsAction<VectorizeSearchEntity>(
                IndexActionType.MergeOrUpload,
                VectorizeSearchEntity.CreateDocument(section.Id, section.Content, section.SourceFile, section.SourcePage, embedding.ToArray())));

            iteration++;
            // Every one thousand documents, batch create.
            if (iteration % 1_000 is 0)
            {
                await IndexDocumentsToAzureSearchAsync(batch);

                batch = new();
            }
        }

        // Any remaining documents, batch create.
        if (batch is { Actions.Count: > 0 })
        {
            await IndexDocumentsToAzureSearchAsync(batch);
        }
    }

    private async Task SetCorpusBlobMetadataAsSucceededStatusAsync(IEnumerable<string> corpusBlobNames)
    {
        _logger?.LogInformation("""Starting {MethodName} for {BlobsCount} blobs""", nameof(SetCorpusBlobMetadataAsSucceededStatusAsync), corpusBlobNames.Count());

        foreach (var corpusBlobName in corpusBlobNames)
        {
            var blobClient = _corpusContainerClient.GetBlobClient(corpusBlobName);

            var blobExists = await blobClient.ExistsAsync();
            if (!blobExists)
            {
                _logger?.LogWarning("""Blob '{BlobName}' does not exists""", corpusBlobName);
                continue;
            }

            await SetBlobMetadataAsync(blobClient, DocumentProcessingStatus.Succeeded);
        }
    }

    private string BlobNameFromFilePage(string blobName, int page = 0) => blobName;

    private int FindPage(IReadOnlyList<PageDetail> pageMap, int offset)
    {
        var length = pageMap.Count;
        for (var i = 0; i < length - 1; i++)
        {
            if (offset >= pageMap[i].Offset && offset < pageMap[i + 1].Offset)
            {
                return i;
            }
        }

        return length - 1;
    }

    private async Task IndexDocumentsToAzureSearchAsync(IndexDocumentsBatch<VectorizeSearchEntity> batch)
    {
        var parsedBatch = ParseVectorizeSearchEntityBatchToSearchDocumentBatch(batch);
        IndexDocumentsResult result = await _searchClient.IndexDocumentsAsync(parsedBatch, new IndexDocumentsOptions { ThrowOnAnyError = true });

        int succeeded = result.Results.Count(r => r.Succeeded);
        _logger?.LogInformation("""Indexed {BatchCount} sections, {SucceededCount} succeeded""", batch.Actions.Count, succeeded);
    }

    private IndexDocumentsBatch<SearchDocument> ParseVectorizeSearchEntityBatchToSearchDocumentBatch(IndexDocumentsBatch<VectorizeSearchEntity> batch)
    {
        var parsedBatch = new IndexDocumentsBatch<SearchDocument>();

        for (var x = 0; x < batch.Actions.Count; x++)
        {
            var document = batch.Actions[x];

            var newAction = new IndexDocumentsAction<SearchDocument>(document.ActionType, document.Document.AsSearchDocument());
            parsedBatch.Actions.Add(newAction);
        }

        return parsedBatch;
    }
}
