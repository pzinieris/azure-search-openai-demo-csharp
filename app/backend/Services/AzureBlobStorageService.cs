namespace MinimalApi.Services;

internal sealed class AzureBlobStorageService(BlobContainerClient container)
{
    internal async Task<UploadDocumentsResponse> UploadFilesAsync(IEnumerable<IFormFile> files, CancellationToken cancellationToken)
    {
        try
        {
            // Upload all files,
            // By first uploading the whole document, which will be used to identify any tables that are spliced by many pages
            // And by splitting them to each page separately
            List<string> uploadedFiles = [];
            foreach (var file in files)
            {
                var fileName = file.FileName;
                await using var stream = file.OpenReadStream();

                // Upload first the whole document
                var documentName = BlobNameFromFilePage(fileName, -1);
                var blobClient = container.GetBlobClient(documentName);
                if (!await blobClient.ExistsAsync(cancellationToken))
                {
                    await blobClient.UploadAsync(stream, new BlobHttpHeaders
                    {
                        ContentType = "application/pdf"
                    },
                    metadata: new Dictionary<string, string>
                    {
                        [nameof(DocumentProcessingStatus)] = DocumentProcessingStatus.NotProcessed_ToBeDeleted.ToString()
                    },
                    cancellationToken: cancellationToken);

                    uploadedFiles.Add(documentName);
                }

                stream.Position = 0;

                // Split and upload each document to each single page
                using var documents = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
                for (int x = 0; x < documents.PageCount; x++)
                {
                    documentName = BlobNameFromFilePage(fileName, x + 1);
                    blobClient = container.GetBlobClient(documentName);
                    if (await blobClient.ExistsAsync(cancellationToken))
                    {
                        continue;
                    }

                    var tempFileName = Path.GetTempFileName();

                    try
                    {
                        using var document = new PdfDocument();
                        document.AddPage(documents.Pages[x]);
                        document.Save(tempFileName);

                        await using var tempStream = File.OpenRead(tempFileName);
                        await blobClient.UploadAsync(tempStream, new BlobHttpHeaders
                        {
                            ContentType = "application/pdf"
                        }, cancellationToken: cancellationToken);

                        uploadedFiles.Add(documentName);
                    }
                    finally
                    {
                        File.Delete(tempFileName);
                    }
                }
            }

            if (uploadedFiles.Count is 0)
            {
                return UploadDocumentsResponse.FromError("""
                    No files were uploaded. Either the files already exist or the files are not PDFs.
                    """);
            }

            return new UploadDocumentsResponse([.. uploadedFiles]);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
        {
            return UploadDocumentsResponse.FromError(ex.ToString());
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    private static string BlobNameFromFilePage(string filename, int page = 0)
    {
        if (page > 0)
        {
            return $"{Path.GetFileNameWithoutExtension(filename)}-{page}{Path.GetExtension(filename)}";
        }

        return Path.GetFileName(filename);
    }

}
