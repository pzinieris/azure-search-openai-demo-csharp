using Azure.Core;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Shared.Domain;
using Shared.Extensions;
using Shared.Models.Settings;
using Shared.Services.Interfaces;

namespace MinimalApi.Services;

public class ReadRetrieveReadChatService
{
    #region Private Fields

    private readonly ILogger<ReadRetrieveReadChatService> _logger;

    private readonly ISearchService _searchClient;
    private readonly OpenAIClient _openAIClient;
    private readonly AppSettings _appSettings;
    private readonly IComputerVisionService? _visionService;
    private readonly TokenCredential? _tokenCredential;
    private readonly Kernel _kernel;

    #endregion Private Fields

    #region Constructor

    public ReadRetrieveReadChatService(
        ILogger<ReadRetrieveReadChatService> logger,
        ISearchService searchClient,
        OpenAIClient client,
        AppSettings appSettings,
        IComputerVisionService? visionService = null,
        TokenCredential? tokenCredential = null)
    {
        _logger = logger;

        _searchClient = searchClient;
        _openAIClient = client;
        _appSettings = appSettings;
        _visionService = visionService;
        _tokenCredential = tokenCredential;

        _kernel = GetSemanticKernel(client);
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
        if (string.IsNullOrWhiteSpace(history.LastOrDefault()?.User))
        {
            throw new InvalidOperationException("Use question is null");
        }

        var aiDeploymentName = _appSettings.AzureOpenAiChatGptDeployment;
        ArgumentNullException.ThrowIfNullOrEmpty(aiDeploymentName);

        var aiEmbedingDeploymentName = _appSettings.AzureOpenAiEmbeddingDeployment;
        ArgumentNullException.ThrowIfNullOrEmpty(aiEmbedingDeploymentName);

        var azureSearchServiceEndpoint = _appSettings.AzureSearchServiceEndpoint;
        ArgumentNullException.ThrowIfNullOrEmpty(azureSearchServiceEndpoint);

        var azureSearchIndex = _appSettings.AzureSearchIndex;
        ArgumentNullException.ThrowIfNullOrEmpty(azureSearchIndex);

        var useSemanticRanker = overrides?.SemanticRanker ?? false;
        AzureSearchQueryType queryType;
        if (overrides?.RetrievalMode == RetrievalMode.Hybrid)
        {
            if (useSemanticRanker)
            {
                queryType = AzureSearchQueryType.VectorSemanticHybrid;
            }
            else
            {
                queryType = AzureSearchQueryType.VectorSimpleHybrid;
            }
        }
        else if (useSemanticRanker)
        {
            queryType = AzureSearchQueryType.Semantic;
        }
        else if (overrides?.RetrievalMode == RetrievalMode.Vector)
        {
            queryType = AzureSearchQueryType.Vector;
        }
        else
        {
            queryType = AzureSearchQueryType.Simple;
        }

        // step 1
        // put together the conversation history to generate answer
        var messages = new List<ChatRequestMessage>()
        {
            new ChatRequestSystemMessage(@"You are a system assistant who helps the company employees with their questions.
                Code snippets must be generated to C# programming language, unless specified otherwise by the user.
                You will always reply with a Markdown formatted response.")
        };

        // add chat history
        foreach (var turn in history)
        {
            messages.Add(new ChatRequestUserMessage(turn.User));
            if (turn.Bot is { } botMessage)
            {
                messages.Add(new ChatRequestAssistantMessage(botMessage));
            }
        }

        var chatCompletionsOptions = new ChatCompletionsOptions(aiDeploymentName, messages)
        {
            AzureExtensionsOptions = new AzureChatExtensionsOptions()
            {
                Extensions =
                {
                    new AzureSearchChatExtensionConfiguration()
                    {
                        SearchEndpoint = new Uri(azureSearchServiceEndpoint),
                        Authentication = new OnYourDataSystemAssignedManagedIdentityAuthenticationOptions(),
                        IndexName = azureSearchIndex,
                        DocumentCount = overrides?.Top ?? 5,
                        QueryType = queryType,
                        SemanticConfiguration = "default",
                        VectorizationSource = new OnYourDataDeploymentNameVectorizationSource(aiEmbedingDeploymentName),
                        FieldMappingOptions = new AzureSearchIndexFieldMappingOptions()
                        {
                            TitleFieldName = nameof(VectorizeSearchEntity.Id).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity)),
                            FilepathFieldName = nameof(VectorizeSearchEntity.SourceFile).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity)),
                            ContentFieldNames =
                            {
                                nameof(VectorizeSearchEntity.Content).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity))
                            },
                            VectorFieldNames =
                            {
                                nameof(VectorizeSearchEntity.Embedding).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity))
                            }
                        }
                    }
                }
            }
            //ResponseFormat = ChatCompletionsResponseFormat.JsonObject
        };

        // get answer
        //var answer = await _openAIClient.GetChatCompletionsStreamingAsync(chatCompletionsOptions);        
        var answer = await _openAIClient.GetChatCompletionsAsync(chatCompletionsOptions, cancellationToken: cancellationToken);

        var aiAnswer = answer.Value ?? throw new InvalidOperationException("Failed to get search query");
        _logger.LogInformation("""Answer retrieved from 'GetChatCompletionsAsync': '{Answer}'""", aiAnswer);

        var aiMessage = aiAnswer.Choices[0]?.Message;
        var ans = aiMessage?.Content ?? throw new InvalidOperationException("Failed to get answer");
        var thoughts = "";

        // step 4
        // add follow up questions if requested
        if (overrides?.SuggestFollowupQuestions is true)
        {
            messages = new List<ChatRequestMessage>()
            {
                new ChatRequestSystemMessage(@"You are a helpful AI assistant"),
                new ChatRequestUserMessage($@"Generate three follow-up question based on the answer you just generated.
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
            chatCompletionsOptions = new ChatCompletionsOptions(aiDeploymentName, messages)
            {
                ResponseFormat = ChatCompletionsResponseFormat.JsonObject
            };

            var followUpQuestionsAnswer = await _openAIClient.GetChatCompletionsAsync(chatCompletionsOptions, cancellationToken: cancellationToken);

            var followUpQuestionsAIAnswer = followUpQuestionsAnswer.Value ?? throw new InvalidOperationException("Failed to get followUp questions");
            _logger.LogInformation("""Answer retrieved from 'GetChatCompletionsAsync': '{Answer}'""", followUpQuestionsAIAnswer);

            var followUpQuestionsAIMessage = followUpQuestionsAIAnswer.Choices[0]?.Message;
            var followUpQuestions = followUpQuestionsAIMessage?.Content ?? throw new InvalidOperationException("Failed to get answer");

            var followUpQuestionsObject = JsonSerializer.Deserialize<JsonElement>(followUpQuestions);
            var followUpQuestionsList = followUpQuestionsObject.EnumerateArray().Select(x => x.GetString()).ToList();

            foreach (var followUpQuestion in followUpQuestionsList)
            {
                ans += $" <<{followUpQuestion}>> ";
            }
        }

        var documentContentList = new SupportingContentRecord[aiMessage.AzureExtensionsContext.Citations.Count];
        for (var x = 0; x < aiMessage.AzureExtensionsContext.Citations.Count; x++)
        {
            var citation = aiMessage.AzureExtensionsContext.Citations.ElementAt(x);

            documentContentList[x] = new SupportingContentRecord(citation.Filepath, citation.Content);

            // Format the response by adding the desired document reference
            ans = ans.Replace($"[doc{x + 1}]", $"[{citation.Filepath}]");
        }

        return new ApproachResponse(ans, thoughts, documentContentList, null, _appSettings.ToCitationBaseUrl());
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
            kernelBuilder = kernelBuilder.AddAzureOpenAIChatCompletion(deployedModelName, client);

            var embeddingModelName = _appSettings.AzureOpenAiEmbeddingDeployment;
            if (!string.IsNullOrEmpty(embeddingModelName))
            {
#pragma warning disable SKEXP0010 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                kernelBuilder = kernelBuilder.AddAzureOpenAITextEmbeddingGeneration(embeddingModelName, client);
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

    #endregion Private Methods
}
