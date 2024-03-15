using System.Net;
using System.Text;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Microsoft.Extensions.Logging;
using Shared.Models;
using Shared.Services.Interfaces;

namespace Shared.Services;

public abstract class AzureFormRecognizerDocumentParserService : IDocumentParserService
{
    #region Private Fields

    private readonly ILogger? _logger;
    private readonly DocumentAnalysisClient _documentAnalysisClient;

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
        AnalyzeDocumentOperation operation = _documentAnalysisClient.AnalyzeDocument(
            WaitUntil.Started, "prebuilt-layout", ms);

        var offset = 0;
        List<PageDetail> pageMap = [];

        var results = await operation.WaitForCompletionAsync();
        var pages = results.Value.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            IReadOnlyList<DocumentTable> tablesOnPage =
                results.Value.Tables.Where(t => t.BoundingRegions[0].PageNumber == i + 1).ToList();

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
            pageMap.Add(new PageDetail(i, offset, pageText.ToString()));
            offset += pageText.Length;
        }

        return pageMap.AsReadOnly();
    }

    #endregion Public Methods

    #region Private Methods

    private static string TableToHtml(DocumentTable table)
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
}
