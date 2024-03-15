using Azure.Identity;
using FluentAssertions;
using Shared.Services;

namespace MinimalApi.Tests;

public class AzureComputerVisionServiceTest
{
    [EnvironmentVariablesFact(
        "AZURE_COMPUTER_VISION_ENDPOINT",
        "AZURE_COMPUTER_VISION_API_VERSION",
        "AZURE_COMPUTER_VISION_API_MODEL_VERSION")]
    public async Task VectorizeImageTestAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_COMPUTER_VISION_ENDPOINT") ?? throw new InvalidOperationException();
        var apiVersion = Environment.GetEnvironmentVariable("AZURE_COMPUTER_VISION_API_VERSION") ?? "2024-02-01";
        var apiModelVersion = Environment.GetEnvironmentVariable("AZURE_COMPUTER_VISION_API_MODEL_VERSION") ?? "2023-04-15";

        using var httpClient = new HttpClient();
        var imageUrl = @"https://learn.microsoft.com/azure/ai-services/computer-vision/media/quickstarts/presentation.png";

        var service = new AzureComputerVisionService(httpClient, endpoint, apiVersion, apiModelVersion, new DefaultAzureCredential());
        var result = await service.VectorizeImageAsync(imageUrl);

        result.modelVersion.Should().NotBeNullOrEmpty();

        // download image to local file, and verify the api on local image.
        var tempFile = Path.GetTempFileName();
        tempFile = Path.ChangeExtension(tempFile, ".png");
        try
        {
            using var client = new HttpClient();
            using var stream = await client.GetStreamAsync(imageUrl);
            using var fileStream = File.OpenWrite(tempFile);
            await stream.CopyToAsync(fileStream);
            fileStream.Flush();
            fileStream.Close();

            var localResult = await service.VectorizeImageAsync(tempFile);

            localResult.modelVersion.Should().NotBeNullOrEmpty();
            localResult.vector.Should().BeEquivalentTo(result.vector);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [EnvironmentVariablesFact(
        "AZURE_COMPUTER_VISION_ENDPOINT",
        "AZURE_COMPUTER_VISION_API_VERSION",
        "AZURE_COMPUTER_VISION_API_MODEL_VERSION")]
    public async Task VectorizeTextTestAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_COMPUTER_VISION_ENDPOINT") ?? throw new InvalidOperationException();
        var apiVersion = Environment.GetEnvironmentVariable("AZURE_COMPUTER_VISION_API_VERSION") ?? "2024-02-01";
        var apiModelVersion = Environment.GetEnvironmentVariable("AZURE_COMPUTER_VISION_API_MODEL_VERSION") ?? "2023-04-15";

        using var httpClient = new HttpClient();
        var service = new AzureComputerVisionService(httpClient, endpoint, apiVersion, apiModelVersion, new DefaultAzureCredential());
        var text = "Hello world";
        var result = await service.VectorizeTextAsync(text);

        result.modelVersion.Should().NotBeNullOrEmpty();
        result.vector.Length.Should().Be(1024);
    }
}
