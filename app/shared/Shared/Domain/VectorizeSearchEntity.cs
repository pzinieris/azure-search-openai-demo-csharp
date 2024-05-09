using System.Text.Json.Serialization;
using Azure.Search.Documents.Models;
using Shared.Extensions;

namespace Shared.Domain;
public sealed class VectorizeSearchEntity
{
    #region Properties

    #region Values retrieved from Azure Search service

    [JsonPropertyName("@search.score")]
    public decimal Score { get; set; }
    [JsonPropertyName("@search.rerankerScore")]
    public decimal RerankerScore { get; set; }

    #endregion Values retrieved from Azure Search service

    [JsonPropertyName("id")]
    public string Id { get; set; }
    [JsonPropertyName("content")]
    public string Content { get; set; }
    [JsonPropertyName("category")]
    public string Category
    {
        get => CategoryEnum.ToString();
        set
        {
            if (VectorizeSearchCategory.TryParse(value, true, out VectorizeSearchCategory enumValue))
            {
                CategoryEnum = enumValue;
            }
        }
    }
    [JsonIgnore]
    public VectorizeSearchCategory CategoryEnum { get; set; }
    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; set; }

    // Document
    [JsonPropertyName("sourcePage")]
    public string? SourcePage { get; set; }
    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }

    // Image
    [JsonPropertyName("imageEmbedding")]
    public float[]? ImageEmbedding { get; set; }

    #endregion Properties

    #region Contructor/s

    public VectorizeSearchEntity() { }

    private VectorizeSearchEntity(string id, string content, VectorizeSearchCategory category, string sourceFile)
    {
        Id = id;
        Content = content;
        CategoryEnum = category;
        SourceFile = sourceFile;
    }

    #endregion Contructor/s

    #region Public Methods

    #region Static Methods

    public static VectorizeSearchEntity CreateDocument(string id, string content, string sourceFile, string sourcePage, float[] embedding)
    {
        var entity = new VectorizeSearchEntity(id, content, VectorizeSearchCategory.Document, sourceFile);

        entity.SourcePage = sourcePage;
        entity.Embedding = embedding;

        return entity;
    }

    public static VectorizeSearchEntity CreateImage(string id, string content, string sourceFile, float[] embedding)
    {
        var entity = new VectorizeSearchEntity(id, content, VectorizeSearchCategory.Image, sourceFile);

        entity.ImageEmbedding = embedding;

        return entity;
    }

    #endregion Static Methods

    public SearchDocument AsSearchDocument()
    {
        if (CategoryEnum == VectorizeSearchCategory.Document)
        {
            return new SearchDocument
            {
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.Id))}"""] = Id,
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.Content))}"""] = Content,
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.Category))}"""] = Category,
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.SourcePage))}"""] = SourcePage,
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.SourceFile))}"""] = SourceFile,
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.Embedding))}"""] = Embedding,
            };
        }
        else if (CategoryEnum == VectorizeSearchCategory.Image)
        {
            return new SearchDocument
            {
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.Id))}"""] = Id,
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.Content))}"""] = Content,
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.Category))}"""] = Category,
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.SourceFile))}"""] = SourceFile,
                [$"""{this.GetJsonPropertyNameAttributeValue(nameof(VectorizeSearchEntity.ImageEmbedding))}"""] = ImageEmbedding,
            };
        }

        throw new ArgumentException($"Category: {CategoryEnum.ToString()} is not mapped");
    }

    // https://www.newtonsoft.com/json/help/html/ConditionalProperties.htm
    public bool ShouldSerializeScore()
    {
        return false;
    }

    public bool ShouldSerializeRerankerScore()
    {
        return false;
    }

    public bool ShouldSerializeSourcePage()
    {
        return CategoryEnum == VectorizeSearchCategory.Document;
    }

    public bool ShouldSerializeEmbedding()
    {
        return CategoryEnum == VectorizeSearchCategory.Document;
    }

    public bool ShouldSerializeImageEmbedding()
    {
        return CategoryEnum == VectorizeSearchCategory.Image;
    }

    #endregion Public Methods
}
