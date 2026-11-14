using AmiiTag.Core.Interfaces;
#if ANDROID || IOS
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;
#endif

namespace AmiiTag.Maui.Services;

#if ANDROID || IOS
/// <summary>
/// Scans QR codes with a native camera page (ZXing.Net.Maui).
/// </summary>
public sealed class CameraQrScanner : IQrScanner
{
    public bool IsSupported => true;

    public Task<bool> HasCameraAsync(CancellationToken cancellationToken = default)
    {
        try
        {
#if ANDROID
            var packages = Android.App.Application.Context.PackageManager;
            return Task.FromResult(packages?.HasSystemFeature(Android.Content.PM.PackageManager.FeatureCameraAny) == true);
#else
            // False on the simulator and on devices without a camera.
            return Task.FromResult(UIKit.UIImagePickerController.IsSourceTypeAvailable(UIKit.UIImagePickerControllerSourceType.Camera));
#endif
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        var status = await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.Camera>());
        if (status != PermissionStatus.Granted)
        {
            throw new InvalidOperationException("Camera permission was denied.");
        }

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = new QrScannerPage(completion);
        var sheet = MauiNavigator.CreateNavigationPage(page);

        await MainThread.InvokeOnMainThreadAsync(() => MauiNavigator.RootPage!.Navigation.PushModalAsync(sheet));
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            return await completion.Task;
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                page.Stop();
                if (MauiNavigator.RootPage is { } root && root.Navigation.ModalStack.Contains(sheet))
                {
                    await root.Navigation.PopModalAsync();
                }
            });
        }
    }

    private sealed class QrScannerPage : ContentPage
    {
        private readonly CameraBarcodeReaderView _reader;
        private readonly TaskCompletionSource<string?> _completion;

        public QrScannerPage(TaskCompletionSource<string?> completion)
        {
            _completion = completion;
            Title = "Scan QR Code";
            BackgroundColor = Colors.Black;

            _reader = new CameraBarcodeReaderView
            {
                Options = new BarcodeReaderOptions
                {
                    Formats = BarcodeFormat.QrCode,
                    AutoRotate = true,
                    Multiple = false,
                },
            };
            _reader.BarcodesDetected += OnBarcodesDetected;

            ToolbarItems.Add(new ToolbarItem
            {
                Text = "Cancel",
                Command = new Command(() => _completion.TrySetResult(null)),
            });

            Content = _reader;
        }

        public void Stop()
        {
            _reader.IsDetecting = false;
            _reader.BarcodesDetected -= OnBarcodesDetected;
        }

        private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
        {
            var value = e.Results?.FirstOrDefault()?.Value;
            if (!string.IsNullOrEmpty(value))
            {
                _completion.TrySetResult(value);
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // Swiping the sheet away counts as cancel.
            _completion.TrySetResult(null);
        }
    }
}
#else
public sealed class CameraQrScanner : IQrScanner
{
    public bool IsSupported => false;

    public Task<bool> HasCameraAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<string?> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}
#endif
