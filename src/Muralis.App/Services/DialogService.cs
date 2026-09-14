using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.Infrastructure;

namespace Muralis.App.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message, string? closeButtonText = null);

    Task<bool> ShowConfirmAsync(string title, string message, string primaryButtonText, string? closeButtonText = null);

    Task ShowErrorAsync(string title, string message);
}

public sealed class DialogService : IDialogService
{
    private readonly WindowContext _windowContext;
    private readonly ILocalizationService _localization;
    private readonly ILogger<DialogService> _logger;

    public DialogService(WindowContext windowContext, ILocalizationService localization, ILogger<DialogService> logger)
    {
        _windowContext = windowContext;
        _localization = localization;
        _logger = logger;
    }

    public Task ShowMessageAsync(string title, string message, string? closeButtonText = null) =>
        ShowAsync(new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = closeButtonText ?? _localization.Get("Common_OK"),
        });

    public async Task<bool> ShowConfirmAsync(string title, string message, string primaryButtonText, string? closeButtonText = null)
    {
        var result = await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText ?? _localization.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        });

        return result == ContentDialogResult.Primary;
    }

    public Task ShowErrorAsync(string title, string message) =>
        ShowMessageAsync(title, message, _localization.Get("Common_Close"));

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
