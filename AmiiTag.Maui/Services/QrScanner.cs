using AmiiTag.Core.Interfaces;

namespace AmiiTag.Maui.Services;

#if IOS
/// <summary>
/// iOS: scans QR codes with AVFoundation (AVCaptureMetadataOutput), in a camera sheet with a Cancel button.
/// </summary>
public sealed class CameraQrScanner : IQrScanner
{
    public bool IsSupported => true;

    // False on the simulator and on devices without a camera.
    public Task<bool> HasCameraAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AVFoundation.AVCaptureDevice.GetDefaultDevice(AVFoundation.AVMediaTypes.Video) != null);

    public async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        var status = await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.Camera>());
        if (status != PermissionStatus.Granted)
        {
            throw new InvalidOperationException("AmiiTag doesn't have access to the camera. Allow it in Settings > AmiiTag.");
        }

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        UIKit.UINavigationController? sheet = null;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var scanner = new QrScannerViewController(completion);
            sheet = new UIKit.UINavigationController(scanner) { ModalPresentationStyle = UIKit.UIModalPresentationStyle.PageSheet };

            // Swiping the sheet down counts as Cancel.
            if (sheet.PresentationController != null)
            {
                sheet.PresentationController.Delegate = new DismissDelegate(() => completion.TrySetResult(null));
            }

            if (Platform.GetCurrentUIViewController() is { } top)
            {
                top.PresentViewController(sheet, true, null);
            }
            else
            {
                completion.TrySetResult(null);
            }
        });

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            return await completion.Task;
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (sheet is { PresentingViewController: not null, IsBeingDismissed: false })
                {
                    sheet.DismissViewController(true, null);
                }
            });
        }
    }

    private sealed class DismissDelegate(Action dismissed) : UIKit.UIAdaptivePresentationControllerDelegate
    {
        public override void DidDismiss(UIKit.UIPresentationController presentationController) => dismissed();
    }

    /// <summary>
    /// The camera preview, reporting the first QR code it sees.
    /// </summary>
    private sealed class QrScannerViewController : UIKit.UIViewController
    {
        private readonly TaskCompletionSource<string?> _completion;
        private readonly CoreFoundation.DispatchQueue _sessionQueue = new("amiitag.qr.session");
        private AVFoundation.AVCaptureSession? _session;
        private AVFoundation.AVCaptureVideoPreviewLayer? _preview;
        private MetadataDelegate? _delegate;

        public QrScannerViewController(TaskCompletionSource<string?> completion)
        {
            _completion = completion;
            Title = "Scan QR Code";
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            View!.BackgroundColor = UIKit.UIColor.Black;
            NavigationItem.LeftBarButtonItem = new UIKit.UIBarButtonItem(UIKit.UIBarButtonSystemItem.Cancel, (_, _) => _completion.TrySetResult(null));

            var device = AVFoundation.AVCaptureDevice.GetDefaultDevice(AVFoundation.AVMediaTypes.Video);
            var input = device == null ? null : AVFoundation.AVCaptureDeviceInput.FromDevice(device, out _);
            if (input == null)
            {
                _completion.TrySetException(new InvalidOperationException("The camera couldn't be opened."));
                return;
            }

            var session = new AVFoundation.AVCaptureSession();
            var output = new AVFoundation.AVCaptureMetadataOutput();
            if (!session.CanAddInput(input) || !session.CanAddOutput(output))
            {
                _completion.TrySetException(new InvalidOperationException("The camera couldn't be used for scanning."));
                return;
            }

            session.AddInput(input);
            session.AddOutput(output);
            _delegate = new MetadataDelegate(value =>
            {
                if (_completion.TrySetResult(value))
                {
                    new UIKit.UINotificationFeedbackGenerator().NotificationOccurred(UIKit.UINotificationFeedbackType.Success);
                }
            });
            output.SetDelegate(_delegate, CoreFoundation.DispatchQueue.MainQueue);

            // Only after the output is in the session are its available types known.
            output.MetadataObjectTypes = AVFoundation.AVMetadataObjectType.QRCode;

            _preview = new AVFoundation.AVCaptureVideoPreviewLayer(session) { VideoGravity = AVFoundation.AVLayerVideoGravity.ResizeAspectFill };
            View.Layer.AddSublayer(_preview);
            _session = session;
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            if (_preview != null)
            {
                _preview.Frame = View!.Bounds;
            }
        }

        public override void ViewWillAppear(bool animated)
        {
            base.ViewWillAppear(animated);

            // startRunning blocks while the camera starts, so not on the main thread.
            var session = _session;
            _sessionQueue.DispatchAsync(() => session?.StartRunning());
        }

        public override void ViewWillDisappear(bool animated)
        {
            base.ViewWillDisappear(animated);
            var session = _session;
            _sessionQueue.DispatchAsync(() => session?.StopRunning());
        }
    }

    private sealed class MetadataDelegate(Action<string> found) : AVFoundation.AVCaptureMetadataOutputObjectsDelegate
    {
        public override void DidOutputMetadataObjects(AVFoundation.AVCaptureMetadataOutput captureOutput, AVFoundation.AVMetadataObject[] metadataObjects,
            AVFoundation.AVCaptureConnection connection)
        {
            foreach (var item in metadataObjects)
            {
                if (item is AVFoundation.AVMetadataMachineReadableCodeObject { StringValue: { Length: > 0 } value })
                {
                    found(value);
                    return;
                }
            }
        }
    }
}
#elif ANDROID
/// <summary>
/// Android: Google's code scanner (Play Services) where it's available: its own camera screen, without the app needing
/// camera permission. Without Play Services, or if its scanner can't be used, ZXing in a camera page instead.
/// </summary>
public sealed class CameraQrScanner : IQrScanner
{
    public bool IsSupported => true;

    public Task<bool> HasCameraAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var packages = Android.App.Application.Context.PackageManager;
            return Task.FromResult(packages?.HasSystemFeature(Android.Content.PM.PackageManager.FeatureCameraAny) == true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (HasPlayServices())
        {
            try
            {
                return await ScanWithGoogleAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The scanner module couldn't be downloaded or started (no network on first use, a restricted
                // device...): ZXing does the job instead.
                System.Diagnostics.Debug.WriteLine($"QR: Google code scanner failed, using ZXing: {ex.Message}");
            }
        }

        return await ZXingQrScanner.ScanAsync(cancellationToken);
    }

    private static bool HasPlayServices()
    {
        try
        {
            return Android.Gms.Common.GoogleApiAvailability.Instance.IsGooglePlayServicesAvailable(Android.App.Application.Context) ==
                Android.Gms.Common.ConnectionResult.Success;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> ScanWithGoogleAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("The app isn't in the foreground.");
            var options = new Xamarin.Google.MLKit.Vision.CodeScanner.GmsBarcodeScannerOptions.Builder()
                .SetBarcodeFormats(Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode.FormatQrCode)
                .Build();
            var scanner = Xamarin.Google.MLKit.Vision.CodeScanner.GmsBarcodeScanning.GetClient(activity, options);
            scanner.StartScan().AddOnCompleteListener(new CompleteListener(completion));
        });

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    private sealed class CompleteListener(TaskCompletionSource<string?> completion) : Java.Lang.Object, Android.Gms.Tasks.IOnCompleteListener
    {
        public void OnComplete(Android.Gms.Tasks.Task task)
        {
            if (task.IsCanceled)
            {
                // Back out of Google's scanner.
                completion.TrySetResult(null);
            }
            else if (task.IsSuccessful)
            {
                completion.TrySetResult(Android.Runtime.Extensions.JavaCast<Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode>(task.Result)?.RawValue);
            }
            else
            {
                // For example "Waiting for the Barcode UI module to be downloaded" on first use.
                completion.TrySetException(task.Exception ?? new Java.Lang.Exception("The code scanner failed."));
            }
        }
    }
}

/// <summary>
/// Android without Google's scanner: ZXing.Net.Maui's camera view in a sheet.
/// </summary>
internal static class ZXingQrScanner
{
    public static async Task<string?> ScanAsync(CancellationToken cancellationToken)
    {
        var status = await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.Camera>());
        if (status != PermissionStatus.Granted)
        {
            throw new InvalidOperationException("AmiiTag doesn't have access to the camera. Allow it in the app's settings.");
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
        private readonly ZXing.Net.Maui.Controls.CameraBarcodeReaderView _reader;
        private readonly TaskCompletionSource<string?> _completion;

        public QrScannerPage(TaskCompletionSource<string?> completion)
        {
            _completion = completion;
            Title = "Scan QR Code";
            BackgroundColor = Colors.Black;

            _reader = new ZXing.Net.Maui.Controls.CameraBarcodeReaderView
            {
                Options = new ZXing.Net.Maui.BarcodeReaderOptions
                {
                    Formats = ZXing.Net.Maui.BarcodeFormat.QrCode,
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

        private void OnBarcodesDetected(object? sender, ZXing.Net.Maui.BarcodeDetectionEventArgs e)
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

            // Leaving the page (back) counts as cancel.
            _completion.TrySetResult(null);
        }
    }
}
#elif WINDOWS
/// <summary>
/// Windows: the webcam through MediaCapture, previewed in a dialog, with each frame decoded by ZXing.Net.
/// </summary>
public sealed class CameraQrScanner : IQrScanner
{
    public bool IsSupported => true;

    public async Task<bool> HasCameraAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cameras = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture);
            System.Diagnostics.Debug.WriteLine($"QR: {cameras.Count} video capture devices: {string.Join(", ", cameras.Select(c => c.Name))}");
            if (cameras.Count > 0)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"QR: video capture device lookup failed: {ex}");
        }

        try
        {
            // The camera frame sources the scanner itself uses (also finds cameras listed under other device classes).
            var groups = await Windows.Media.Capture.Frames.MediaFrameSourceGroup.FindAllAsync();
            var color = groups.Count(g => g.SourceInfos.Any(i => i.SourceKind == Windows.Media.Capture.Frames.MediaFrameSourceKind.Color));
            System.Diagnostics.Debug.WriteLine($"QR: {groups.Count} frame source groups, {color} with color.");
            return color > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"QR: frame source lookup failed: {ex}");
            return false;
        }
    }

    public Task<string?> ScanAsync(CancellationToken cancellationToken = default) =>
        MainThread.InvokeOnMainThreadAsync(() => ScanOnMainThreadAsync(cancellationToken));

    private static async Task<string?> ScanOnMainThreadAsync(CancellationToken cancellationToken)
    {
        if (MauiNavigator.CurrentPage?.Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window { Content: Microsoft.UI.Xaml.FrameworkElement root })
        {
            return null;
        }

        var capture = new Windows.Media.Capture.MediaCapture();
        try
        {
            await capture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Video,
                MemoryPreference = Windows.Media.Capture.MediaCaptureMemoryPreference.Cpu,
                SharingMode = Windows.Media.Capture.MediaCaptureSharingMode.ExclusiveControl,
            });
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw new InvalidOperationException("AmiiTag can't use the camera. In Settings > Privacy & security > Camera, allow desktop apps to access the camera.");
        }
        catch (Exception ex)
        {
            capture.Dispose();
            throw new InvalidOperationException($"The camera couldn't be opened: {ex.Message}");
        }

        var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == Windows.Media.Capture.Frames.MediaFrameSourceKind.Color &&
                s.Info.MediaStreamType == Windows.Media.Capture.MediaStreamType.VideoPreview)
            ?? capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == Windows.Media.Capture.Frames.MediaFrameSourceKind.Color);
        if (source == null)
        {
            capture.Dispose();
            throw new InvalidOperationException("No color camera was found.");
        }

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = await capture.CreateFrameReaderAsync(source, Windows.Media.MediaProperties.MediaEncodingSubtypes.Bgra8);
        var decoder = new ZXing.BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = { PossibleFormats = [ZXing.BarcodeFormat.QR_CODE], TryHarder = true },
        };
        var decoding = 0;
        reader.FrameArrived += (frameReader, _) =>
        {
            // One frame at a time; frames that arrive meanwhile are dropped.
            if (completion.Task.IsCompleted || Interlocked.Exchange(ref decoding, 1) == 1)
            {
                return;
            }

            try
            {
                using var frame = frameReader.TryAcquireLatestFrame();
                if (frame?.VideoMediaFrame?.SoftwareBitmap is not { } bitmap)
                {
                    return;
                }

                using var bgra = bitmap.BitmapPixelFormat == Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8
                    ? Windows.Graphics.Imaging.SoftwareBitmap.Copy(bitmap)
                    : Windows.Graphics.Imaging.SoftwareBitmap.Convert(bitmap, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8);
                var buffer = new Windows.Storage.Streams.Buffer((uint)(bgra.PixelWidth * bgra.PixelHeight * 4));
                bgra.CopyToBuffer(buffer);
                var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buffer);
                var luminance = new ZXing.RGBLuminanceSource(pixels, bgra.PixelWidth, bgra.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
                if (decoder.Decode(luminance)?.Text is { Length: > 0 } text)
                {
                    completion.TrySetResult(text);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"QR: frame decode failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref decoding, 0);
            }
        };

        var preview = new Microsoft.UI.Xaml.Controls.MediaPlayerElement
        {
            Source = Windows.Media.Core.MediaSource.CreateFromMediaFrameSource(source),
            AutoPlay = true,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            Width = 480,
            Height = 360,
        };
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Scan QR Code",
            Content = preview,
            CloseButtonText = "Cancel",
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
        };
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style) && style is Microsoft.UI.Xaml.Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        // Closing the dialog (Cancel, Escape) is a cancel; a result closes it.
        dialog.Closed += (_, _) => completion.TrySetResult(null);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            await reader.StartAsync();
            _ = dialog.ShowAsync();
            return await completion.Task;
        }
        finally
        {
            dialog.Hide();
            try
            {
                await reader.StopAsync();
            }
            catch
            {
            }

            reader.Dispose();
            preview.Source = null;
            capture.Dispose();
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
