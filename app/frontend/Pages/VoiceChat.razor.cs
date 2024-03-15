using Markdig.Extensions.AutoLinks;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ClientApp.Pages;

public sealed partial class VoiceChat : IDisposable
{
    private string _userQuestion = "";
    private UserQuestion _currentQuestion;
    private bool _isRecognizingSpeech = false;
    private bool _isReceivingResponse = false;
    private bool _isReadingResponse = false;
    private IDisposable? _recognitionSubscription;
    private VoicePreferences? _voicePreferences;
    private readonly Dictionary<UserQuestion, string?> _questionAndAnswerMap = [];
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .ConfigureNewLine("\n")
        .UseAdvancedExtensions()
        .UseAutoLinks(new AutoLinkOptions() { OpenInNewWindow = true })
        .UseEmojiAndSmiley()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    [Inject] public required OpenAIPromptQueue OpenAIPrompts { get; set; }
    [Inject] public required IDialogService Dialog { get; set; }
    [Inject] public required ISpeechRecognitionService SpeechRecognition { get; set; }
    [Inject] public required ISpeechSynthesisService SpeechSynthesis { get; set; }
    [Inject] public required ILocalStorageService LocalStorage { get; set; }
    [Inject] public required IJSInProcessRuntime JavaScript { get; set; }
    [Inject] public required ILogger<VoiceChat> Logger { get; set; }

    [CascadingParameter(Name = nameof(IsReversed))]
    public required bool IsReversed { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await SpeechRecognition.InitializeModuleAsync();
        }
    }

    private void OnSendPrompt()
    {
        if (_isReceivingResponse || string.IsNullOrWhiteSpace(_userQuestion))
        {
            return;
        }

        _isReceivingResponse = true;
        _currentQuestion = new(_userQuestion, DateTime.Now);
        _questionAndAnswerMap[_currentQuestion] = null;

        OpenAIPrompts.Enqueue(
            _userQuestion,
            async (PromptResponse response) => await InvokeAsync(() =>
            {
                var (_, responseText, isComplete) = response;
                var html = Markdown.ToHtml(responseText, _pipeline);

                _questionAndAnswerMap[_currentQuestion] = html;
                _isReceivingResponse = isComplete is false;

                if (isComplete)
                {
                    // Change the voice state
                    _voicePreferences = new VoicePreferences(LocalStorage);
                    var (voice, rate, isEnabled) = _voicePreferences;
                    if (isEnabled)
                    {
                        _isReadingResponse = true;
                        var utterance = new SpeechSynthesisUtterance
                        {
                            Rate = rate,
                            Text = responseText
                        };
                        if (voice is not null)
                        {
                            utterance.Voice = new SpeechSynthesisVoice
                            {
                                Name = voice
                            };
                        }
                        SpeechSynthesis.Speak(utterance, duration =>
                        {
                            _isReadingResponse = false;
                            StateHasChanged();
                        });
                    }

                    // Making the links that are defined as [foo](url) or <url> to open in new tap
                    MarkdownDocument document = Markdown.Parse(responseText, _pipeline);

                    foreach (LinkInline link in document.Descendants<LinkInline>())
                    {
                        link.GetAttributes().AddPropertyIfNotExist("target", "_blank");
                    }

                    foreach (AutolinkInline link in document.Descendants<AutolinkInline>())
                    {
                        link.GetAttributes().AddPropertyIfNotExist("target", "_blank");
                    }

                    html = document.ToHtml(_pipeline);
                    _questionAndAnswerMap[_currentQuestion] = html;

                    _userQuestion = "";
                    _currentQuestion = default;
                }

                StateHasChanged();
            }));
    }

    private void OnKeyUp(KeyboardEventArgs args)
    {
        if (args is { Key: "Enter", ShiftKey: false })
        {
            OnSendPrompt();
        }
    }

    protected override void OnAfterRender(bool firstRender) => JavaScript.InvokeVoid("highlight");

    private void StopTalking()
    {
        SpeechSynthesis.Cancel();
        _isReadingResponse = false;
    }

    private void OnRecognizeSpeechClick()
    {
        if (_isRecognizingSpeech)
        {
            SpeechRecognition.CancelSpeechRecognition(false);
        }
        else
        {
            var bcp47Tag = CultureInfo.CurrentUICulture.Name;

            _recognitionSubscription?.Dispose();
            _recognitionSubscription = SpeechRecognition.RecognizeSpeech(
                bcp47Tag,
                OnRecognized,
                OnError,
                OnStarted,
                OnEnded);
        }
    }

    private async Task ShowVoiceDialogAsync()
    {
        var dialog = await Dialog.ShowAsync<VoiceDialog>(title: "🔊 Text-to-speech Preferences");
        var result = await dialog.Result;
        if (result is not { Canceled: true })
        {
            _voicePreferences = await dialog.GetReturnValueAsync<VoicePreferences>();
        }
    }

    private void OnStarted()
    {
        _isRecognizingSpeech = true;
        StateHasChanged();
    }

    private void OnEnded()
    {
        _isRecognizingSpeech = false;
        StateHasChanged();
    }

    private void OnError(SpeechRecognitionErrorEvent errorEvent)
    {
        Logger.LogWarning(
            "{Error}: {Message}", errorEvent.Error, errorEvent.Message);

        StateHasChanged();
    }

    private void OnRecognized(string transcript)
    {
        _userQuestion = _userQuestion switch
        {
            null => transcript,
            _ => $"{_userQuestion.Trim()} {transcript}".Trim()
        };

        StateHasChanged();
    }

    public void Dispose() => _recognitionSubscription?.Dispose();
}
