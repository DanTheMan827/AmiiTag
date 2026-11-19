using AmiiTag.Core.Interfaces;

namespace AmiiTag.Core.Services.Web;

public enum DialogKind
{
    Alert,
    Confirm,
    Prompt,
    Choose,
    Progress,
}

public sealed class DialogModel
{
    internal DialogModel(DialogKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public DialogKind Kind { get; }
    public string Title { get; }
    public string? Message { get; set; }
    public double? Fraction { get; set; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public string Accept { get; init; } = "OK";
    public string? Cancel { get; init; }
    public string? InputValue { get; set; }
    public int MaxLength { get; init; } = -1;
    public CancellationTokenSource? Cancellation { get; init; }

    internal TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Dialogs rendered by <see cref="Components.DialogHost"/>. Used by the web host.
/// </summary>
public sealed class BlazorDialogService : IDialogService
{
    private readonly List<DialogModel> _dialogs = [];
    private readonly object _sync = new();

    public event Action? Changed;

    public IReadOnlyList<DialogModel> Dialogs
    {
        get
        {
            lock (_sync)
            {
                return _dialogs.ToList();
            }
        }
    }

    public async Task AlertAsync(string title, string? message = null, string ok = "OK")
    {
        await Show(new DialogModel(DialogKind.Alert, title) { Message = message, Accept = ok });
    }

    public async Task<bool> ConfirmAsync(string title, string? message = null, string accept = "Yes", string cancel = "No")
    {
        var result = await Show(new DialogModel(DialogKind.Confirm, title) { Message = message, Accept = accept, Cancel = cancel });
        return result is true;
    }

    public async Task<string?> PromptAsync(string title, string? message = null, string? initialValue = null, int maxLength = -1)
    {
        var result = await Show(new DialogModel(DialogKind.Prompt, title)
        {
            Message = message,
            InputValue = initialValue,
            MaxLength = maxLength,
            Accept = "OK",
            Cancel = "Cancel",
        });
        return result as string;
    }

    public async Task<int> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel")
    {
        var result = await Show(new DialogModel(DialogKind.Choose, title) { Options = options, Cancel = cancel });
        return result is int index ? index : -1;
    }

    public IProgressDialog ShowProgress(string title, string? message = null, CancellationTokenSource? cancellation = null)
    {
        var model = new DialogModel(DialogKind.Progress, title)
        {
            Message = message,
            Cancellation = cancellation,
            Cancel = cancellation != null ? "Cancel" : null,
        };
        _ = Show(model);
        return new ProgressHandle(this, model);
    }

    /// <summary>
    /// Called by the host component when a button is pressed.
    /// </summary>
    public void Complete(DialogModel model, object? result)
    {
        if (model.Kind == DialogKind.Progress)
        {
            model.Cancellation?.Cancel();
        }

        lock (_sync)
        {
            if (!_dialogs.Remove(model))
            {
                return;
            }
        }

        model.Completion.TrySetResult(result);
        Changed?.Invoke();
    }

    internal void NotifyChanged() => Changed?.Invoke();

    private Task<object?> Show(DialogModel model)
    {
        lock (_sync)
        {
            _dialogs.Add(model);
        }

        Changed?.Invoke();
        return model.Completion.Task;
    }

    private sealed class ProgressHandle : IProgressDialog
    {
        private readonly BlazorDialogService _owner;
        private readonly DialogModel _model;

        public ProgressHandle(BlazorDialogService owner, DialogModel model)
        {
            _owner = owner;
            _model = model;
        }

        public void Update(string? message, double? fraction = null)
        {
            _model.Message = message;
            _model.Fraction = fraction;
            _owner.NotifyChanged();
        }

        public ValueTask DisposeAsync()
        {
            lock (_owner._sync)
            {
                if (!_owner._dialogs.Remove(_model))
                {
                    return ValueTask.CompletedTask;
                }
            }

            _model.Completion.TrySetResult(null);
            _owner.NotifyChanged();
            return ValueTask.CompletedTask;
        }
    }
}
