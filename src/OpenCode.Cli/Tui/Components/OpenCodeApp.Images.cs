namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Images;
using OpenCode.Schema;
using OpenCode.Client;

public partial class OpenCodeApp
{
    [Parameter] public Func<LocationRef, CancellationToken, ImageSourceLoader>? CreateImageLoader { get; set; }
    private ImagePreviewController? _imagePreview;
    private ImageSourceLoader? _transcriptImageLoader;
    private (SessionHttpClient? Client, LocationRef Location)? _transcriptImageSource;

    private void ReadTranscriptImageSource()
    {
        if (!_sessionImagePreview || !_hasConversation || CreateImageLoader is null) return;
        var source = (ReadSessionClient?.Invoke(), SelectionLocation);
        if (_transcriptImageLoader is not null && _transcriptImageSource == source) return;
        // Host retains each loader until all mounted views are disposed; no per-row loader or file probe.
        _transcriptImageLoader = CreateImageLoader(source.Item2, _configurationLifetime.Token);
        _transcriptImageSource = source;
        _dirty = true;
    }
    private IReadOnlyList<ImagePreviewItem> PromptImages => (CapturePromptInput(_input).Files ?? [])
        .Where(file => file.Uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        .Select(file => new ImagePreviewItem(file.Uri, file.Mention?.Text)).ToArray();

    private Task OpenImagePreview(ImagePreviewRequest request)
    {
        if (request.Images.Count == 0 || CreateImageLoader is null) return Task.CompletedTask;
        var location = SelectionLocation;
        CloseDialog();
        _imagePreview = new(CreateImageLoader(location, _configurationLifetime.Token), request.Images, request.Initial);
        _dirty = true;
        return InvokeAsync(StateHasChanged);
    }
}
