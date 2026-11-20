using AmiiTag.Core.Interfaces;
using iOSPage = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page;
using iOSPresentation = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.UIModalPresentationStyle;

namespace AmiiTag.Maui.Services;

/// <summary>
/// Native alerts, action sheets and prompts, and the system's own progress dialog (<see cref="NativeProgressDialog"/>),
/// falling back to a small overlay page where there's none.
/// </summary>
public sealed class MauiDialogService : IDialogService
{
    // Progress dialogs on screen, newest last (main thread only).
    private static readonly List<NativeProgressHandle> ActiveProgress = [];

    private static Page RequirePage() => MauiNavigator.CurrentPage ?? throw new InvalidOperationException("No page is showing.");

    public Task AlertAsync(string title, string? message = null, string ok = "OK") =>
        MainThread.InvokeOnMainThreadAsync(() => AboveProgressAsync(() => RequirePage().DisplayAlertAsync(title, message, ok)));

    public Task<bool> ConfirmAsync(string title, string? message = null, string accept = "Yes", string cancel = "No") =>
        MainThread.InvokeOnMainThreadAsync(() => AboveProgressAsync(() => RequirePage().DisplayAlertAsync(title, message, accept, cancel)));

    public Task<string?> PromptAsync(string title, string? message = null, string? initialValue = null, int maxLength = -1) =>
        MainThread.InvokeOnMainThreadAsync(() => AboveProgressAsync<string?>(async () =>
            await RequirePage().DisplayPromptAsync(title, message ?? string.Empty, "OK", "Cancel", maxLength: maxLength, initialValue: initialValue ?? string.Empty)));

    public Task<int> ChooseAsync(string title, IReadOnlyList<string> options, string cancel = "Cancel") =>
        MainThread.InvokeOnMainThreadAsync(() => AboveProgressAsync(async () =>
        {
            var result = await RequirePage().DisplayActionSheetAsync(title, cancel, null, options.ToArray());
            if (result == null || result == cancel)
            {
                return -1;
            }

            for (var i = 0; i < options.Count; i++)
            {
                if (options[i] == result)
                {
                    return i;
                }
            }

            return -1;
        }));

    /// <summary>
    /// Shows another dialog while a progress dialog may be up. iOS, Mac and Windows show one system dialog at a time
    /// (Windows refuses a second ContentDialog; UIKit won't present over an alert reliably), so the progress dialog
    /// steps aside and comes back after. Android stacks dialogs, so there it stays.
    /// </summary>
    private static Task AboveProgressAsync(Func<Task> show) => AboveProgressAsync(async () => { await show(); return true; });

    private static async Task<T> AboveProgressAsync<T>(Func<Task<T>> show)
    {
#if IOS || MACCATALYST || WINDOWS
        var hidden = new List<NativeProgressHandle>();
        foreach (var handle in ActiveProgress.ToList())
        {
            if (await handle.HideForAnotherDialogAsync())
            {
                hidden.Add(handle);
            }
        }

        try
        {
            return await show();
        }
        finally
        {
            foreach (var handle in hidden)
            {
                await handle.ShowAgainAsync();
            }
        }
#else
        return await show();
#endif
    }

    public IProgressDialog ShowProgress(string title, string? message = null, CancellationTokenSource? cancellation = null)
    {
        if (NativeProgressDialog.Create(title, message, cancellation) is { } native)
        {
            var handle = new NativeProgressHandle(native);
            handle.Start();
            return handle;
        }

        var page = new ProgressPage(title, message, cancellation);
        var shown = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (MauiNavigator.RootPage is { } root)
            {
                await root.Navigation.PushModalAsync(page, false);
            }
        });

        return new ProgressHandle(page, shown);
    }

    /// <summary>
    /// A system progress dialog, shown and closed on the main thread in the order asked.
    /// </summary>
    private sealed class NativeProgressHandle : IProgressDialog
    {
        private readonly INativeProgressDialog _dialog;
        private Task _shown = Task.CompletedTask;
        private bool _disposed;

        public NativeProgressHandle(INativeProgressDialog dialog)
        {
            _dialog = dialog;
        }

        public void Start() => _shown = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            ActiveProgress.Add(this);
            await _dialog.ShowAsync();
        });

        public void Update(string? message, double? fraction = null) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_disposed)
                {
                    _dialog.Update(message, fraction);
                }
            });

        /// <summary>
        /// Closes it for a moment; true if it was showing.
        /// </summary>
        public async Task<bool> HideForAnotherDialogAsync()
        {
            await Settle(_shown);
            if (_disposed || !_dialog.IsShowing)
            {
                return false;
            }

            await _dialog.HideAsync();
            return true;
        }

        public Task ShowAgainAsync() => _disposed ? Task.CompletedTask : _dialog.ShowAsync();

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            await Settle(_shown);
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                ActiveProgress.Remove(this);
                await _dialog.HideAsync();
            });
        }

        private static async Task Settle(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }
    }

    private sealed class ProgressHandle : IProgressDialog
    {
        private readonly ProgressPage _page;
        private readonly Task _shown;
        private int _disposed;

        public ProgressHandle(ProgressPage page, Task shown)
        {
            _page = page;
            _shown = shown;
        }

        public void Update(string? message, double? fraction = null) =>
            MainThread.BeginInvokeOnMainThread(() => _page.Update(message, fraction));

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                await _shown;
            }
            catch
            {
            }

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (MauiNavigator.RootPage is not { } root)
                {
                    return;
                }

                // Usually on top; if something was presented above it, wait for that to go away first.
                for (var attempt = 0; attempt < 100 && root.Navigation.ModalStack.Contains(_page); attempt++)
                {
                    if (root.Navigation.ModalStack.LastOrDefault() == _page)
                    {
                        await root.Navigation.PopModalAsync(false);
                        return;
                    }

                    await Task.Delay(100);
                }
            });
        }
    }
}

/// <summary>
/// A translucent modal page with a small card, used as a progress "alert".
/// </summary>
internal sealed class ProgressPage : ContentPage
{
    private readonly Label _message;
    private readonly ProgressBar _progress;
    private readonly ActivityIndicator _spinner;

    public ProgressPage(string title, string? message, CancellationTokenSource? cancellation)
    {
        BackgroundColor = Color.FromRgba(0, 0, 0, 0.45);
        SetValue(iOSPage.ModalPresentationStyleProperty, iOSPresentation.OverFullScreen);
        NavigationPage.SetHasNavigationBar(this, false);

        _message = new Label
        {
            Text = message,
            HorizontalTextAlignment = TextAlignment.Center,
            FontSize = 15,
            IsVisible = !string.IsNullOrEmpty(message),
        };
        _message.SetAppThemeColor(Label.TextColorProperty, Colors.Black, Colors.White);

        _progress = new ProgressBar { IsVisible = false, ProgressColor = Color.FromArgb("#007AFF") };
        _spinner = new ActivityIndicator { IsRunning = true, Color = Color.FromArgb("#007AFF") };

        var titleLabel = new Label
        {
            Text = title,
            FontAttributes = FontAttributes.Bold,
            FontSize = 17,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        titleLabel.SetAppThemeColor(Label.TextColorProperty, Colors.Black, Colors.White);

        var stack = new VerticalStackLayout
        {
            Spacing = 10,
            Children = { titleLabel, _message, _spinner, _progress },
        };

        if (cancellation != null)
        {
            stack.Children.Add(new Button
            {
                Text = "Cancel",
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#007AFF"),
                Command = new Command(() => cancellation.Cancel()),
            });
        }

        var card = new Border
        {
            Padding = new Thickness(20, 18),
            StrokeThickness = 0,
            WidthRequest = 280,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Content = stack,
        };
        card.SetAppThemeColor(BackgroundColorProperty,
            Color.FromArgb("#F6F6FA"),
            Color.FromArgb("#1C1C1E"));

        Content = new Grid { Children = { card } };
    }

    // Progress dialogs can't be dismissed with the back button.
    protected override bool OnBackButtonPressed() => true;

    public void Update(string? message, double? fraction)
    {
        _message.Text = message;
        _message.IsVisible = !string.IsNullOrEmpty(message);
        if (fraction is double value)
        {
            _progress.IsVisible = true;
            _spinner.IsVisible = false;
            _progress.Progress = Math.Clamp(value, 0, 1);
        }
    }
}
