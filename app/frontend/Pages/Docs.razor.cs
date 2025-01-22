namespace ClientApp.Pages;

public sealed partial class Docs : IDisposable
{
    private const long MaxIndividualFileSize = 1_024L * 1_024;

    private MudForm _form = null!;
    private MudFileUpload<IReadOnlyList<IBrowserFile>> _fileUpload = null!;
    private ICollection<IBrowserFile> _filesToUpload = null!;
    private Task _getDocumentsTask = null!;
    private bool _isLoadingDocuments = false;
    private bool _isUploadingDocuments = false;
    private string _filter = "";

    // Store a cancelation token that will be used to cancel if the user disposes of this component.
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly HashSet<DocumentResponse> _documents = [];

    [Inject]
    public required ApiClient Client { get; set; }

    [Inject]
    public required IDialogService Dialog { get; set; }

    [Inject]
    public required ISnackbar Snackbar { get; set; }

    [Inject]
    public required ILogger<Docs> Logger { get; set; }

    [Inject]
    public required IJSRuntime JSRuntime { get; set; }

    private bool FilesSelected => _filesToUpload is { Count: > 0 };

    protected override void OnInitialized() =>
        // Instead of awaiting this async enumerable here, let's capture it in a task
        // and start it in the background. This way, we can await it in the UI.
        _getDocumentsTask = GetDocumentsAsync();

    private bool OnFilter(DocumentResponse document) => document is not null
        && (string.IsNullOrWhiteSpace(_filter) || document.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    private async Task GetDocumentsAsync()
    {
        _isLoadingDocuments = true;

        try
        {
            var documents =
                await Client.GetDocumentsAsync(_cancellationTokenSource.Token)
                    .ToListAsync();

            foreach (var document in documents)
            {
                _documents.Add(document);
            }
        }
        finally
        {
            _isLoadingDocuments = false;
            StateHasChanged();
        }
    }

    private void MudFileUploadFilesChanged(IReadOnlyList<IBrowserFile> files)
    {
        if (files == null || !files.Any() || _filesToUpload == null)
        {
            _filesToUpload = files?.ToList() ?? new List<IBrowserFile>();
        }
        else
        {
            for (var x = 0; x < files.Count; x++)
            {
                _filesToUpload.Add(files[x]);
            }
        }
    }

    private async Task SubmitFilesForUploadAsync()
    {
        if (!FilesSelected)
        {
            return;
        }

        _isUploadingDocuments = true;

        var cookie = await JSRuntime.InvokeAsync<string>("getCookie", "XSRF-TOKEN");

        var result = await Client.UploadDocumentsAsync(
            _filesToUpload, MaxIndividualFileSize, cookie);

        Logger.LogInformation("Result: {x}", result);
        _isUploadingDocuments = false;

        if (result.IsSuccessful)
        {
            Snackbar.Add(
                $"Uploaded {result.UploadedFiles.Length} documents.",
                Severity.Success,
                static options =>
                {
                    options.ShowCloseIcon = true;
                    options.VisibleStateDuration = 10_000;
                });

            await _fileUpload.ResetAsync();

            // Update the documents list
            await GetDocumentsAsync();
        }
        else
        {
            Snackbar.Add(
                result.Error,
                Severity.Error,
                static options =>
                {
                    options.ShowCloseIcon = true;
                    options.VisibleStateDuration = 10_000;
                });
        }
    }

    private void OnShowDocument(DocumentResponse document)
    {
        var extension = Path.GetExtension(document.Name);
        if (extension is ".pdf")
        {
            Dialog.Show<PdfViewerDialog>(
            $"📄 {document.Name}",
            new DialogParameters
            {
                [nameof(PdfViewerDialog.FileName)] = document.Name,
                [nameof(PdfViewerDialog.BaseUrl)] =
                    document.Url.ToString().Replace($"/{document.Name}", ""),
            },
            new DialogOptions
            {
                MaxWidth = MaxWidth.Large,
                FullWidth = true,
                CloseButton = true,
                CloseOnEscapeKey = true
            });
        }
        else if (extension is ".png" or ".jpg" or ".jpeg")
        {
            Dialog.Show<ImageViewerDialog>(
            $"📄 {document.Name}",
            new DialogParameters
            {
                [nameof(ImageViewerDialog.FileName)] = document.Name,
                [nameof(ImageViewerDialog.Src)] = document.Url.ToString(),
            },
            new DialogOptions
            {
                MaxWidth = MaxWidth.Large,
                FullWidth = true,
                CloseButton = true,
                CloseOnEscapeKey = true
            });
        }
        else
        {
            Snackbar.Add(
                $"Unsupported file type: '{extension}'",
                Severity.Error,
                static options =>
                {
                    options.ShowCloseIcon = true;
                    options.VisibleStateDuration = 10_000;
                });
        }
    }

    public void Dispose() => _cancellationTokenSource.Cancel();
}
