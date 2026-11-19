namespace AmiiTag.Core.Interfaces;

/// <summary>
/// Alerts, prompts and progress. Native dialogs on MAUI, Blazor overlays on the web.
/// </summary>
public interface IDialogService
{
    Task AlertAsync(string title, string? message = null, string ok = "OK");

    Task<bool> ConfirmAsync(string title, string? message = null, string accept = "Yes", string cancel = "No");

    /// <summary>
    /// Asks for text. Returns null if cancelled.
    /// </summary>
    Task<string?> PromptAsync(string title, string? message = null, string? initialValue = null, int maxLength = -1);

    /// <summary>
    /// Shows a list of choices (action sheet). Returns the chosen index, or -1 if cancelled.
    /// </summary>
    Task<int> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel");

    /// <summary>
    /// Shows a progress dialog until the returned object is disposed.
    /// </summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">Initial message.</param>
    /// <param name="cancellation">If set, a Cancel button is shown which cancels this source.</param>
    IProgressDialog ShowProgress(string title, string? message = null, CancellationTokenSource? cancellation = null);
}

public interface IProgressDialog : IAsyncDisposable
{
    /// <summary>
    /// Updates the text and, optionally, a 0-1 progress value.
    /// </summary>
    void Update(string? message, double? fraction = null);
}
