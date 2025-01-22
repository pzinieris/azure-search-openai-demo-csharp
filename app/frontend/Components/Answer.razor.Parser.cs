using Markdig.Extensions.AutoLinks;

namespace ClientApp.Components;

public sealed partial class Answer
{
    private readonly static MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .ConfigureNewLine("\n")
        .UseAdvancedExtensions()
        .UseAutoLinks(new AutoLinkOptions() { OpenInNewWindow = true })
        .UseEmojiAndSmiley()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    internal static HtmlParsedAnswer ParseAnswerToHtml(string answer, string citationBaseUrl)
    {
        var citations = new List<CitationDetails>();
        var followupQuestions = new HashSet<string>();

        var parsedAnswer = ReplacementRegex().Replace(answer, match =>
        {
            followupQuestions.Add(match.Value);
            return "";
        });

        parsedAnswer = parsedAnswer.Trim();

        var parts = SplitRegex().Split(parsedAnswer);

        var fragments = parts.Select((part, index) =>
        {
            if (index % 2 is 0)
            {
                return part;
            }
            else
            {
                var citationNumber = citations.Count + 1;
                var existingCitation = citations.FirstOrDefault(c => c.Name == part);
                if (existingCitation is not null)
                {
                    citationNumber = existingCitation.Number;
                }
                else
                {
                    var citation = new CitationDetails(part, citationBaseUrl, citationNumber);
                    citations.Add(citation);
                }
                // <MudChip T="string" Variant="Variant.Text" Color="Color.Info"
                //Icon = "@icon" OnClick = "@(_ => OnShowCitation(citation))" >
                //                        @($"{citation.Number}. {citation.Name}")
                //                    </ MudChip >
                //return $"""
                //    <MudChip T="string" Variant="Variant.Text" Color="Color.Info" OnClick="@(_ => OnShowCitation({citationNumber}))">{citationNumber}</MudChip>
                //    """;
                return $"""
                    <sup class="mud-chip mud-chip-text mud-chip-color-info rounded pa-1">{citationNumber}</sup>
                    """;
                //return $"""
                //    <sup>{citationNumber}</sup>
                //    """;
            }
        });

        var answerHtml = string.Join("", fragments);
        answerHtml = Markdown.ToHtml(answerHtml, s_pipeline);

        return new HtmlParsedAnswer(
            answerHtml,
            citations,
            followupQuestions.Select(f => f.Replace("<<", "").Replace(">>", ""))
                .ToHashSet());
    }

    [GeneratedRegex(@"<<([^>>]+)>>", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex ReplacementRegex();

    [GeneratedRegex(@"\[([^\]]+)\]", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex SplitRegex();
}

internal readonly record struct HtmlParsedAnswer(
    string AnswerHtml,
    List<CitationDetails> Citations,
    HashSet<string> FollowupQuestions);
