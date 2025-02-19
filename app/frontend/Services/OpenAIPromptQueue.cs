﻿using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace ClientApp.Services;

public sealed class OpenAIPromptQueue(
    IServiceProvider provider,
    ILogger<OpenAIPromptQueue> logger)
{
    #region Fields

    private const string AIEndpoint = "api/openai/chat";
    private const string AIWithDocumentsEndpoint = "api/chat";

    private readonly StringBuilder _responseBuffer = new();
    private Task? _processPromptTask = null;

    #endregion Fields

    #region Public Methods

    public void Enqueue(string prompt, Func<PromptResponse, Task> handler)
    {
        var promptRequest = new PromptRequest { Prompt = prompt };

        EnqueueAPI(prompt, promptRequest, false, handler);
    }

    public void Enqueue(ChatRequest chatRequest, Func<PromptResponse, Task> handler)
    {
        EnqueueAPI(chatRequest.History.Last().User, chatRequest, true, handler);
    }

    #endregion Public Methods

    #region Private Methods

    public void EnqueueAPI(string prompt, object bodyObject, bool useDocuments, Func<PromptResponse, Task> handler)
    {
        if (_processPromptTask is not null)
        {
            return;
        }

        _processPromptTask = Task.Run(async () =>
        {
            try
            {
                var endpoint = useDocuments ? AIWithDocumentsEndpoint : AIEndpoint;

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.SetBrowserResponseStreamingEnabled(true); // Enable response streaming

                var options = SerializerOptions.Default;
                var json = JsonSerializer.Serialize(bodyObject, options);
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                request.Content = body;

                using var scope = provider.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                using var client = factory.CreateClient(typeof(ApiClient).Name);

                // Be sure to use HttpCompletionOption.ResponseHeadersRead
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync();

                    await foreach (var chunk in JsonSerializer.DeserializeAsyncEnumerable<ChatChunkResponse>(stream, options))
                    {
                        if (chunk is null)
                        {
                            continue;
                        }

                        _responseBuffer.Append(chunk.Text);

                        await handler(
                            new PromptResponse(prompt, _responseBuffer.ToString()));
                    }
                }
            }
            catch (Exception ex)
            {
                await handler(
                    new PromptResponse(prompt, ex.Message, true));
            }
            finally
            {
                if (_responseBuffer.Length > 0)
                {
                    var responseText = NormalizeResponseText(_responseBuffer, logger);
                    await handler(
                        new PromptResponse(prompt, responseText, true));

                    _responseBuffer.Clear();
                }

                _processPromptTask = null;
            }
        });
    }

    private static string NormalizeResponseText(StringBuilder builder, ILogger logger)
    {
        if (builder is null or { Length: 0 })
        {
            return "";
        }

        var text = builder.ToString();

        logger.LogDebug("Before normalize\n\t{Text}", text);

        text = text.StartsWith("null,") ? text[5..] : text;
        text = text.Replace("\r", "\n")
            .Replace("\\n\\r", "\n")
            .Replace("\\n", "\n");

        text = Regex.Unescape(text);

        logger.LogDebug("After normalize:\n\t{Text}", text);

        return text;
    }

    #endregion Private Methods
}
