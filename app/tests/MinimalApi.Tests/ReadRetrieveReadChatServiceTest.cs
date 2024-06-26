﻿using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MinimalApi.Services;
using NSubstitute;
using Shared.Models;
using Shared.Models.Settings;
using Shared.Services;
using Shared.Services.Interfaces;

namespace MinimalApi.Tests;
public class ReadRetrieveReadChatServiceTest
{
    [EnvironmentVariablesFact(
        "AZURE_OPENAI_ENDPOINT",
        "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
        "AZURE_OPENAI_CHATGPT_DEPLOYMENT")]
    public async Task NorthwindHealthQuestionTest_TextOnlyAsync()
    {
        var logger = Substitute.For<ILogger<ReadRetrieveReadChatService>>();

        var documentSearchService = Substitute.For<ISearchService>();
        documentSearchService.QueryDocumentsAsync(Arg.Any<string?>(), Arg.Any<float[]?>(), Arg.Any<RequestOverrides?>(), Arg.Any<CancellationToken>())
                .Returns(new SupportingContentRecord[]
                {
                    new SupportingContentRecord("Northwind_Health_Plus_Benefits_Details-52.pdf", "The Northwind Health Plus plan covers a wide range of services related to the treatment of SUD. These services include inpatient and outpatient treatment, counseling, and medications to help with recovery. It also covers mental health services and support for family members of those with SUD"),
                    new SupportingContentRecord("Northwind_Health_Plus_Benefits_Details-90.pdf", "This contract includes the plan documents that you receive from Northwind Health, the Northwind Health Plus plan summary, and any additional contracts or documents that you may have received from Northwind Health. It is important to remember that any changes made to this plan must be in writing and signed by both you and Northwind Health."),
                });

        var openAIEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? throw new InvalidOperationException();
        var openAIClient = new OpenAIClient(new Uri(openAIEndpoint), new DefaultAzureCredential());
        var openAiEmbeddingDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? throw new InvalidOperationException();
        var openAIChatGptDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_CHATGPT_DEPLOYMENT") ?? throw new InvalidOperationException();

        var appSettings = Substitute.For<AppSettings>();
        appSettings.AzureOpenAiChatGptDeployment.Returns(openAIChatGptDeployment);
        appSettings.AzureOpenAiEmbeddingDeployment.Returns(openAiEmbeddingDeployment);
        appSettings.AzureOpenAiServiceEndpoint.Returns(openAIEndpoint);
        appSettings.AzureStorageAccountEndpoint.Returns("https://northwindhealth.blob.core.windows.net/");
        appSettings.AzureStorageContainer.Returns("northwindhealth");
        appSettings.UseAOAI.Returns(true);

        var chatService = new ReadRetrieveReadChatService(logger, documentSearchService, openAIClient, appSettings);

        var history = new ChatTurn[]
        {
            new ChatTurn("What is included in my Northwind Health Plus plan that is not in standard?", "user"),
        };
        var overrides = new RequestOverrides
        {
            RetrievalMode = RetrievalMode.Text,
            Top = 2,
            SemanticCaptions = true,
            SemanticRanker = true,
            SuggestFollowupQuestions = true,
        };

        var response = await chatService.ReplyAsync(history, overrides);

        // TODO
        // use AutoGen agents to evaluate if answer
        // - has follow up question
        // - has correct answer
        // - has has correct format for source reference.

        response.DataPoints?.Count().Should().Be(2);
        response.Answer.Should().NotBeNullOrEmpty();
        response.CitationBaseUrl.Should().Be("https://northwindhealth.blob.core.windows.net/northwindhealth");
    }

    [EnvironmentVariablesFact(
        "OPENAI_API_KEY",
        "AZURE_SEARCH_INDEX",
        "AZURE_COMPUTER_VISION_ENDPOINT",
        "AZURE_SEARCH_SERVICE_ENDPOINT")]
    public async Task FinancialReportTestAsync()
    {
        var logger = Substitute.For<ILogger<ReadRetrieveReadChatService>>();

        var azureSearchServiceEndpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_SERVICE_ENDPOINT") ?? throw new InvalidOperationException();
        var azureSearchIndex = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX") ?? throw new InvalidOperationException();
        var azureCredential = new DefaultAzureCredential();
        var azureSearchService = new AzureSearchService(new SearchClient(new Uri(azureSearchServiceEndpoint), azureSearchIndex, azureCredential));

        var openAIAPIKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException();
        var openAIClient = new OpenAIClient(openAIAPIKey);

        var azureComputerVisionEndpoint = Environment.GetEnvironmentVariable("AZURE_COMPUTER_VISION_ENDPOINT") ?? throw new InvalidOperationException();
        var apiVersion = Environment.GetEnvironmentVariable("AZURE_COMPUTER_VISION_API_VERSION") ?? "2024-02-01";
        var modelVersion = Environment.GetEnvironmentVariable("AZURE_COMPUTER_VISION_MODEL_VERSION") ?? "2023-04-15";
        using var httpClient = new HttpClient();
        var azureComputerVisionService = new AzureComputerVisionService(httpClient, azureComputerVisionEndpoint, apiVersion, modelVersion, azureCredential);

        var appSettings = Substitute.For<AppSettings>();
        appSettings.UseAOAI.Returns(false);
        appSettings.OpenAiChatGptDeployment.Returns("gpt-4-vision-preview");
        appSettings.OpenAiEmbeddingDeployment.Returns("text-embedding-ada-002");
        appSettings.AzureStorageAccountEndpoint.Returns("https://northwindhealth.blob.core.windows.net/");
        appSettings.AzureStorageContainer.Returns("northwindhealth");

        var chatService = new ReadRetrieveReadChatService(
            logger,
            azureSearchService,
            openAIClient,
            appSettings,
            azureComputerVisionService,
            azureCredential);

        var history = new ChatTurn[]
        {
            new ChatTurn("What's 2023 financial report", "user"),
        };
        var overrides = new RequestOverrides
        {
            RetrievalMode = RetrievalMode.Hybrid,
            Top = 2,
            SemanticCaptions = true,
            SemanticRanker = true,
            SuggestFollowupQuestions = false,
            Temperature = 0,
        };

        var response = await chatService.ReplyAsync(history, overrides);

        // TODO
        // use AutoGen agents to evaluate if answer
        // - has follow up question
        // - has correct answer
        // - has has correct format for source reference.

        response.DataPoints?.Count().Should().Be(0);
        response.Images?.Count().Should().Be(2);
        response.Answer.Should().NotBeNullOrEmpty();
    }
}
