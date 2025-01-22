using System.ClientModel;
using System.Text;
using Azure.AI.OpenAI.Chat;
using Azure.Core;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using MinimalApi.Models;
using OpenAI;
using OpenAI.Chat;
using Shared.Domain;
using Shared.Models.Settings;
using Shared.Services.AI.Interface;
using Shared.Services.Interfaces;

namespace MinimalApi.Services;

public class ReadRetrieveReadChatService
{
    #region Private Fields

    private readonly ILogger<ReadRetrieveReadChatService> _logger;

    private readonly ISearchService _searchClient;
    private readonly IAIClientService _clientService;
    private readonly AppSettings _appSettings;
    private readonly IComputerVisionService? _visionService;
    private readonly TokenCredential? _tokenCredential;
    private readonly Kernel _kernel;

    #endregion Private Fields

    #region Constructor

    public ReadRetrieveReadChatService(
        ILogger<ReadRetrieveReadChatService> logger,
        ISearchService searchClient,
        IAIClientService clientService,
        AppSettings appSettings,
        IComputerVisionService? visionService = null,
        TokenCredential? tokenCredential = null)
    {
        _logger = logger;

        _searchClient = searchClient;
        _clientService = clientService;
        _appSettings = appSettings;
        _visionService = visionService;
        _tokenCredential = tokenCredential;

        _kernel = GetSemanticKernel(clientService.AIClient);
    }

    #endregion Constructor

    #region Public Methods

    /// <summary>
    /// This is the method were we manually retrieving the documents from Azure search service, and provide the documents to the Azure OpenAI request
    /// </summary>
    /// <param name="history"></param>
    /// <param name="overrides"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<ApproachResponse> ReplyAsync(
        ChatTurn[] history,
        RequestOverrides? overrides,
        CancellationToken cancellationToken = default)
    {
        var top = overrides?.Top ?? 3;
        var useSemanticCaptions = overrides?.SemanticCaptions ?? false;
        var useSemanticRanker = overrides?.SemanticRanker ?? false;
        var excludeCategory = overrides?.ExcludeCategory ?? null;
        var filter = excludeCategory is null ? null : $"category ne '{excludeCategory}'";
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
#pragma warning disable SKEXP0001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var embedding = _kernel.GetRequiredService<ITextEmbeddingGenerationService>();
#pragma warning restore SKEXP0001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        float[]? embeddings = null;
        var question = history.LastOrDefault()?.User is { } userQuestion
            ? userQuestion
            : throw new InvalidOperationException("Use question is null");
        if (overrides?.RetrievalMode != RetrievalMode.Text && embedding is not null)
        {
#pragma warning disable SKEXP0001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            embeddings = (await embedding.GenerateEmbeddingAsync(question, cancellationToken: cancellationToken)).ToArray();
#pragma warning restore SKEXP0001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        }

        //_kernel.InvokePromptAsync
        //_kernel.CreatePluginFromPromptDirectory

        // step 1
        // use llm to get query if retrieval mode is not vector
        string? query = null;
        if (overrides?.RetrievalMode != RetrievalMode.Vector)
        {
            var getQueryChat = new ChatHistory(
                @"You are a helpful AI assistant, generate search query for followup question.
                Make your respond simple and precise. Return the query only, do not return any other text.
                e.g.
                Northwind Health Plus AND standard plan.
                standard plan AND dental AND employee benefit.
                ");

            //// Enable auto function calling
            //OpenAIPromptExecutionSettings openAIPromptExecutionSettings = new()
            //{
            //    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
            //};

            getQueryChat.AddUserMessage(question);
            var result = await chat.GetChatMessageContentAsync(
                getQueryChat,
                //// Enable auto function calling
                //executionSettings: openAIPromptExecutionSettings,
                //kernel: _kernel,
                cancellationToken: cancellationToken);

            query = result.Content ?? throw new InvalidOperationException("Failed to get search query");
        }

        // step 2
        // use query to search related docs
        var documentContentList = await _searchClient.QueryDocumentsAsync(query, embeddings, overrides, cancellationToken);

        string documentContents = string.Empty;
        if (documentContentList.Length == 0)
        {
            documentContents = "no source available.";
        }
        else
        {
            documentContents = string.Join("\r", documentContentList.Select(x => $"{x.Title}:{x.Content}"));
        }

        // step 2.5
        // retrieve images if _visionService is available
        SupportingImageRecord[]? images = default;
        if (_visionService is not null)
        {
            var queryEmbeddings = await _visionService.VectorizeTextAsync(query ?? question, cancellationToken);
            images = await _searchClient.QueryImagesAsync(query, queryEmbeddings.vector, overrides, cancellationToken);
        }

        // step 3
        // put together related docs and conversation history to generate answer
        var answerChat = new ChatHistory(@"You are a system assistant who helps the company employees with their questions.");

        // add chat history
        foreach (var turn in history)
        {
            answerChat.AddUserMessage(turn.User);
            if (turn.Bot is { } botMessage)
            {
                answerChat.AddAssistantMessage(botMessage);
            }
        }

        if (images != null && images.Any())
        {
            var prompt = @$"## Source ##
                {documentContents}
                ## End ##

                Answer question based on available source and images.
                Your answer needs to be a json object with answer and thoughts field.
                Don't put your answer between ```json and ```, return the json string directly. e.g {{""answer"": ""I don't know"", ""thoughts"": ""I don't know""}}";

            var tokenRequestContext = new TokenRequestContext(new[] { "https://storage.azure.com/.default" });
            var sasToken = await (_tokenCredential?.GetTokenAsync(tokenRequestContext, cancellationToken) ?? throw new InvalidOperationException("Failed to get token"));
            var sasTokenString = sasToken.Token;
            var imageUrls = images.Select(x => $"{x.Url}?{sasTokenString}").ToArray();

            var collection = new ChatMessageContentItemCollection();
            collection.Add(new TextContent(prompt));

            foreach (var imageUrl in imageUrls)
            {
                collection.Add(new ImageContent(new Uri(imageUrl)));
            }

            answerChat.AddUserMessage(collection);
        }
        else
        {
            var prompt = @$"Give your answer based on the question provided and the Documents Source.
                # Documents Source
                {documentContents}
                # End of Documents Source

                # Format of the response
                You answer needs to be a json object with the following format. Don't put your answer between ```json or ```, return the json object directly.
                {{
                    ""answer"": // the answer to the question, add a source reference to the end of each sentence. e.g. Apple is a fruit [reference1.pdf][reference2.pdf]. If no source available, put the answer as I don't know. You will always reply with a Markdown formatted response
                    ""thoughts"": // brief thoughts on how you came up with the answer, e.g. what sources you used, what you thought about, etc.
                }}";
            //Don't put your answer between ```json or ```, return the json object directly.";
            answerChat.AddUserMessage(prompt);
        }

        var promptExecutingSetting = new OpenAIPromptExecutionSettings
        {
            MaxTokens = 1024,
            Temperature = overrides?.Temperature ?? 0.7
        };

        //// Also check the below
        //var stream = chat.GetStreamingChatMessageContentsAsync(....)
        //await foreach (var content in stream)
        //{
        //    yield return content.Content;
        //}

        // get answer
        var answer = await chat.GetChatMessageContentAsync(
            answerChat,
            promptExecutingSetting,
            cancellationToken: cancellationToken);

        var answerJson = answer.Content ?? throw new InvalidOperationException("Failed to get search query");
        _logger.LogInformation("""Answer retrived from 'GetChatMessageContentAsync': '{Answer}'""", answerJson);

        //var answerObject = JsonSerializer.Deserialize<JsonElement>(answerJson);
        var answerObject = GetJsonElement(answerJson);
        var ans = answerObject.GetProperty("answer").GetString() ?? throw new InvalidOperationException("Failed to get answer");
        var thoughts = answerObject.GetProperty("thoughts").GetString() ?? throw new InvalidOperationException("Failed to get thoughts");

        // step 4
        // add follow up questions if requested
        if (overrides?.SuggestFollowupQuestions is true)
        {
            var followUpQuestionChat = new ChatHistory(@"You are a helpful AI assistant");
            followUpQuestionChat.AddUserMessage($@"Generate three follow-up question based on the answer you just generated.
                # Answer
                {ans}

                # Format of the response
                Return the follow-up question as a json string list. Don't put your answer between ```json and ```, return the json string directly.
                e.g.
                [
                    ""What is the deductible?"",
                    ""What is the co-pay?"",
                    ""What is the out-of-pocket maximum?""
                ]");

            var followUpQuestions = await chat.GetChatMessageContentAsync(
                followUpQuestionChat,
                cancellationToken: cancellationToken);

            var followUpQuestionsJson = followUpQuestions.Content ?? throw new InvalidOperationException("Failed to get search query");
            _logger.LogInformation("""Answer retrived from 'GetChatMessageContentAsync': '{Answer}'""", followUpQuestionsJson);

            var followUpQuestionsObject = JsonSerializer.Deserialize<JsonElement>(followUpQuestionsJson);
            var followUpQuestionsList = followUpQuestionsObject.EnumerateArray().Select(x => x.GetString()).ToList();

            foreach (var followUpQuestion in followUpQuestionsList)
            {
                ans += $" <<{followUpQuestion}>> ";
            }
        }

        return new ApproachResponse(ans, thoughts, documentContentList, images, _appSettings.ToCitationBaseUrl());
    }

    /// <summary>
    /// This is the method were the Azure OpenAI is handling the document retrieval and usage into the user prompt
    /// </summary>
    /// <param name="history"></param>
    /// <param name="overrides"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ApproachResponse> ReplyOnYourDataAsync(
        ChatTurn[] history,
        RequestOverrides? overrides,
        CancellationToken cancellationToken = default)
    {
        var replyOnYourDataParams = ValidateAndGetChatCompletionsOptionsAndParams(history, overrides);

        // get answer
        //var answer = await _openAIClient.GetChatCompletionsAsync(replyOnYourDataParams.ChatCompletionsOptions, cancellationToken: cancellationToken);
        ClientResult<ChatCompletion> response = await _clientService
            .GetChatClient(replyOnYourDataParams.AIDeploymentName)
            .CompleteChatAsync(replyOnYourDataParams.ChatMessages, replyOnYourDataParams.ChatCompletionsOptions, cancellationToken: cancellationToken);

        ChatCompletion aiAnswer = response.Value ?? throw new InvalidOperationException("Failed to get search query");
        _logger.LogInformation("""Answer retrieved from 'CompleteChatAsync': '{Answer}'""", aiAnswer);

        var ans = aiAnswer.Content[0]?.Text ?? throw new InvalidOperationException("Failed to get answer");
        var thoughts = "";

        // step 4
        // add follow up questions if requested
        if (overrides?.SuggestFollowupQuestions is true)
        {
            var messages = new List<ChatMessage>()
            {
                new SystemChatMessage(@"You are a helpful AI assistant"),
                new UserChatMessage($@"Generate three follow-up question based on the answer you just generated.
                    # Answer
                    {ans}

                    # Format of the response
                    Return the follow-up question as a json string list. Don't put your answer between ```json and ```, return the json string directly.
                    e.g.
                    [
                        ""What is the deductible?"",
                        ""What is the co-pay?"",
                        ""What is the out-of-pocket maximum?""
                    ]")
            };

            ClientResult<ChatCompletion> followUpQuestionsAnswer = await _clientService
                .GetChatClient(replyOnYourDataParams.AIDeploymentName)
                .CompleteChatAsync(messages, cancellationToken: cancellationToken);

            ChatCompletion followUpQuestionsAIAnswer = followUpQuestionsAnswer.Value ?? throw new InvalidOperationException("Failed to get followUp questions");
            _logger.LogInformation("""Answer retrieved from 'CompleteChatAsync': '{Answer}'""", followUpQuestionsAIAnswer);

            var followUpQuestions = followUpQuestionsAIAnswer.Content[0]?.Text ?? throw new InvalidOperationException("Failed to get answer");

            var followUpQuestionsObject = JsonSerializer.Deserialize<JsonElement>(followUpQuestions);
            var followUpQuestionsList = followUpQuestionsObject.EnumerateArray().Select(x => x.GetString()).ToList();

            foreach (var followUpQuestion in followUpQuestionsList)
            {
                ans += $" <<{followUpQuestion}>> ";
            }
        }

        var documentContentList = new SupportingContentRecord[aiAnswer.Content.Count];
        for (var x = 0; x < aiAnswer.Content.Count; x++)
        {
            var citation = aiAnswer.Content.ElementAt(x);

            //documentContentList[x] = new SupportingContentRecord(citation.ImageUri., citation.Content);
            //documentContentList[x] = new SupportingContentRecord(citation.Filepath, citation.Content);

            // Format the response by adding the desired document reference
            //ans = ans.Replace($"[doc{x + 1}]", $"[{citation.Filepath}]");
        }

        return new ApproachResponse(ans, thoughts, documentContentList, null, _appSettings.ToCitationBaseUrl());
    }

    /// <summary>
    /// This is the method were the Azure OpenAI is handling the document retrieval and usage into the user prompt
    /// </summary>
    /// <param name="history"></param>
    /// <param name="overrides"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async IAsyncEnumerable<ChatChunkResponse> ReplyOnYourDataStreamingAsync(
        ChatTurn[] history,
        RequestOverrides? overrides,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var replyOnYourDataParams = ValidateAndGetChatCompletionsOptionsAndParams(history, overrides);

        // Get AI answer from prompt
        AsyncCollectionResult<StreamingChatCompletionUpdate> response = _clientService
            .GetChatClient(replyOnYourDataParams.AIDeploymentName)
            .CompleteChatStreamingAsync(replyOnYourDataParams.ChatMessages, replyOnYourDataParams.ChatCompletionsOptions, cancellationToken: cancellationToken);

        var answer = new StringBuilder();
        string temporaryAnswerWithDocument = "";
#pragma warning disable AOAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        IEnumerable<ChatCitation>? citations = null;
#pragma warning restore AOAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        bool returnAIOriginalContent = true;
        StringBuilder contentUpdate;
        await foreach (StreamingChatCompletionUpdate update in response.WithCancellation(cancellationToken))
        {
            if (update.ContentUpdate is { Count: > 0 })
            {
                contentUpdate = new StringBuilder();
                foreach (ChatMessageContentPart part in update.ContentUpdate)
                {
                    // Construct the hole answer, piece by piece, to be used for the followUp questions
                    answer.Append(part.Text);
                    contentUpdate.Append(part.Text);

                    // Citations references are returned by the services as [doc1], [doc2], ...
                    // If true, we store it temporally before returning it as answer, to replace it with the appropriate citation
                    // Else, if the temporaryAnswerWithDocument already has a value, then we already identified the start of the reference document, in previews choice/chunk
                    if (part.Text.Contains('[')
                        || temporaryAnswerWithDocument != "")
                    {
                        // Add it into the StringBuilder and continue
                        temporaryAnswerWithDocument += part.Text;

                        // This should never happen, since we are receiving the citations from the first ever choice/chunk
                        // We continue the iteration, until we receive the citations
                        if (citations == null)
                        {
                            continue;
                        }

                        // We need to make sure that we have all the complete document references part included into the current string
                        // eg. [doc1][doc2]
                        // An incomplete references part might be [doc1][doc
                        var docReferenceOpeningsCount = temporaryAnswerWithDocument.Count(c => c == '[');
                        var docReferenceClosingCount = temporaryAnswerWithDocument.Count(c => c == ']');

                        // If true, then we can go ahead and replace with the citations reference
                        if (docReferenceOpeningsCount == docReferenceClosingCount)
                        {
                            for (int index = temporaryAnswerWithDocument.IndexOf("[doc"); index > -1; index = temporaryAnswerWithDocument.IndexOf("[doc", index + 1))
                            {
                                // Extract the hole document reference from the string
                                var documentReference = temporaryAnswerWithDocument.Substring(index, temporaryAnswerWithDocument.IndexOf("]", index) + 1 - index);
                                // Get citation index number
                                var documentReferenceNumber = Convert.ToInt32(documentReference.Replace("[doc", "").Replace("]", ""));

                                // Some citations might have more than one document refences
                                var citationFilePath = citations.ElementAt(documentReferenceNumber - 1).FilePath;
                                // Replace all occurrences into the string with the citation
                                if (!citationFilePath.Contains(','))
                                {
                                    temporaryAnswerWithDocument = temporaryAnswerWithDocument.Replace($"[doc{documentReferenceNumber}]", $"[{citationFilePath}]");
                                }
                                else
                                {
                                    var citationFilePathsArr = citationFilePath.Split(',');
                                    string documentReferences = "";
                                    for (var x = 0; x < citationFilePathsArr.Length; x++)
                                    {
                                        documentReferences += $"[{citationFilePathsArr[x]}]";
                                    }

                                    temporaryAnswerWithDocument = temporaryAnswerWithDocument.Replace($"[doc{documentReferenceNumber}]", documentReferences);
                                }
                            }

                            // We finally returning the chunk back too the response
                            yield return new ChatChunkResponse(temporaryAnswerWithDocument.Length, temporaryAnswerWithDocument);
                            // We are making sure tho empty the string before exiting
                            temporaryAnswerWithDocument = "";
                            // Finally, we mark the returnAIOriginalContent flag as false, since we have tampered it with our actions
                            returnAIOriginalContent = false;
                        }

                        continue;
                    }
                }

                // Means that the content has already been returned, by first tampering it with our actions
                if (!returnAIOriginalContent)
                {
                    returnAIOriginalContent = true;
                    continue;
                }

                yield return new ChatChunkResponse(contentUpdate.Length, contentUpdate.ToString());
                // Always have a delay after each return to simulate the streaming in the frontend
                await Task.Delay(30);
            }

            if ((citations == null || !citations.Any()))
            {
#pragma warning disable AOAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                ChatMessageContext chatMessageContext = update.GetMessageContext();
#pragma warning restore AOAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                if (chatMessageContext.Citations.Any())
                {
                    citations = chatMessageContext.Citations;
                }
            }
        }

        if (temporaryAnswerWithDocument != "")
        {
            yield return new ChatChunkResponse(temporaryAnswerWithDocument.Length, temporaryAnswerWithDocument);
        }

        // Get follow up questions, if requested
        if (overrides?.SuggestFollowupQuestions is true)
        {
            AsyncCollectionResult<StreamingChatCompletionUpdate> followUpQuestionsAnswer = _clientService
                .GetChatClient(replyOnYourDataParams.AIDeploymentName)
                .CompleteChatStreamingAsync(
                [
                    new SystemChatMessage(@"You are a helpful AI assistant"),
                    new UserChatMessage($@"Generate three follow-up question based on the answer you just generated.
                        # Answer
                        {answer.ToString()}

                        # Format of the response
                        Return each follow-up question between << and >>
                        e.g.
                        &nbsp;<<follow-up question 1>>&nbsp;&nbsp;<<follow-up question 2>>&nbsp;&nbsp;<<follow-up question 3>>&nbsp;")
                ], cancellationToken: cancellationToken);

            await foreach (StreamingChatCompletionUpdate update in followUpQuestionsAnswer.WithCancellation(cancellationToken))
            {
                foreach (ChatMessageContentPart part in update.ContentUpdate)
                {
                    yield return new ChatChunkResponse(part.Text.Length, part.Text);
                }
            }
        }
    }

    #endregion Public Methods

    #region Private Methods

    private Kernel GetSemanticKernel(OpenAIClient client)
    {
        var kernelBuilder = Kernel.CreateBuilder();

        if (!_appSettings.UseAOAI)
        {
            var deployedModelName = _appSettings.OpenAiChatGptDeployment;
            ArgumentNullException.ThrowIfNullOrWhiteSpace(deployedModelName);
            kernelBuilder = kernelBuilder.AddOpenAIChatCompletion(deployedModelName, client);

            var embeddingModelName = _appSettings.OpenAiEmbeddingDeployment;
            ArgumentNullException.ThrowIfNullOrWhiteSpace(embeddingModelName);
#pragma warning disable SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            kernelBuilder = kernelBuilder.AddOpenAITextEmbeddingGeneration(embeddingModelName, client);
#pragma warning restore SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        }
        else
        {
            var deployedModelName = _appSettings.AzureOpenAiChatGptDeployment;
            ArgumentNullException.ThrowIfNullOrWhiteSpace(deployedModelName);
            kernelBuilder = kernelBuilder.AddAzureOpenAIChatCompletion(deployedModelName, (AzureOpenAIClient)client);

            var embeddingModelName = _appSettings.AzureOpenAiEmbeddingDeployment;
            if (!string.IsNullOrEmpty(embeddingModelName))
            {
#pragma warning disable SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                kernelBuilder = kernelBuilder.AddAzureOpenAITextEmbeddingGeneration(embeddingModelName, (AzureOpenAIClient)client);
#pragma warning restore SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            }
        }

        // using Microsoft.SemanticKernel.CoreSkills;
        //kernelBuilder.Plugins.AddFromType<TimePlugin>();
        //kernelBuilder.Plugins.AddFromPromptDirectory("dir_name");

        return kernelBuilder.Build();
    }

    private JsonElement GetJsonElement(string aiMessage)
    {
        JsonElement answerObject;
        try
        {
            answerObject = JsonSerializer.Deserialize<JsonElement>(aiMessage);
        }
        catch
        {
            var json = @$"
                {{
                    ""answer"": ""{aiMessage}"",
                    ""thoughts"": ""No thoughts provided""
                }}";

            answerObject = JsonSerializer.Deserialize<JsonElement>(json);
        }

        return answerObject;
    }

    private ReplyOnYourDataParamsDTO ValidateAndGetChatCompletionsOptionsAndParams(ChatTurn[] history, RequestOverrides? overrides)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(history.LastOrDefault()?.User);

        var aiDeploymentName = _appSettings.AzureOpenAiChatGptDeployment;
        ArgumentNullException.ThrowIfNullOrEmpty(aiDeploymentName);

        var aiEmbeddingDeploymentName = _appSettings.AzureOpenAiEmbeddingDeployment;
        ArgumentNullException.ThrowIfNullOrEmpty(aiEmbeddingDeploymentName);

        var azureSearchServiceEndpoint = _appSettings.AzureSearchServiceEndpoint;
        ArgumentNullException.ThrowIfNullOrEmpty(azureSearchServiceEndpoint);

        var azureSearchIndex = _appSettings.AzureSearchIndex;
        ArgumentNullException.ThrowIfNullOrEmpty(azureSearchIndex);

        var useSemanticRanker = overrides?.SemanticRanker ?? false;
#pragma warning disable AOAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        DataSourceQueryType queryType;
        if (overrides?.RetrievalMode == RetrievalMode.Hybrid)
        {
            if (useSemanticRanker)
            {
                queryType = DataSourceQueryType.VectorSemanticHybrid;
            }
            else
            {
                queryType = DataSourceQueryType.VectorSimpleHybrid;
            }
        }
        else if (useSemanticRanker)
        {
            queryType = DataSourceQueryType.Semantic;
        }
        else if (overrides?.RetrievalMode == RetrievalMode.Vector)
        {
            queryType = DataSourceQueryType.Vector;
        }
        else
        {
            queryType = DataSourceQueryType.Simple;
        }
#pragma warning restore AOAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        // step 1
        // put together the conversation history to generate answer
        var messages = new List<ChatMessage>()
        {
            new SystemChatMessage(@"You are a system assistant who helps the company employees with their questions.
                Code snippets must be generated to C# programming language, unless specified otherwise by the user.
                You will always reply with a Markdown formatted response.")
        };

        // add chat history
        foreach (var turn in history)
        {
            messages.Add(new UserChatMessage(turn.User));
            if (turn.Bot is { } botMessage)
            {
                messages.Add(new AssistantChatMessage(botMessage));
            }
        }

        var chatCompletionsOptions = new ChatCompletionOptions();
#pragma warning disable AOAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        chatCompletionsOptions.AddDataSource(new AzureSearchChatDataSource()
        {
            Endpoint = new Uri(azureSearchServiceEndpoint),
            Authentication = DataSourceAuthentication.FromSystemManagedIdentity(),
            IndexName = azureSearchIndex,
            TopNDocuments = overrides?.Top ?? 5,
            QueryType = queryType,
            OutputContexts = DataSourceOutputContexts.Citations,
            SemanticConfiguration = "default",
            VectorizationSource = DataSourceVectorizer.FromDeploymentName(aiEmbeddingDeploymentName),
            FieldMappings = new DataSourceFieldMappings
            {
                TitleFieldName = VectorizeSearchEntity.IdAsJsonPropertyName(),
                FilePathFieldName = VectorizeSearchEntity.SourceFileAsJsonPropertyName(),
                ContentFieldNames =
                {
                    VectorizeSearchEntity.ContentAsJsonPropertyName()
                },
                VectorFieldNames =
                {
                    VectorizeSearchEntity.EmbeddingAsJsonPropertyName()
                }
            }
        });
#pragma warning restore AOAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        return new ReplyOnYourDataParamsDTO(messages, chatCompletionsOptions, aiDeploymentName, aiEmbeddingDeploymentName, azureSearchServiceEndpoint, azureSearchIndex);
    }

    #endregion Private Methods
}
