namespace Shared.Models;

public enum DocumentProcessingStatus
{
    NotProcessed,
    Succeeded,
    Failed,

    NotProcessed_ToBeDeleted,
    Hidden
};
