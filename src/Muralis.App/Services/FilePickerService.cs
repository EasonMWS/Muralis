using Microsoft.Extensions.Logging;
using Muralis.App.Infrastructure;
using Windows.Storage.Pickers;

namespace Muralis.App.Services;

public interface IFilePickerService
{
    Task<string?> PickImageFileAsync();

    Task<IReadOnlyList<string>> PickImageFilesAsync();

    Task<string?> PickFolderAsync();
}

/// <summary>
/// Wraps WinRT pickers so they can be used from an unpackaged desktop app
/// (pickers require an owner window handle).
/// </summary>
public sealed class FilePickerService : IFilePickerService
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".avif"];

    private readonly WindowContext _windowContext;
    private readonly ILogger<FilePickerService> _logger;

    public FilePickerService(WindowContext windowContext, ILogger<FilePickerService> logger)
    {
        _windowContext = windowContext;
        _logger = logger;
    }

    public async Task<string?> PickImageFileAsync()
    {
        var files = await PickImageFilesAsync().ConfigureAwait(true);
        return files.Count > 0 ? files[0] : null;
    }

    public async Task<IReadOnlyList<string>> PickImageFilesAsync()
    {
        if (!EnsureWindow("file picker"))
        {
            return [];
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };

            foreach (var extension in ImageExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowContext.Handle);
            _logger.LogDebug("Showing the file picker dialog (owner hwnd: {Handle})", _windowContext.Handle);
            var files = await picker.PickMultipleFilesAsync().AsTask().ConfigureAwait(true);
            _logger.LogDebug("File picker dialog closed");
            return files?.Select(file => file.Path).Where(path => !string.IsNullOrEmpty(path)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The file picker failed");
            return [];
        }
    }

    public async Task<string?> PickFolderAsync()
    {
        if (!EnsureWindow("folder picker"))
        {
            return null;
        }

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add("*");

            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowContext.Handle);
            var folder = await picker.PickSingleFolderAsync().AsTask().ConfigureAwait(true);
            return string.IsNullOrEmpty(folder?.Path) ? null : folder.Path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The folder picker failed");
            return null;
        }
    }

    private bool EnsureWindow(string dialogName)
    {
        if (_windowContext.IsReady && _windowContext.Handle != 0)
        {
            return true;
        }

        _logger.LogWarning("Cannot show the {Dialog} before the main window is ready", dialogName);
        return false;
    }
}
