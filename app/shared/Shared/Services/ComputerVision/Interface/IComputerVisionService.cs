﻿namespace Shared.Services.Interfaces;

public interface IComputerVisionService
{
    public int Dimension { get; }

    Task<ImageEmbeddingResponse> VectorizeImageAsync(string imagePathOrUrl, CancellationToken ct = default);
    Task<ImageEmbeddingResponse> VectorizeTextAsync(string text, CancellationToken ct = default);
}
