namespace Shared.Models;

public readonly record struct PageDetail(
    int FromPageNumber,
    int? ToPageNumber,
    int Offset,
    string Text);
