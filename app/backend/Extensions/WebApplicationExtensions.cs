using System.ClientModel;
using System.Text;
using Azure;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using OpenAI.Images;
using Shared.Enum;
using Shared.Models.Settings;
using Shared.Services.AI.Interface;

namespace MinimalApi.Extensions;

internal static class WebApplicationExtensions
{
    #region Internal Methods

    internal static WebApplication MapApi(this WebApplication app)
    {
        var api = app.MapGroup("api");

        // Blazor 📎 Clippy streaming endpoint
        api.MapPost("openai/chat", OnPostChatPromptAsync);

        // Long-form chat w/ contextual history endpoint
        api.MapPost("chat", OnPostChatAsync);

        // Long-form chat w/ contextual history endpoint
        api.MapGet("citationBaseUrl", OnGetCitationBaseUrl);

        // Upload a document
        api.MapPost("documents", OnPostDocumentAsync);

        // Get all documents
        api.MapGet("documents", OnGetDocumentsAsync);

        // Get all documents
        api.MapGet("document", OnGetDocumentAsync);

        // Get DALL-E image result from prompt
        api.MapPost("images", OnPostImagePromptAsync);

        api.MapGet("enableLogout", OnGetEnableLogout);

        return app;
    }

    #endregion Internal Methods

    #region Private Methods

    #region API methods implementation

    private static IResult OnGetEnableLogout(HttpContext context)
    {
        var header = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"];
        var enableLogout = !string.IsNullOrEmpty(header);

        return TypedResults.Ok(enableLogout);
    }

    private static async IAsyncEnumerable<ChatChunkResponse> OnPostChatPromptAsync(
        PromptRequest prompt,
        IAIClientService clientService,
        IOptions<AppSettings> options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var deploymentId = options.Value.AzureOpenAiChatGptDeployment;
        ArgumentNullException.ThrowIfNullOrEmpty(deploymentId);

        ChatCompletionOptions? chatCompletionOptions = null;
        AsyncCollectionResult<StreamingChatCompletionUpdate> response = clientService
            .GetChatClient(deploymentId)
            .CompleteChatStreamingAsync(
            [
                new SystemChatMessage("""
                    You're an AI assistant for developers, helping them write code more efficiently.
                    You're name is **Blazor 📎 Clippy** and you're an expert Blazor developer.
                    You're also an expert in ASP.NET Core, C#, TypeScript, and even JavaScript.
                    You will always reply with a Markdown formatted response.
                    """),
                //new UserChatMessage("What's your name?"),
                //new AssistantChatMessage("Hi, my name is **Blazor 📎 Clippy**! Nice to meet you."),
                new UserChatMessage(prompt.Prompt)
            ], chatCompletionOptions, cancellationToken);

        await foreach (StreamingChatCompletionUpdate choice in response.WithCancellation(cancellationToken))
        {
            if (choice.ContentUpdate is { Count: > 0 })
            {
                var contentUpdate = new StringBuilder();
                foreach (ChatMessageContentPart part in choice.ContentUpdate)
                {
                    contentUpdate.Append(part.Text);
                }

                yield return new ChatChunkResponse(contentUpdate.Length, contentUpdate.ToString());
                // Always have a delay after each return to simulate the streaming in the frontend
                await Task.Delay(30);
            }
        }
    }

    private static async Task<IResult> OnPostChatOldAsync(
        ChatRequest request,
        ReadRetrieveReadChatService chatService,
        CancellationToken cancellationToken)
    {
        if (request is { History.Length: > 0 })
        {
            var response = await chatService.ReplyOnYourDataAsync(
                request.History, request.Overrides, cancellationToken);

            return TypedResults.Ok(response);
        }

        return Results.BadRequest();
    }

    private static IAsyncEnumerable<ChatChunkResponse> OnPostChatAsync(
        ChatRequest request,
        ReadRetrieveReadChatService chatService,
        CancellationToken cancellationToken)
    {
        return chatService.ReplyOnYourDataStreamingAsync(
                request.History, request.Overrides);//, cancellationToken);
    }

    private static IResult OnGetCitationBaseUrl(
        IOptions<AppSettings> options)
    {
        return TypedResults.Ok(options.Value.ToCitationBaseUrl());
    }

    private static async Task<IResult> OnPostDocumentAsync(
        [FromForm] IFormFileCollection files,
        [FromServices] AzureBlobStorageService service,
        [FromServices] ILogger<AzureBlobStorageService> logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Upload documents");

        var response = await service.UploadFilesAsync(files, cancellationToken);

        logger.LogInformation("Upload documents: {x}", response);

        return TypedResults.Ok(response);
    }

    private static async IAsyncEnumerable<DocumentResponse> OnGetDocumentsAsync(
        BlobContainerClient client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in client.GetBlobsAsync(BlobTraits.Metadata, cancellationToken: cancellationToken))
        {
            if (blob is not null
                and { Deleted: false })
            {
                var metadata = blob.Metadata;
                var documentProcessingStatus = GetMetadataEnumOrDefault<DocumentProcessingStatus>(
                    metadata, nameof(DocumentProcessingStatus), DocumentProcessingStatus.NotProcessed);
                if (documentProcessingStatus == DocumentProcessingStatus.NotProcessed_ToBeDeleted
                    || documentProcessingStatus == DocumentProcessingStatus.Hidden)
                {
                    // We do not display to the documents list the documents that are for deletion
                    // Or are marked as hidden
                    continue;
                }

                var embeddingType = GetMetadataEnumOrDefault<EmbeddingType>(
                    metadata, nameof(EmbeddingType), EmbeddingType.AzureSearch);

                var props = blob.Properties;
                var baseUri = client.Uri;
                var builder = new UriBuilder(baseUri);
                builder.Path += $"/{blob.Name}";

                yield return new(
                    blob.Name,
                    props.ContentType,
                    props.ContentLength ?? 0,
                    props.LastModified,
                    builder.Uri,
                    documentProcessingStatus,
                    embeddingType);

                static TEnum GetMetadataEnumOrDefault<TEnum>(
                    IDictionary<string, string> metadata,
                    string key,
                    TEnum @default) where TEnum : struct => metadata.TryGetValue(key, out var value)
                        && Enum.TryParse<TEnum>(value, out var status)
                            ? status
                            : @default;
            }
        }
    }

    private static async Task<IResult> OnGetDocumentAsync(
        string documentName,
        [FromServices] BlobContainerClient client,
        CancellationToken cancellationToken)
    {
        var blobClient = client.GetBlobClient(documentName);
        if (await blobClient.ExistsAsync())
        {
            Response<BlobDownloadStreamingResult> blobResult = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);

            var documentExtension = Path.GetExtension(documentName);
            string contentType;
            if (documentExtension == ".pdf")
            {
                contentType = "application/pdf";
            }
            else
            {
                // TODO: Implement the rest of the content types
                contentType = "";
            }

            return Results.File(blobResult.Value.Content, contentType);

            //using MemoryStream ms = new MemoryStream();
            //blobResult.Value.Content.CopyTo(ms);

            //var mimeType = "image/png";
            //var path = @"path_to_png.png";
            //return Results.File(path, contentType: mimeType);

            //return TypedResults.Ok(ms.ToArray());
        }

        return TypedResults.NotFound($"""Document with name: {documentName} was not found""");
    }

    private static async Task<IResult> OnPostImagePromptAsync(
        PromptRequest prompt,
        IAIClientService clientService,
        IOptions<AppSettings> options,
        CancellationToken cancellationToken)
    {
        // TODO: Use the Image creation model name
        var deploymentId = options.Value.AzureOpenAiChatGptDeployment;
        ArgumentNullException.ThrowIfNullOrEmpty(deploymentId);

        ImageGenerationOptions? imageGenerationOptions = null;

        ClientResult<GeneratedImage> result = await clientService
            .GetImageClient(deploymentId)
            .GenerateImageAsync(prompt.Prompt, imageGenerationOptions, cancellationToken);

        GeneratedImage generatedImage = result.Value;
        Uri imageUrl = result.Value.ImageUri;
        var response = new ImageResponse(DateTime.UtcNow, [generatedImage.ImageUri]);

        return TypedResults.Ok(response);
    }

    #endregion API methods implementation

    #endregion Private Methods
}
