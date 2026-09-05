namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Images;
using OpenCode.Schema;

public partial class OpenCodeApp
{
    [Parameter] public Func<LocationRef, CancellationToken, ImageSourceLoader>? CreateImageLoader { get; set; }
    private ImagePreviewController? _imagePreview;
    private IReadOnlyList<ImagePreviewItem> PromptImages => (CapturePromptInput(_input).Files ?? [])
        .Where(file => file.Uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        .Select(file => new ImagePreviewItem(file.Uri, file.Mention?.Text)).ToArray();

    private Task OpenImagePreview(ImagePreviewRequest request)
    {
        if (request.Images.Count == 0 || CreateImageLoader is null) return Task.CompletedTask;
        var location = _presentation?.Location ?? new LocationRef(CurrentDirectory);
        CloseDialog();
        _imagePreview = new(CreateImageLoader(location, _configurationLifetime.Token), request.Images, request.Initial);
        _dirty = true;
        return InvokeAsync(StateHasChanged);
    }
}
