using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Text;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Shared.Models;
using Shared.Services.Interfaces;

namespace Shared.Services;

public abstract class AzureFormRecognizerDocumentParserService : IDocumentParserService
{
    #region Private Fields

    private readonly ILogger? _logger;
    private readonly DocumentAnalysisClient _documentAnalysisClient;

    private const int Maximum_Distance_Tolerance_In_Percentage_Untill_25_Characters = 20;
    private const int Maximum_Distance_Tolerance_In_Percentage_Untill_75_Characters = 15;
    private const int Maximum_Distance_Tolerance_In_Percentage_More_Than_75_Characters = 10;

    #endregion Private Fields

    #region Contructor/s

    public AzureFormRecognizerDocumentParserService(ILogger? logger, DocumentAnalysisClient documentAnalysisClient)
    {
        _logger = logger;
        _documentAnalysisClient = documentAnalysisClient;
    }

    #endregion Contructor/s

    #region Public Methods

    public async Task<IReadOnlyList<PageDetail>> GetDocumentTextAsync(Stream blobStream, string blobName)
    {
        _logger?.LogInformation("Extracting text from '{Blob}' using Azure Form Recognizer", blobName);

        using var ms = new MemoryStream();
        blobStream.CopyTo(ms);
        ms.Position = 0;
        AnalyzeDocumentOperation operation = await _documentAnalysisClient.AnalyzeDocumentAsync(
            WaitUntil.Started, "prebuilt-layout", ms);

        var offset = 0;
        List<PageDetail> pageMap = [];

        var results = await operation.WaitForCompletionAsync();
        var pages = results.Value.Pages;

        for (var i = 0; i < pages.Count; i++)
        {
            int pageNumber = i + 1;
            IReadOnlyList<DocumentTable> tablesOnPage =
                results.Value.Tables.Where(t => t.BoundingRegions[0].PageNumber == pageNumber).ToList();

            // Mark all positions of the table spans in the page
            int pageIndex = pages[i].Spans[0].Index;
            int pageLength = pages[i].Spans[0].Length;
            int[] tableChars = Enumerable.Repeat(-1, pageLength).ToArray();
            for (var tableId = 0; tableId < tablesOnPage.Count; tableId++)
            {
                foreach (DocumentSpan span in tablesOnPage[tableId].Spans)
                {
                    // Replace all table spans with "tableId" in tableChars array
                    for (var j = 0; j < span.Length; j++)
                    {
                        int index = span.Index - pageIndex + j;
                        if (index >= 0 && index < pageLength)
                        {
                            tableChars[index] = tableId;
                        }
                    }
                }
            }

            // Build page text by replacing characters in table spans with table HTML
            StringBuilder pageText = new();
            HashSet<int> addedTables = [];
            for (int j = 0; j < tableChars.Length; j++)
            {
                if (tableChars[j] == -1)
                {
                    pageText.Append(results.Value.Content[pageIndex + j]);
                }
                else if (!addedTables.Contains(tableChars[j]))
                {
                    pageText.Append(TableToHtml(tablesOnPage[tableChars[j]]));
                    addedTables.Add(tableChars[j]);
                }
            }

            pageText.Append(' ');
            pageMap.Add(new PageDetail(pageNumber, null, offset, pageText.ToString()));
            offset += pageText.Length;
        }

        return pageMap.AsReadOnly();
    }

    public async Task<IReadOnlyList<PageDetail>> GetDocumentTextForPagesWithSplitTablesAsync(Stream blobStream, string blobName)
    {
        _logger?.LogInformation("Extracting text from '{Blob}' using Azure Form Recognizer", blobName);

        using var ms = new MemoryStream();
        blobStream.CopyTo(ms);
        ms.Position = 0;

        AnalyzeDocumentOperation operation = await _documentAnalysisClient.AnalyzeDocumentAsync(
            WaitUntil.Started, "prebuilt-layout", ms);

        var results = await operation.WaitForCompletionAsync();
        var documentAnalyzeResult = results.Value;

        return IdentifyAndMergeCrossPageTables(documentAnalyzeResult);
    }

    //public void TryIdentifyHeadersAndFooters()
    //{
    //    MyAnalyzeResult? documentIntelligenceResult;

    //    //using (StreamReader r = new StreamReader(@"C:\Workspace\git\AzureSearchOpenai\azure-search-openai-demo-csharp\app\shared\Shared\Services\DocumentParser\DemoDocs\Demo1.json"))
    //    using (StreamReader r = new StreamReader(@"C:\Workspace\git\AzureSearchOpenai\azure-search-openai-demo-csharp\app\shared\Shared\Services\DocumentParser\DemoDocs\JCCgatewaySubscriptionPayments Fully Analyzed.json"))
    //    {
    //        string json = r.ReadToEnd();
    //        documentIntelligenceResult = JsonConvert.DeserializeObject<MyAnalyzeResult>(json);
    //    }

    //    if (documentIntelligenceResult == null)
    //    {
    //        throw new ArgumentException("Document is not found");
    //    }

    //    //var response = TryIdentifyHeadersAndFooters(documentIntelligenceResult.AnalyzeResult.Paragraphs, documentIntelligenceResult.AnalyzeResult.Pages.Count);
    //    IdentifyAndMergeCrossPageTables(documentIntelligenceResult.AnalyzeResult);
    //}

    #endregion Public Methods

    #region Private Methods

    private ReadOnlyCollection<PageDetail> IdentifyAndMergeCrossPageTables(AnalyzeResult documentAnalyzeResult)
    {
        IDictionary<int, (int HeadersParagraphs, int FooterParagraphs)> pageHeadersAndFootersIdentified
            = TryIdentifyHeadersAndFooters(documentAnalyzeResult.Paragraphs, documentAnalyzeResult.Pages.Count);
        IDictionary<int, int> crossPageTablesIdentified
            = TryIdentifyCrossPageTables(documentAnalyzeResult.Tables, documentAnalyzeResult.Paragraphs, pageHeadersAndFootersIdentified);

        var offset = 0;
        List<PageDetail> pageMap = [];

        for (var x = 0; x < crossPageTablesIdentified.Count; x++)
        {
            var crossPageTableInfo = crossPageTablesIdentified.ElementAt(x);
            // Get the page number
            var pageNumber = documentAnalyzeResult.Tables[crossPageTableInfo.Key].BoundingRegions[0].PageNumber;

            // Get all the tables in the current page
            IReadOnlyList<DocumentTable> tablesOnPage =
                documentAnalyzeResult.Tables.Where(t => t.BoundingRegions[0].PageNumber == pageNumber).ToList();
            // Get all the cross page tables of the current page
            IList<DocumentTable> crossPageTables = new List<DocumentTable>();
            for (var y = 1; y <= crossPageTableInfo.Value; y++)
            {
                crossPageTables.Add(documentAnalyzeResult.Tables[crossPageTableInfo.Key + y]);
            }

            // Mark all positions of the table spans in the page
            DocumentPage page = documentAnalyzeResult.Pages.First(c => c.PageNumber == pageNumber);
            int pageIndex = page.Spans[0].Index;
            int pageLength = page.Spans[0].Length;
            int[] tableChars = Enumerable.Repeat(-1, pageLength).ToArray();
            for (var tableId = 0; tableId < tablesOnPage.Count; tableId++)
            {
                foreach (DocumentSpan span in tablesOnPage[tableId].Spans)
                {
                    // Replace all table spans with "tableId" in tableChars array
                    for (var j = 0; j < span.Length; j++)
                    {
                        int index = span.Index - pageIndex + j;
                        if (index >= 0 && index < pageLength)
                        {
                            tableChars[index] = tableId;
                        }
                    }
                }
            }

            // Build page text by replacing characters in table spans with table HTML
            StringBuilder pageText = new();
            HashSet<int> addedTables = [];
            for (var y = 0; y < tableChars.Length; y++)
            {
                if (tableChars[y] == -1)
                {
                    pageText.Append(documentAnalyzeResult.Content[pageIndex + y]);
                }
                else if (!addedTables.Contains(tableChars[y]))
                {
                    pageText.Append(TableToHtml(tablesOnPage[tableChars[y]]));
                    addedTables.Add(tableChars[y]);
                }
            }

            // Here we add the cross page table to the page, if any
            for (var y = 0; y < crossPageTables.Count; y++)
            {
                pageText.Append(TableToHtml(crossPageTables[y]));
            }

            pageText.Append(' ');
            int? documentTotalPages = crossPageTables.Any() ? crossPageTables.Last().BoundingRegions[0].PageNumber : null;

            pageMap.Add(new PageDetail(pageNumber, documentTotalPages, offset, pageText.ToString()));

            offset += pageText.Length;
        }

        return pageMap.AsReadOnly();
    }

    #region TryIdentifyHeadersAndFooters

    private IDictionary<int, (int HeadersParagraphs, int FooterParagraphs)> TryIdentifyHeadersAndFooters(IReadOnlyList<DocumentParagraph> paragraphs, int totalPages)
    {
        int[,] pageHeadersIdentified = new int[totalPages, totalPages];
        int[,] pageFootersIdentified = new int[totalPages, totalPages];

        for (var currentPage = 0; currentPage < totalPages; currentPage++)
        {
            var currentPageHeadersReport = IdentifyCommonPageHeadersAndFooters(paragraphs, totalPages, currentPage);

            for (var x = 0; x < totalPages; x++)
            {
                pageHeadersIdentified[currentPage, x] = currentPageHeadersReport.PageHeadersReport[x];
                pageFootersIdentified[currentPage, x] = currentPageHeadersReport.PageFootersReport[x];
            }
        }

        IDictionary<int, (int HeadersParagraphs, int FooterParagraphs)> response = new Dictionary<int, (int HeadersParagraphs, int FooterParagraphs)>();
        for (int paragraphsFound = 0, currentPage = 1; paragraphsFound < totalPages; paragraphsFound++, currentPage++)
        {
            int[] pageHeadersParagraphsFound = new int[totalPages];
            int[] pageFooterParagraphsFound = new int[totalPages];
            for (var page = 0; page < totalPages; page++)
            {
                pageHeadersParagraphsFound[page] = pageHeadersIdentified[page, paragraphsFound];
                pageFooterParagraphsFound[page] = pageFootersIdentified[page, paragraphsFound];
            }

            var pageHeadersParagraphsCount = pageHeadersParagraphsFound.Where(c => c > -1).GroupBy(c => c).OrderByDescending(c => c.Count()).ElementAt(0).Key;
            var pageFootersParagraphsCount = pageFooterParagraphsFound.Where(c => c > -1).GroupBy(c => c).OrderByDescending(c => c.Count()).ElementAt(0).Key;

            response.Add(currentPage, (pageHeadersParagraphsCount, pageFootersParagraphsCount));
        }

        return response;
    }

    private (int[] PageHeadersReport, int[] PageFootersReport) IdentifyCommonPageHeadersAndFooters(IReadOnlyList<DocumentParagraph> paragraphs, int totalPages, int currentPage)
    {
        int[] headersIdentifiedBasedOnCurrentPage = new int[totalPages];
        int[] footersIdentifiedBasedOnCurrentPage = new int[totalPages];

        int currentPageHeaderParagraphsCount = 0;
        int currentPageFooterParagraphsCount = 0;
        string currentPageHeaders = "";
        string currentPageFooters = "";

        DocumentParagraph[] currentPageParagraphs = SelectPageParagraphsAsArray(paragraphs, currentPage + 1);

        for (var x = 1; x < totalPages; x++)
        {
            var nextPage = currentPage + x;
            if (nextPage >= totalPages)
            {
                nextPage -= totalPages;
            }

            DocumentParagraph[] nextPageParagraphs = SelectPageParagraphsAsArray(paragraphs, nextPage + 1);

            var headersReport = TryIdentifyCommonPageHeadersOrFooters(currentPageParagraphs, nextPageParagraphs, true, ref currentPageHeaderParagraphsCount, ref currentPageHeaders);
            if (headersReport.CurrentPageParagraphCount > -1)
            {
                headersIdentifiedBasedOnCurrentPage[currentPage] = headersReport.CurrentPageParagraphCount;
            }
            headersIdentifiedBasedOnCurrentPage[nextPage] = headersReport.NextPageParagraphCount;

            var footersReport = TryIdentifyCommonPageHeadersOrFooters(currentPageParagraphs, nextPageParagraphs, false, ref currentPageFooterParagraphsCount, ref currentPageFooters);
            if (footersReport.CurrentPageParagraphCount > -1)
            {
                footersIdentifiedBasedOnCurrentPage[currentPage] = footersReport.CurrentPageParagraphCount;
            }
            footersIdentifiedBasedOnCurrentPage[nextPage] = footersReport.NextPageParagraphCount;
        }

        return (headersIdentifiedBasedOnCurrentPage, footersIdentifiedBasedOnCurrentPage);
    }

    private (int CurrentPageParagraphCount, int NextPageParagraphCount) TryIdentifyCommonPageHeadersOrFooters(DocumentParagraph[] currentPageParagraphs, DocumentParagraph[] nextPageParagraphs,
        bool checkForHeaders, ref int currentPageHeaderOrFooterParagraphsCount, ref string currentPageHeadersOrFooters)
    {
        // Means we already have identified headers in the currentPage
        if (currentPageHeaderOrFooterParagraphsCount > 0)
        {
            int nextPageParagraphCount = currentPageHeaderOrFooterParagraphsCount;
            DocumentParagraph[] paragraphsToCompare;
            if (checkForHeaders)
            {
                paragraphsToCompare = nextPageParagraphs[0..nextPageParagraphCount];
            }
            else
            {
                paragraphsToCompare = nextPageParagraphs[(nextPageParagraphs.Length - nextPageParagraphCount)..nextPageParagraphs.Length];
            }

            var nextPageParagraphsToCompare = string.Join("", paragraphsToCompare.Select(c => c.Content)).Replace(" ", "").ToLowerInvariant();

            var areParagraphsSame = AreParagraphsSameOrWithinTolerance(currentPageHeadersOrFooters, nextPageParagraphsToCompare);
            if (areParagraphsSame)
            {
                return (-1, nextPageParagraphCount);
            }

            // If the distance percentage is more than the accepted tolerance, then we attempt to check again, by adding the next paragraph also
            bool increaseNextPageParagraph;
            if (currentPageHeadersOrFooters.Length > nextPageParagraphsToCompare.Length)
            {
                increaseNextPageParagraph = true;

                int nextPageParagraphToAddIndex;
                if (checkForHeaders)
                {
                    nextPageParagraphToAddIndex = nextPageParagraphCount;
                }
                else
                {
                    nextPageParagraphToAddIndex = nextPageParagraphs.Length - nextPageParagraphCount - 1;
                }

                var nextPageParagraphToAdd = nextPageParagraphs[nextPageParagraphToAddIndex].Content.Replace(" ", "").ToLowerInvariant();

                if (checkForHeaders)
                {
                    nextPageParagraphsToCompare += nextPageParagraphToAdd;
                }
                else
                {
                    // We add the new paragraph to the beginning of the string
                    nextPageParagraphsToCompare = string.Join("", nextPageParagraphToAdd, nextPageParagraphsToCompare);
                }
            }
            else
            {
                increaseNextPageParagraph = false;

                int nextPageParagraphToRemovePosition;
                if (checkForHeaders)
                {
                    nextPageParagraphToRemovePosition = nextPageParagraphCount - 1;
                }
                else
                {
                    nextPageParagraphToRemovePosition = nextPageParagraphs.Length - nextPageParagraphCount;
                }

                var strToRemove = nextPageParagraphs[nextPageParagraphToRemovePosition].Content.Replace(" ", "").ToLowerInvariant();

                if (checkForHeaders)
                {
                    nextPageParagraphsToCompare = nextPageParagraphsToCompare.Substring(0, nextPageParagraphsToCompare.Length - strToRemove.Length);
                }
                else
                {
                    nextPageParagraphsToCompare = nextPageParagraphsToCompare.Substring(strToRemove.Length);
                }
            }

            areParagraphsSame = AreParagraphsSameOrWithinTolerance(currentPageHeadersOrFooters, nextPageParagraphsToCompare);
            if (areParagraphsSame)
            {
                // We also making sure that we add the extra count to the shortest paragraph
                if (increaseNextPageParagraph)
                {
                    nextPageParagraphCount++;
                }
                else
                {
                    nextPageParagraphCount--;
                }

                return (-1, nextPageParagraphCount);
            }
        }
        else
        {
            bool haveMatchingParagraphs = false;
            int nextPageParagraphCount = 0;
            do
            {
                int currentPageParagraphPosition;
                int nextPageParagraphPosition;
                if (checkForHeaders)
                {
                    currentPageParagraphPosition = currentPageHeaderOrFooterParagraphsCount;
                    nextPageParagraphPosition = nextPageParagraphCount;
                }
                else
                {
                    currentPageParagraphPosition = currentPageParagraphs.Length - currentPageHeaderOrFooterParagraphsCount - 1;
                    nextPageParagraphPosition = nextPageParagraphs.Length - nextPageParagraphCount - 1;
                }

                var currentPageParagraph = currentPageParagraphs[currentPageParagraphPosition].Content.Replace(" ", "").ToLowerInvariant();
                var nextPageParagraph = nextPageParagraphs[nextPageParagraphPosition].Content.Replace(" ", "").ToLowerInvariant();

                var areParagraphsSame = AreParagraphsSameOrWithinTolerance(currentPageParagraph, nextPageParagraph);
                if (areParagraphsSame)
                {
                    haveMatchingParagraphs = true;
                    currentPageHeaderOrFooterParagraphsCount++;
                    nextPageParagraphCount++;

                    if (checkForHeaders)
                    {
                        currentPageHeadersOrFooters += currentPageParagraph;
                    }
                    else
                    {
                        // We add the new paragraph to the beginning of the string
                        currentPageHeadersOrFooters = string.Join("", currentPageParagraph, currentPageHeadersOrFooters);
                    }

                    continue;
                }

                // If the distance percentage is more than the accepted tolerance, then we attempt to check again, by adding the next paragraph also
                bool increaseCurrentPageParagraph;
                string currentPageParagraphWithAdditional = currentPageParagraph;
                string nextPageParagraphWithAdditional = nextPageParagraph;
                if (currentPageParagraph.Length > nextPageParagraph.Length)
                {
                    increaseCurrentPageParagraph = false;

                    if (checkForHeaders)
                    {
                        nextPageParagraphPosition++;
                    }
                    else
                    {
                        nextPageParagraphPosition--;
                    }
                    var additionalParagraph = nextPageParagraphs[nextPageParagraphPosition].Content.Replace(" ", "").ToLowerInvariant();

                    if (checkForHeaders)
                    {
                        nextPageParagraphWithAdditional += additionalParagraph;
                    }
                    else
                    {
                        // We add the new paragraph to the beginning of the string
                        nextPageParagraphWithAdditional = string.Join("", additionalParagraph, nextPageParagraphWithAdditional);
                    }
                }
                else
                {
                    increaseCurrentPageParagraph = true;

                    if (checkForHeaders)
                    {
                        currentPageParagraphPosition++;
                    }
                    else
                    {
                        currentPageParagraphPosition--;
                    }
                    var additionalParagraph = currentPageParagraphs[currentPageParagraphPosition].Content.Replace(" ", "").ToLowerInvariant();

                    if (checkForHeaders)
                    {
                        currentPageParagraphWithAdditional += additionalParagraph;
                    }
                    else
                    {
                        // We add the new paragraph to the beginning of the string
                        currentPageParagraphWithAdditional = string.Join("", additionalParagraph, currentPageParagraphWithAdditional);
                    }
                }

                areParagraphsSame = AreParagraphsSameOrWithinTolerance(currentPageParagraphWithAdditional, nextPageParagraphWithAdditional);
                if (areParagraphsSame)
                {
                    haveMatchingParagraphs = true;
                    currentPageHeaderOrFooterParagraphsCount++;
                    nextPageParagraphCount++;

                    // We also making sure that we add the extra count to the shortest paragraph
                    if (increaseCurrentPageParagraph)
                    {
                        currentPageHeaderOrFooterParagraphsCount++;
                    }
                    else
                    {
                        nextPageParagraphCount++;
                    }

                    if (checkForHeaders)
                    {
                        currentPageHeadersOrFooters += currentPageParagraphWithAdditional;
                    }
                    else
                    {
                        // We add the new paragraph to the beginning of the string
                        currentPageHeadersOrFooters = string.Join("", currentPageParagraphWithAdditional, currentPageHeadersOrFooters);
                    }

                    continue;
                }

                haveMatchingParagraphs = false;

            } while (haveMatchingParagraphs);

            if (currentPageHeaderOrFooterParagraphsCount > 0 && nextPageParagraphCount > 0)
            {
                return (currentPageHeaderOrFooterParagraphsCount, nextPageParagraphCount);
            }
        }

        return (-1, -1);
    }

    private bool AreParagraphsSameOrWithinTolerance(string text1, string text2)
    {
        var distance = GetDamerauLevenshteinDistance(text1, text2);
        if (distance == 0)
        {
            return true;
        }

        int longestParagraphLength = text1.Length > text2.Length ? text1.Length : text2.Length;
        double distancePercentageFromLongestParagraph = CalculatePercentageDifference(distance, longestParagraphLength);

        int maximumDistanceToleranceInPercentage;
        if (longestParagraphLength <= 25)
        {
            maximumDistanceToleranceInPercentage = Maximum_Distance_Tolerance_In_Percentage_Untill_25_Characters;
        }
        else if (longestParagraphLength <= 75)
        {
            maximumDistanceToleranceInPercentage = Maximum_Distance_Tolerance_In_Percentage_Untill_75_Characters;
        }
        else
        {
            maximumDistanceToleranceInPercentage = Maximum_Distance_Tolerance_In_Percentage_More_Than_75_Characters;
        }

        return distancePercentageFromLongestParagraph <= maximumDistanceToleranceInPercentage;
    }

    private int GetDamerauLevenshteinDistance(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1))
        {
            if (string.IsNullOrEmpty(str2))
            {
                return 0;
            }
            return str2.Length;
        }

        if (string.IsNullOrEmpty(str2))
        {
            return str1.Length;
        }

        // Length of str1
        int str1Len = str1.Length;
        // Length of str2
        int str2Len = str2.Length;

        // 'previous' cost array, horizontally
        int[] p = new int[str1Len + 1];
        // cost array, horizontally
        int[] d = new int[str1Len + 1];

        // Iterates through str1
        int str1Position;
        // Iterates through str2
        int str2Position;

        for (str1Position = 0; str1Position <= str1Len; str1Position++)
        {
            p[str1Position] = str1Position;
        }

        for (str2Position = 1; str2Position <= str2Len; str2Position++)
        {
            char chartToCompare = str2[str2Position - 1];
            d[0] = str2Position;

            for (str1Position = 1; str1Position <= str1Len; str1Position++)
            {
                int cost = str1[str1Position - 1] == chartToCompare ? 0 : 1; // cost
                                                                             // minimum of cell to the left+1, to the top+1, diagonally left and up +cost                
                d[str1Position] = Math.Min(Math.Min(d[str1Position - 1] + 1, p[str1Position] + 1), p[str1Position - 1] + cost);
            }

            // Copy current distance counts to 'previous row' distance counts
            int[] dPlaceholder = p; //placeholder to assist in swapping p and d
            p = d;
            d = dPlaceholder;
        }

        // Our last action in the above loop was to switch d and p, so p now 
        // Actually has the most recent cost counts
        return p[str1Len];
    }

    private double CalculatePercentageDifference(double damerauLevenshteinDistance, double stringLength)
        => (damerauLevenshteinDistance / stringLength) * 100;

    #endregion TryIdentifyHeadersAndFooters

    #region TryIdentifyCrossPageTables

    private IDictionary<int, int> TryIdentifyCrossPageTables(IReadOnlyList<DocumentTable> tables, IReadOnlyList<DocumentParagraph> paragraphs,
        IDictionary<int, (int HeadersParagraphs, int FooterParagraphs)> pageHeadersAndFooters)
    {
        // The table index that has been identified as cross table and how many table they should be merged with it
        IDictionary<int, int> crossPageTables = new Dictionary<int, int>();

        int candidateCrossTableIndex = -1;
        int tablesToBeMergedWithCandidateTable = 0;
        for (var x = 0; x < tables.Count; x++)
        {
            DocumentTable table = tables[x];

            // Means that we are trying to identify if the current table is a candidate as a cross table
            if (candidateCrossTableIndex == -1)
            {
                if (IsTableACrossPageTableCandidate(table, paragraphs, pageHeadersAndFooters))
                {
                    // Current table now is a candidate as as cross page table
                    candidateCrossTableIndex = x;

                    continue;
                }
            }
            // Means we already have identified a table as candidate as a cross page table
            else
            {
                // First we check if the 2 tables have the same number of columns
                var crossPageCandidateTable = tables[candidateCrossTableIndex];
                if (crossPageCandidateTable.ColumnCount == table.ColumnCount)
                {
                    if (IsTableAPartOfCrossPageTable(table, paragraphs, pageHeadersAndFooters))
                    {
                        // We are increasing the counter, marking the current table a continuation of the cross page candidate table
                        tablesToBeMergedWithCandidateTable++;

                        continue;
                    }
                }

                // By reaching to this point, means that the 2 table are not related, therefore we have exclude the current candidate table
                // and check if the new one must be set as the new candidate

                // First, we need to check if the current candidate table is really a cross page table and how many tables/page is long
                if (tablesToBeMergedWithCandidateTable > 0)
                {
                    // We register the cross page table, along with the tables that needs to be merged
                    crossPageTables.Add(candidateCrossTableIndex, tablesToBeMergedWithCandidateTable);
                    // We reset the property
                    tablesToBeMergedWithCandidateTable = 0;
                }

                // We exclude the current candidate table
                candidateCrossTableIndex = -1;
                // We determine if the new table must be set as the new candidate
                if (IsTableACrossPageTableCandidate(table, paragraphs, pageHeadersAndFooters))
                {
                    // Current table now is a candidate as as cross page table
                    candidateCrossTableIndex = x;

                    continue;
                }
            }
        }

        // We make sure that we add the last cross page candidate table to the response. if any
        if (candidateCrossTableIndex != -1 && tablesToBeMergedWithCandidateTable > 0)
        {
            // We register the cross page table, along with the tables that needs to be merged
            crossPageTables.Add(candidateCrossTableIndex, tablesToBeMergedWithCandidateTable);
        }

        return crossPageTables;
    }

    private bool IsTableACrossPageTableCandidate(DocumentTable table, IReadOnlyList<DocumentParagraph> paragraphs,
        IDictionary<int, (int HeadersParagraphs, int FooterParagraphs)> pageHeadersAndFooters)
    {
        int tablePage = table.BoundingRegions[0].PageNumber;
        DocumentParagraph[] currentPageParagraphs = SelectPageParagraphsAsArray(paragraphs, tablePage);

        DocumentTableCell lastTableCell;
        int numberOfEmptyCells = 0;
        do
        {
            lastTableCell = table.Cells.ElementAt(table.Cells.Count - 1 - numberOfEmptyCells);
            numberOfEmptyCells++;
        }
        // Cells can be empty. They will have the property "content": "" and the property "spans": []
        while (!lastTableCell.Spans.Any());

        var lastPageParagraphWithoutFootersIndex = currentPageParagraphs.Length - 1 - pageHeadersAndFooters[tablePage].FooterParagraphs;
        var lastPageParagraphWithoutFooters = currentPageParagraphs[lastPageParagraphWithoutFootersIndex];

        if (lastPageParagraphWithoutFooters.Spans.Any()
            && lastTableCell.Spans[0].Equals(lastPageParagraphWithoutFooters.Spans[0]))
        {
            // Current table now is a candidate as as cross page table
            return true;
        }

        return false;
    }

    private bool IsTableAPartOfCrossPageTable(DocumentTable table, IReadOnlyList<DocumentParagraph> paragraphs,
        IDictionary<int, (int HeadersParagraphs, int FooterParagraphs)> pageHeadersAndFooters)
    {
        int tablePage = table.BoundingRegions[0].PageNumber;
        DocumentParagraph[] currentPageParagraphs = SelectPageParagraphsAsArray(paragraphs, tablePage);

        DocumentTableCell firstTableCell;
        int numberOfEmptyCells = 0;
        do
        {
            firstTableCell = table.Cells.ElementAt(numberOfEmptyCells);
            numberOfEmptyCells++;
        }
        // Cells can be empty. They will have the property "content": "" and the property "spans": []
        while (!firstTableCell.Spans.Any());

        var firstPageParagraphWithoutFootersIndex = pageHeadersAndFooters[tablePage].HeadersParagraphs;
        var firstPageParagraphWithoutFooters = currentPageParagraphs[firstPageParagraphWithoutFootersIndex];

        if (firstPageParagraphWithoutFooters.Spans.Any()
            && firstTableCell.Spans[0].Equals(firstPageParagraphWithoutFooters.Spans[0]))
        {
            // Current table is at the top of the page, making it a possible candidate of being part of a cross page table
            return true;
        }

        return false;
    }

    #endregion TryIdentifyCrossPageTables

    private DocumentParagraph[] SelectPageParagraphsAsArray(IEnumerable<DocumentParagraph> paragraphs, int pageNumber)
        => paragraphs.Where(c => c.BoundingRegions.Any(y => y.PageNumber == pageNumber)).ToArray();

    private string TableToHtml(DocumentTable table)
    {
        var tableHtml = new StringBuilder("<table>");
        var rows = new List<DocumentTableCell>[table.RowCount];
        for (int i = 0; i < table.RowCount; i++)
        {
            rows[i] =
            [
                .. table.Cells.Where(c => c.RowIndex == i)
                                .OrderBy(c => c.ColumnIndex)
,
            ];
        }

        foreach (var rowCells in rows)
        {
            tableHtml.Append("<tr>");
            foreach (DocumentTableCell cell in rowCells)
            {
                var tag = (cell.Kind == "columnHeader" || cell.Kind == "rowHeader") ? "th" : "td";
                var cellSpans = string.Empty;
                if (cell.ColumnSpan > 1)
                {
                    cellSpans += $" colSpan='{cell.ColumnSpan}'";
                }

                if (cell.RowSpan > 1)
                {
                    cellSpans += $" rowSpan='{cell.RowSpan}'";
                }

                tableHtml.AppendFormat(
                    "<{0}{1}>{2}</{0}>", tag, cellSpans, WebUtility.HtmlEncode(cell.Content));
            }

            tableHtml.Append("</tr>");
        }

        tableHtml.Append("</table>");

        return tableHtml.ToString();
    }

    #endregion Private Methods

    //private class MyAnalyzeResult
    //{
    //    public string Status { get; set; }
    //    public DateTime CreatedDateTime { get; set; }
    //    public DateTime LastUpdatedDateTime { get; set; }
    //    public MyMyAnalyzeResult AnalyzeResult { get; set; }
    //}

    //private class MyMyAnalyzeResult
    //{
    //    /// <summary> Document model ID used to produce this result. </summary>
    //    public string ModelId { get; set; }
    //    /// <summary> Concatenate string representation of all textual and visual elements in reading order. </summary>
    //    public string Content { get; set; }
    //    /// <summary> Analyzed pages. </summary>
    //    public IReadOnlyList<MyDocumentPage> Pages { get; set; }
    //    /// <summary> Extracted paragraphs. </summary>
    //    public IReadOnlyList<MyDocumentParagraph> Paragraphs { get; set; }
    //    /// <summary> Extracted tables. </summary>
    //    public IReadOnlyList<MyDocumentTable> Tables { get; set; }
    //    /// <summary> Extracted key-value pairs. </summary>
    //    //public IReadOnlyList<DocumentKeyValuePair> KeyValuePairs { get; set; }
    //    ///// <summary> Extracted font styles. </summary>
    //    //public IReadOnlyList<DocumentStyle> Styles { get; set; }
    //    ///// <summary> Detected languages. </summary>
    //    //public IReadOnlyList<DocumentLanguage> Languages { get; set; }
    //    ///// <summary> Extracted documents. </summary>
    //    //public IReadOnlyList<AnalyzedDocument> Documents { get; set; }
    //}

    //private class MyDocumentPage
    //{
    //    /// <summary> 1-based page number in the input document. </summary>
    //    public int PageNumber { get; set; }
    //    /// <summary> The general orientation of the content in clockwise direction, measured in degrees between (-180, 180]. </summary>
    //    public float? Angle { get; set; }
    //    /// <summary> The width of the image/PDF in pixels/inches, respectively. </summary>
    //    public float? Width { get; set; }
    //    /// <summary> The height of the image/PDF in pixels/inches, respectively. </summary>
    //    public float? Height { get; set; }
    //    /// <summary> Location of the page in the reading order concatenated content. </summary>
    //    public IReadOnlyList<MyDocumentTableSpan> Spans { get; set; }
    //}

    //private class MyDocumentParagraph
    //{
    //    /// <summary> Concatenated content of the paragraph in reading order. </summary>
    //    public string Content { get; set; }
    //    /// <summary> Semantic role of the paragraph. </summary>
    //    //[JsonPropertyName("role")]
    //    [JsonProperty("role")]
    //    public string? RoleStr { get; set; }
    //    /// <summary> Semantic role of the paragraph. </summary>
    //    public MyParagraphRole? Role
    //    {
    //        get
    //        {
    //            if (string.IsNullOrWhiteSpace(RoleStr))
    //            {
    //                return null;
    //            }

    //            return new MyParagraphRole(RoleStr);
    //        }
    //    }
    //    /// <summary> Bounding regions covering the paragraph. </summary>
    //    public IReadOnlyList<MyBoundingRegion> BoundingRegions { get; set; }
    //    /// <summary> Location of the paragraph in the reading order concatenated content. </summary>
    //    public IReadOnlyList<MyDocumentSpan> Spans { get; set; }
    //}

    //public struct MyParagraphRole : IEquatable<MyParagraphRole>
    //{
    //    private readonly string _value;

    //    public MyParagraphRole(string value)
    //    {
    //        _value = value ?? throw new ArgumentNullException(nameof(value));
    //    }

    //    private const string PageHeaderValue = "pageHeader";
    //    private const string PageFooterValue = "pageFooter";
    //    private const string PageNumberValue = "pageNumber";
    //    private const string TitleValue = "title";
    //    private const string SectionHeadingValue = "sectionHeading";
    //    private const string FootnoteValue = "footnote";
    //    private const string FormulaBlockValue = "formulaBlock";

    //    /// <summary> Text near the top edge of the page. </summary>
    //    public static MyParagraphRole PageHeader { get; } = new MyParagraphRole(PageHeaderValue);
    //    /// <summary> Text near the bottom edge of the page. </summary>
    //    public static MyParagraphRole PageFooter { get; } = new MyParagraphRole(PageFooterValue);
    //    /// <summary> Page number. </summary>
    //    public static MyParagraphRole PageNumber { get; } = new MyParagraphRole(PageNumberValue);
    //    /// <summary> Top-level title describing the entire document. </summary>
    //    public static MyParagraphRole Title { get; } = new MyParagraphRole(TitleValue);
    //    /// <summary> Sub heading describing a section of the document. </summary>
    //    public static MyParagraphRole SectionHeading { get; } = new MyParagraphRole(SectionHeadingValue);
    //    /// <summary> A note usually placed after the main content on a page. </summary>
    //    public static MyParagraphRole Footnote { get; } = new MyParagraphRole(FootnoteValue);
    //    /// <summary> A block of formulas, often with shared alignment. </summary>
    //    public static MyParagraphRole FormulaBlock { get; } = new MyParagraphRole(FormulaBlockValue);
    //    /// <summary> Determines if two <see cref="MyParagraphRole"/> values are the same. </summary>
    //    public static bool operator ==(MyParagraphRole left, MyParagraphRole right) => left.Equals(right);
    //    /// <summary> Determines if two <see cref="MyParagraphRole"/> values are not the same. </summary>
    //    public static bool operator !=(MyParagraphRole left, MyParagraphRole right) => !left.Equals(right);
    //    /// <summary> Converts a string to a <see cref="MyParagraphRole"/>. </summary>
    //    public static implicit operator MyParagraphRole(string value) => new MyParagraphRole(value);

    //    /// <inheritdoc />
    //    [EditorBrowsable(EditorBrowsableState.Never)]
    //    public override bool Equals(object obj) => obj is MyParagraphRole other && Equals(other);
    //    /// <inheritdoc />
    //    public bool Equals(MyParagraphRole other) => string.Equals(_value, other._value, StringComparison.InvariantCultureIgnoreCase);

    //    /// <inheritdoc />
    //    [EditorBrowsable(EditorBrowsableState.Never)]
    //    public override int GetHashCode() => _value?.GetHashCode() ?? 0;
    //    /// <inheritdoc />
    //    public override string ToString() => _value;
    //}

    //public struct MyBoundingRegion
    //{
    //    /// <summary>
    //    /// 1-based page number of page containing the bounding region.
    //    /// </summary>
    //    public int PageNumber { get; set; }
    //    public double[] Polygon { get; set; }
    //}

    //public struct MyDocumentSpan
    //{
    //    public int Offset { get; set; }
    //    public int Length { get; set; }
    //}

    //public struct MyDocumentTableSpan
    //{
    //    [JsonProperty("offset")]
    //    public int Index { get; set; }
    //    public int Length { get; set; }
    //}

    //public class MyDocumentTable
    //{
    //    /// <summary> Number of rows in the table. </summary>
    //    public int RowCount { get; set; }
    //    /// <summary> Number of columns in the table. </summary>
    //    public int ColumnCount { get; set; }
    //    /// <summary> Cells contained within the table. </summary>
    //    public IReadOnlyList<MyDocumentTableCell> Cells { get; set; }
    //    /// <summary> Bounding regions covering the table. </summary>
    //    public IReadOnlyList<MyBoundingRegion> BoundingRegions { get; set; }
    //    ///// <summary> Location of the table in the reading order concatenated content. </summary>
    //    public IReadOnlyList<MyDocumentTableSpan> Spans { get; set; }
    //}

    //public class MyDocumentTableCell
    //{
    //    private const int DefaultSpanValue = 1;

    //    private static readonly DocumentTableCellKind s_defaultTableCellKind = DocumentTableCellKind.Content;

    //    /// <summary>
    //    /// Table cell kind.
    //    /// </summary>
    //    public DocumentTableCellKind Kind => KindPrivate ?? s_defaultTableCellKind;

    //    /// <summary>
    //    /// Number of rows spanned by this cell.
    //    /// </summary>
    //    public int RowSpan => RowSpanPrivate ?? DefaultSpanValue;

    //    /// <summary>
    //    /// Number of columns spanned by this cell.
    //    /// </summary>
    //    public int ColumnSpan => ColumnSpanPrivate ?? DefaultSpanValue;

    //    private DocumentTableCellKind? KindPrivate { get; set; }

    //    private int? RowSpanPrivate { get; set; }

    //    private int? ColumnSpanPrivate { get; set; }

    //    public int RowIndex { get; set; }
    //    /// <summary> Column index of the cell. </summary>
    //    public int ColumnIndex { get; set; }
    //    /// <summary> Concatenated content of the table cell in reading order. </summary>
    //    public string Content { get; set; }
    //    /// <summary> Bounding regions covering the table cell. </summary>
    //    public IReadOnlyList<MyBoundingRegion> BoundingRegions { get; set; }
    //    /// <summary> Location of the table cell in the reading order concatenated content. </summary>
    //    public IReadOnlyList<MyDocumentSpan> Spans { get; set; }
    //}
}
