using System.Globalization;
using System.Text.Json;
using Android.Content;
using Android.Provider;
using Android.Views;
using Microsoft.Maui.ApplicationModel;
using DragEvent = Android.Views.DragEvent;
using View = Android.Views.View;
using WebView = Android.Webkit.WebView;

namespace AmiiTag.Maui;

/// <summary>
/// Files dragged from another app (split screen, freeform windows, DeX, ChromeOS) onto the app. Android's WebView
/// doesn't hand those to the page, so they're caught here and passed to the page's drop zones (drop-zone.js,
/// window.amiitagNativeDrop) with the position, so the zone under the finger highlights and takes them.
/// </summary>
internal sealed class WebViewDropForwarder : Java.Lang.Object, View.IOnDragListener
{
    private const long MaxBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebView _webView;

    private WebViewDropForwarder(WebView webView)
    {
        _webView = webView;
    }

    public static void Attach(WebView webView) => webView.SetOnDragListener(new WebViewDropForwarder(webView));

    public bool OnDrag(View? view, DragEvent? e)
    {
        if (e == null)
        {
            return false;
        }

        switch (e.Action)
        {
            case DragAction.Started:
                // What's being dragged is only known on drop; anything with a type might be files.
                return e.ClipDescription is { MimeTypeCount: > 0 };

            case DragAction.Entered:
            case DragAction.Location:
                Evaluate($"window.amiitagNativeDrop&&window.amiitagNativeDrop.over({Css(e.GetX())},{Css(e.GetY())})");
                return true;

            case DragAction.Exited:
            case DragAction.Ended:
                Evaluate("window.amiitagNativeDrop&&window.amiitagNativeDrop.leave()");
                return true;

            case DragAction.Drop:
                return Drop(e);

            default:
                return false;
        }
    }

    private bool Drop(DragEvent e)
    {
        var clip = e.ClipData;
        if (clip == null || clip.ItemCount == 0)
        {
            return false;
        }

        // Access to another app's files lasts while these permissions are held.
        var permissions = Platform.CurrentActivity?.RequestDragAndDropPermissions(e);
        var resolver = _webView.Context?.ContentResolver;
        var uris = Enumerable.Range(0, clip.ItemCount).Select(i => clip.GetItemAt(i)?.Uri).OfType<Android.Net.Uri>().ToList();
        var x = Css(e.GetX());
        var y = Css(e.GetY());
        if (resolver == null || uris.Count == 0)
        {
            permissions?.Release();
            return false;
        }

        _ = Task.Run(() =>
        {
            var files = new List<DroppedFile>();
            try
            {
                foreach (var uri in uris)
                {
                    files.Add(Read(resolver, uri));
                }
            }
            finally
            {
                permissions?.Release();
            }

            var json = JsonSerializer.Serialize(files, JsonOptions);
            MainThread.BeginInvokeOnMainThread(() =>
                Evaluate($"window.amiitagNativeDrop&&window.amiitagNativeDrop.drop({x},{y},{json})"));
        });

        return true;
    }

    private static DroppedFile Read(ContentResolver resolver, Android.Net.Uri uri)
    {
        var name = DisplayName(resolver, uri) ?? uri.LastPathSegment ?? "file";
        try
        {
            using var input = resolver.OpenInputStream(uri);
            if (input == null)
            {
                return new DroppedFile(name, null, "not a file");
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return new DroppedFile(name, null, "too large");
                }

                buffer.Write(chunk, 0, read);
            }

            return new DroppedFile(name, Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length), null);
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException or UnauthorizedAccessException)
        {
            return new DroppedFile(name, null, "not a file");
        }
    }

    private static string? DisplayName(ContentResolver resolver, Android.Net.Uri uri)
    {
        try
        {
            using var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                return index >= 0 ? cursor.GetString(index) : null;
            }
        }
        catch (Java.Lang.Exception)
        {
        }

        return null;
    }

    /// <summary>
    /// A view position in device pixels as CSS pixels (the page is laid out at the display's density).
    /// </summary>
    private string Css(float px)
    {
        var density = _webView.Resources?.DisplayMetrics?.Density ?? 1f;
        return (px / (density > 0 ? density : 1f)).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void Evaluate(string script) => _webView.EvaluateJavascript(script, null);

    private sealed record DroppedFile(string Name, string? Base64, string? Problem);
}
