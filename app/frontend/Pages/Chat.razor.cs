//using ClientApp.Models;
//using Markdig.Extensions.AutoLinks;
//using Markdig.Syntax.Inlines;
//using Markdig.Syntax;
//using Markdig.Renderers.Html;

using Markdig.Extensions.AutoLinks;

namespace ClientApp.Pages;

public sealed partial class Chat
{
    /// Original implementation
    //#region Fields

    //private string _userQuestion = "";
    //private UserQuestion _currentQuestion;
    //private string _lastReferenceQuestion = "";
    //private bool _isReceivingResponse = false;

    //private readonly Dictionary<UserQuestion, ApproachResponse?> _questionAndAnswerMap = [];

    //#endregion Fields

    //#region Properties

    //[Inject] public required ISessionStorageService SessionStorage { get; set; }

    //[Inject] public required ApiClient ApiClient { get; set; }

    //[CascadingParameter(Name = nameof(Settings))]
    //public required RequestSettingsOverrides Settings { get; set; }

    //[CascadingParameter(Name = nameof(IsReversed))]
    //public required bool IsReversed { get; set; }

    //#endregion Properties

    //#region Private Methods

    //#region UI events

    //private Task OnAskQuestionAsync(string question)
    //{
    //    _userQuestion = question;
    //    return OnAskClickedAsync();
    //}

    //private async Task OnAskClickedAsync()
    //{
    //    if (string.IsNullOrWhiteSpace(_userQuestion))
    //    {
    //        return;
    //    }

    //    _isReceivingResponse = true;
    //    _lastReferenceQuestion = _userQuestion;
    //    _currentQuestion = new(_userQuestion, DateTime.Now);
    //    _questionAndAnswerMap[_currentQuestion] = null;

    //    try
    //    {
    //        var history = _questionAndAnswerMap
    //            .Where(x => x.Value is not null)
    //            .Select(x => new ChatTurn(x.Key.Question, x.Value!.Answer))
    //            .ToList();

    //        history.Add(new ChatTurn(_userQuestion));

    //        var request = new ChatRequest([.. history], Settings.Approach, Settings.Overrides);
    //        var result = await ApiClient.ChatConversationAsync(request);

    //        _questionAndAnswerMap[_currentQuestion] = result.Response;
    //        if (result.IsSuccessful)
    //        {
    //            _userQuestion = "";
    //            _currentQuestion = default;
    //        }
    //    }
    //    finally
    //    {
    //        _isReceivingResponse = false;
    //    }
    //}

    //private void OnClearChat()
    //{
    //    _userQuestion = _lastReferenceQuestion = "";
    //    _currentQuestion = default;
    //    _questionAndAnswerMap.Clear();
    //}

    //#endregion UI events

    //#endregion Private Methods


    /// New implementation, based on enqueue
    #region Fields

    private string _userQuestion = "";
    private UserQuestion _currentQuestion;
    private string _lastReferenceQuestion = "";
    private bool _isReceivingResponse = false;
    private string _citationBaseUrl = "";

    private readonly Dictionary<UserQuestion, ApproachResponse?> _questionAndAnswerMap = [];
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .ConfigureNewLine("\n")
        .UseAdvancedExtensions()
        .UseAutoLinks(new AutoLinkOptions() { OpenInNewWindow = true })
        .UseEmojiAndSmiley()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    #endregion Fields

    #region Properties

    [Inject] public required ISessionStorageService SessionStorage { get; set; }

    [Inject] public required OpenAIPromptQueue OpenAIPrompts { get; set; }

    [Inject] public required ApiClient ApiClient { get; set; }

    [Inject] public required ILogger<Chat> Logger { get; set; }

    [CascadingParameter(Name = nameof(Settings))]
    public required RequestSettingsOverrides Settings { get; set; }

    [CascadingParameter(Name = nameof(IsReversed))]
    public required bool IsReversed { get; set; }

    #endregion Properties

    #region LifeCycle events

    protected override async Task OnInitializedAsync()
    {
        // Retrieve and set the citationBaseUrl
        var citationBaseUrl = await GetCitationBaseUrlAsync();
        if (!string.IsNullOrWhiteSpace(citationBaseUrl))
        {
            _citationBaseUrl = citationBaseUrl;
        }

        await base.OnInitializedAsync();
    }

    #endregion LifeCycle events

    #region Private Methods

    #region UI events

    private void OnAskQuestionAsync(string question)
    {
        _userQuestion = question;

        OnAskClicked();
    }

    private void OnAskClicked()
    {
        if (_isReceivingResponse || string.IsNullOrWhiteSpace(_userQuestion))
        {
            return;
        }

        _isReceivingResponse = true;
        _lastReferenceQuestion = _userQuestion;
        _currentQuestion = new(_userQuestion, DateTime.Now);
        _questionAndAnswerMap[_currentQuestion] = null;

        var history = _questionAndAnswerMap
                .Where(x => x.Value is not null)
                .Select(x => new ChatTurn(x.Key.Question, x.Value!.Answer))
                .ToList();

        history.Add(new ChatTurn(_userQuestion));

        var request = new ChatRequest([.. history], Settings.Approach, Settings.Overrides);

        OpenAIPrompts.Enqueue(
            request,
            async (PromptResponse response) => await InvokeAsync(() =>
            {
                var (_, responseText, isComplete) = response;
                //var html = Markdown.ToHtml(responseText, _pipeline);

                var approachResponse = new ApproachResponse(responseText, null, null, null, _citationBaseUrl);
                _questionAndAnswerMap[_currentQuestion] = approachResponse;
                _isReceivingResponse = isComplete is false;

                if (isComplete)
                {
                    _userQuestion = "";
                    _currentQuestion = default;
                }

                StateHasChanged();
            }));
    }

    private void OnClearChat()
    {
        _userQuestion = _lastReferenceQuestion = "";
        _currentQuestion = default;
        _questionAndAnswerMap.Clear();
    }

    #endregion UI events

    private async ValueTask<string?> GetCitationBaseUrlAsync()
    {
        var sessionKey = "CitationBaseUrl";

        // Try to get the citationBaseUrl from the session storage
        var citationBaseUrl = SessionStorage.GetItem<string>(sessionKey);
        if (!string.IsNullOrWhiteSpace(citationBaseUrl))
        {
            return citationBaseUrl;
        }

        // Retrieve the citationBaseUrl from the API
        citationBaseUrl = await ApiClient.GetCitationBaseUrlAsync();

        if (string.IsNullOrWhiteSpace(citationBaseUrl))
        {
            Logger.LogError("CitationBaseUrl returned from API is null");

            return null;
        }

        // Store the citationBaseUrl into the session storage, before returning
        SessionStorage.SetItem<string>(sessionKey, citationBaseUrl);

        return citationBaseUrl;
    }

    #endregion Private Methods
}
