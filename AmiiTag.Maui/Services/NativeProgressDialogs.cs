namespace AmiiTag.Maui.Services;

/// <summary>
/// A system progress dialog: an alert that can't be dismissed (iOS, Mac), an AlertDialog with a progress bar (Android),
/// or a ContentDialog with one (Windows). Every member runs on the main thread.
/// </summary>
internal interface INativeProgressDialog
{
    /// <summary>
    /// Shows it (again, after <see cref="HideAsync"/>), completing once it's on screen.
    /// </summary>
    Task ShowAsync();

    void Update(string? message, double? fraction);

    Task HideAsync();

    /// <summary>
    /// Whether it's on screen. False once the user cancelled it (which closes it).
    /// </summary>
    bool IsShowing { get; }
}

internal static class NativeProgressDialog
{
    /// <summary>
    /// The platform's dialog, or null where there's none (the app then uses its own overlay page).
    /// </summary>
    public static INativeProgressDialog? Create(string title, string? message, CancellationTokenSource? cancellation)
    {
#if IOS || MACCATALYST
        return new AppleProgressAlert(title, message, cancellation);
#elif ANDROID
        return new AndroidProgressDialog(title, message, cancellation);
#elif WINDOWS
        return new WindowsProgressDialog(title, message, cancellation);
#else
        return null;
#endif
    }

    /// <summary>
    /// The message with the progress as a percentage, for dialogs that can't hold a progress bar.
    /// </summary>
    public static string? WithPercentage(string? message, double? fraction) =>
        fraction is double value
            ? string.IsNullOrEmpty(message) ? $"{Math.Clamp(value, 0, 1):P0}" : $"{message}\n{Math.Clamp(value, 0, 1):P0}"
            : message;
}

#if IOS || MACCATALYST
/// <summary>
/// iOS and Mac: a system alert with no buttons (only Cancel, when the task can be cancelled). Once there's a fraction
/// it also gets a progress bar. UIKit doesn't support adding views to an alert (its view hierarchy is private), so the
/// bar is placed over blank lines kept at the end of the message, lined up with the message's label; if that label
/// can't be found (a future iOS laying alerts out differently), the bar is left out and the percentage in the message
/// still shows the progress.
/// </summary>
internal sealed class AppleProgressAlert : INativeProgressDialog
{
    // Room for the bar: the message ends with these blank lines, and the bar sits in them.
    private const string BarSpace = "\n\n";

    private readonly string _title;
    private readonly CancellationTokenSource? _cancellation;
    private string? _message;
    private double? _fraction;
    private UIKit.UIAlertController? _alert;
    private UIKit.UIProgressView? _bar;

    // The alert gets a window of its own above the app's, so navigation underneath can't take it along: presented from
    // a page that was then popped or replaced (connecting to a device, say), it stayed on screen, unresponsive, with
    // nothing left to dismiss it from.
    private UIKit.UIWindow? _window;
    private bool _barUnavailable;

    public AppleProgressAlert(string title, string? message, CancellationTokenSource? cancellation)
    {
        _title = title;
        _message = message;
        _cancellation = cancellation;
    }

    public bool IsShowing => _alert is { PresentingViewController: not null, IsBeingDismissed: false };

    public async Task ShowAsync()
    {
        if (IsShowing)
        {
            return;
        }

        var window = CreateWindow();
        if (window.RootViewController is not { } host)
        {
            return;
        }

        var alert = UIKit.UIAlertController.Create(_title, Text(), UIKit.UIAlertControllerStyle.Alert);
        if (_cancellation != null)
        {
            // UIKit closes the alert itself; its window goes too, or it would keep catching every touch.
            alert.AddAction(UIKit.UIAlertAction.Create("Cancel", UIKit.UIAlertActionStyle.Cancel, _ =>
            {
                _cancellation.Cancel();
                if (ReferenceEquals(_alert, alert))
                {
                    _alert = null;
                    _bar = null;
                    CloseWindow();
                }
            }));
        }

        _alert = alert;
        _bar = null;
        _window = window;
        window.Hidden = false;
        var shown = new TaskCompletionSource();
        host.PresentViewController(alert, true, () => shown.TrySetResult());
        await shown.Task;
        EnsureBar();
    }

    // A clear window at alert level in the app's scene, holding an empty controller to present the alert from.
    private static UIKit.UIWindow CreateWindow()
    {
        var scenes = UIKit.UIApplication.SharedApplication.ConnectedScenes.ToArray().OfType<UIKit.UIWindowScene>().ToList();
        var scene = scenes.FirstOrDefault(s => s.ActivationState == UIKit.UISceneActivationState.ForegroundActive) ?? scenes.FirstOrDefault();
        var window = scene != null ? new UIKit.UIWindow(scene) : new UIKit.UIWindow(UIKit.UIScreen.MainScreen.Bounds);
        window.WindowLevel = UIKit.UIWindowLevel.Alert + 1;
        window.BackgroundColor = UIKit.UIColor.Clear;
        window.RootViewController = new UIKit.UIViewController();
        window.RootViewController.View!.BackgroundColor = UIKit.UIColor.Clear;
        return window;
    }

    private void CloseWindow()
    {
        if (_window is { } window)
        {
            _window = null;
            window.Hidden = true;
            window.RootViewController = null;
        }
    }

    public void Update(string? message, double? fraction)
    {
        _message = message;
        _fraction = fraction ?? _fraction;
        if (_alert == null)
        {
            return;
        }

        _alert.Message = Text();
        EnsureBar();
        _bar?.SetProgress((float)Math.Clamp(_fraction ?? 0, 0, 1), true);
    }

    // The message, the percentage under it, and the space the bar sits in once there's a bar to show.
    private string? Text()
    {
        var text = NativeProgressDialog.WithPercentage(_message, _fraction);
        return _fraction != null && !_barUnavailable ? text + BarSpace : text;
    }

    /// <summary>
    /// Adds the bar once there's a fraction: pinned to the message label's sides, centred on the blank line at its end.
    /// </summary>
    private void EnsureBar()
    {
        if (_bar != null || _barUnavailable || _fraction == null || _alert is not { View: { } root } alert || !IsShowing)
        {
            return;
        }

        root.LayoutIfNeeded();
        if (FindLabel(root, alert.Message) is not { } label)
        {
            // Not laid out as expected: no bar, and no blank space left for one.
            _barUnavailable = true;
            alert.Message = Text();
            return;
        }

        var bar = new UIKit.UIProgressView(UIKit.UIProgressViewStyle.Default)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            Progress = (float)Math.Clamp(_fraction.Value, 0, 1),
        };
        root.AddSubview(bar);
        var lineHeight = label.Font?.LineHeight ?? 18;
        UIKit.NSLayoutConstraint.ActivateConstraints(
        [
            bar.LeadingAnchor.ConstraintEqualTo(label.LeadingAnchor),
            bar.TrailingAnchor.ConstraintEqualTo(label.TrailingAnchor),
            bar.CenterYAnchor.ConstraintEqualTo(label.BottomAnchor, -lineHeight),
        ]);
        _bar = bar;
    }

    // The alert's message label: the label whose text is the message (the hierarchy itself is private).
    private static UIKit.UILabel? FindLabel(UIKit.UIView view, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (view is UIKit.UILabel label && label.Text == text)
        {
            return label;
        }

        foreach (var child in view.Subviews)
        {
            if (FindLabel(child, text) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public async Task HideAsync()
    {
        var alert = _alert;
        _alert = null;
        _bar = null;
        if (alert is { PresentingViewController: { } presenter, IsBeingDismissed: false })
        {
            var hidden = new TaskCompletionSource();
            presenter.DismissViewController(true, () => hidden.TrySetResult());

            // Dismissal can't fail to end in practice, but closing the window below removes the alert regardless.
            await Task.WhenAny(hidden.Task, Task.Delay(1000));
        }

        CloseWindow();
    }
}
#endif

#if ANDROID
/// <summary>
/// Android: a Material AlertDialog holding the message and a progress bar (spinning until there's a fraction), which
/// the back button and taps outside don't dismiss.
/// </summary>
internal sealed class AndroidProgressDialog : INativeProgressDialog
{
    private readonly string _title;
    private readonly CancellationTokenSource? _cancellation;
    private string? _message;
    private double? _fraction;
    private AndroidX.AppCompat.App.AlertDialog? _dialog;
    private Android.Widget.TextView? _text;
    private Android.Widget.ProgressBar? _bar;
    private bool _cancelled;

    public AndroidProgressDialog(string title, string? message, CancellationTokenSource? cancellation)
    {
        _title = title;
        _message = message;
        _cancellation = cancellation;
    }

    public bool IsShowing => _dialog?.IsShowing == true;

    public Task ShowAsync()
    {
        if (IsShowing || _cancelled || Platform.CurrentActivity is not { IsFinishing: false } activity)
        {
            return Task.CompletedTask;
        }

        var density = activity.Resources?.DisplayMetrics?.Density ?? 1;
        int Dp(int value) => (int)(value * density);

        var layout = new Android.Widget.LinearLayout(activity) { Orientation = Android.Widget.Orientation.Vertical };
        layout.SetPadding(Dp(24), Dp(8), Dp(24), Dp(4));
        _text = new Android.Widget.TextView(activity);
        _bar = new Android.Widget.ProgressBar(activity, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
        layout.AddView(_text);
        layout.AddView(_bar, new Android.Widget.LinearLayout.LayoutParams(Android.Views.ViewGroup.LayoutParams.MatchParent, Android.Views.ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(12),
        });
        Apply();

        var builder = new AndroidX.AppCompat.App.AlertDialog.Builder(activity)
            .SetTitle(_title)!
            .SetView(layout)!
            .SetCancelable(false)!;
        if (_cancellation != null)
        {
            builder.SetNegativeButton("Cancel", (_, _) =>
            {
                _cancelled = true;
                _cancellation.Cancel();
            });
        }

        _dialog = builder.Create();
        _dialog.SetCanceledOnTouchOutside(false);
        _dialog.Show();
        return Task.CompletedTask;
    }

    public void Update(string? message, double? fraction)
    {
        _message = message;
        _fraction = fraction ?? _fraction;
        Apply();
    }

    private void Apply()
    {
        if (_text != null)
        {
            _text.Text = _message;
            _text.Visibility = string.IsNullOrEmpty(_message) ? Android.Views.ViewStates.Gone : Android.Views.ViewStates.Visible;
        }

        if (_bar != null)
        {
            _bar.Indeterminate = _fraction == null;
            if (_fraction is double value)
            {
                _bar.Progress = (int)(Math.Clamp(value, 0, 1) * 1000);
            }
        }
    }

    public Task HideAsync()
    {
        var dialog = _dialog;
        _dialog = null;
        _text = null;
        _bar = null;
        if (dialog?.IsShowing == true)
        {
            dialog.Dismiss();
        }

        return Task.CompletedTask;
    }
}
#endif

#if WINDOWS
/// <summary>
/// Windows: a ContentDialog with the message and a progress bar (indeterminate until there's a fraction). Escape and
/// clicks outside don't close it; only Cancel, when the task can be cancelled.
/// </summary>
internal sealed class WindowsProgressDialog : INativeProgressDialog
{
    private readonly string _title;
    private readonly CancellationTokenSource? _cancellation;
    private string? _message;
    private double? _fraction;
    private Microsoft.UI.Xaml.Controls.ContentDialog? _dialog;
    private Microsoft.UI.Xaml.Controls.TextBlock? _text;
    private Microsoft.UI.Xaml.Controls.ProgressBar? _bar;
    private bool _closing;
    private bool _cancelled;

    public WindowsProgressDialog(string title, string? message, CancellationTokenSource? cancellation)
    {
        _title = title;
        _message = message;
        _cancellation = cancellation;
    }

    public bool IsShowing => _dialog != null;

    public async Task ShowAsync()
    {
        if (_dialog != null || _cancelled ||
            MauiNavigator.CurrentPage?.Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window { Content: Microsoft.UI.Xaml.FrameworkElement root })
        {
            return;
        }

        _text = new Microsoft.UI.Xaml.Controls.TextBlock { TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
        _bar = new Microsoft.UI.Xaml.Controls.ProgressBar { Minimum = 0, Maximum = 1, Margin = new Microsoft.UI.Xaml.Thickness(0, 12, 0, 0) };
        Apply();

        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = _title,
            Content = new Microsoft.UI.Xaml.Controls.StackPanel { MinWidth = 280, Children = { _text, _bar } },
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            CloseButtonText = _cancellation != null ? "Cancel" : string.Empty,
        };

        // A dialog made in code doesn't get the WinUI look unless it's given the style.
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style) && style is Microsoft.UI.Xaml.Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        dialog.CloseButtonClick += (_, _) =>
        {
            _cancelled = true;
            _cancellation?.Cancel();
        };

        // Only Cancel or the app close it (Escape would otherwise).
        dialog.Closing += (_, e) =>
        {
            if (!_closing && !_cancelled)
            {
                e.Cancel = true;
            }
        };
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dialog, dialog))
            {
                _dialog = null;
            }
        };

        _dialog = dialog;
        _closing = false;

        // Returns once it's open: Hide() on a dialog that hasn't opened yet does nothing, and it would then open after
        // its task had finished and never close.
        var opened = new TaskCompletionSource();
        dialog.Opened += (_, _) => opened.TrySetResult();

        // Windows allows one ContentDialog at a time; one closing (an alert just answered) still counts for a moment.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _ = dialog.ShowAsync();
                await Task.WhenAny(opened.Task, Task.Delay(2000));
                return;
            }
            catch (Exception) when (attempt < 20)
            {
                await Task.Delay(50);
            }
            catch (Exception)
            {
                // Never shown: the task carries on without it.
                _dialog = null;
                return;
            }
        }
    }

    public void Update(string? message, double? fraction)
    {
        _message = message;
        _fraction = fraction ?? _fraction;
        Apply();
    }

    private void Apply()
    {
        if (_text != null)
        {
            _text.Text = _message ?? string.Empty;
            _text.Visibility = string.IsNullOrEmpty(_message) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        }

        if (_bar != null)
        {
            _bar.IsIndeterminate = _fraction == null;
            if (_fraction is double value)
            {
                _bar.Value = Math.Clamp(value, 0, 1);
            }
        }
    }

    /// <summary>
    /// Closes it, completing once it's gone (so another ContentDialog can open).
    /// </summary>
    public Task HideAsync()
    {
        var dialog = _dialog;
        _dialog = null;
        if (dialog == null)
        {
            return Task.CompletedTask;
        }

        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        _closing = true;
        dialog.Hide();

        // Closed doesn't come for a dialog that never opened.
        return Task.WhenAny(closed.Task, Task.Delay(1000));
    }
}
#endif
