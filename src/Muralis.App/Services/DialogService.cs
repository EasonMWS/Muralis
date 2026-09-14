using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.Infrastructure;

namespace Muralis.App.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message, string closeButtonText = "OK");

    Task<bool> ShowConfirmAsync(string title, string message, string primaryButtonText, string closeButtonText = "Cancel");

    Task ShowErrorAsync(string title, string message);
}

public sealed class DialogService : IDialogService
{
    private readonly WindowContext _windowContext;
    private readonly ILogger<DialogService> _logger;

    public DialogService(WindowContext windowContext, ILogger<DialogService> logger)
    {
        _windowContext = windowContext;
        _logger = logger;
    }

    public Task ShowMessageAsync(string title, string message, string closeButtonText = "OK") =>
        ShowAsync(new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = closeButtonText,
        });

    public async Task<bool> ShowConfirmAsync(string title, string message, string primaryButtonText, string closeButtonText = "Cancel")
    {
        var result = await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Primary,
        });

        return result == ContentDialogResult.Primary;
    }

    public Task ShowErrorAsync(string title, string message) =>
        ShowMessageAsync(title, message, "Close");

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var xamlRoot = _windowContext.XamlRoot;
        if (xamlRoot is null)
        {
            _logger.LogWarning("Dialog '{Title}' was requested before the main window was ready", dialog.Title);
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = xamlRoot;
        return await dialog.ShowAsync();
    }
}
