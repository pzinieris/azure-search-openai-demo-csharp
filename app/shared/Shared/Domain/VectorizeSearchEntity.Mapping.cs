using Azure.Search.Documents.Models;
using Shared.Extensions;

namespace Shared.Domain;
public partial class VectorizeSearchEntity
{
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

    public static string IdAsJsonPropertyName()
    {
        return nameof(VectorizeSearchEntity.Id).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity));
    }

    public static string ContentAsJsonPropertyName()
    {
        return nameof(VectorizeSearchEntity.Content).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity));
    }

    public static string CategoryAsJsonPropertyName()
    {
        return nameof(VectorizeSearchEntity.Category).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity));
    }

    public static string SourceFileAsJsonPropertyName()
    {
        return nameof(VectorizeSearchEntity.SourceFile).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity));
    }

    public static string SourcePageAsJsonPropertyName()
    {
        return nameof(VectorizeSearchEntity.SourcePage).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity));
    }

    public static string EmbeddingAsJsonPropertyName()
    {
        return nameof(VectorizeSearchEntity.Embedding).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity));
    }

    public static string ImageEmbeddingAsJsonPropertyName()
    {
        return nameof(VectorizeSearchEntity.ImageEmbedding).GetJsonPropertyNameAttributeValue(typeof(VectorizeSearchEntity));
    }
}
