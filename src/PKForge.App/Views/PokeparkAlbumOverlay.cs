using PKForge.App.Services;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>The photo album over the park: one photo at a time with its caption and date; L and R
/// (or a tap on either side) turn the pages, X (or the Share button) sends the photo to another
/// app, B (or a tap in the middle) closes it.</summary>
public sealed class PokeparkAlbumOverlay : IPadHandler
{
    private readonly Grid _host;
    private readonly SKCanvasView _view = new() { EnableTouchEvents = true };
    private readonly GamepadRouter? _router;
    private readonly TaskCompletionSource _done = new();
    private readonly IReadOnlyList<PokeparkAlbum.Photo> _photos;
    private int _index;
    private SKBitmap? _shown;
    private int _shownIndex = -1;
    private bool _closed, _sharing;
    private SKRect _shareButton;

    public static Task ShowAsync(Grid host) => new PokeparkAlbumOverlay(host)._done.Task;

    private PokeparkAlbumOverlay(Grid host)
    {
        _host = host;
        _photos = PokeparkAlbum.List();
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _view.BackgroundColor = Colors.Transparent;
        _view.PaintSurface += Paint;
        _view.Touch += Touch;
        host.Add(_view);
        Grid.SetRowSpan(_view, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_view, Math.Max(1, host.ColumnDefinitions.Count));
        App.Suspended += Suspended;
        _router?.Push(this);
    }

    // The share sheet sends the app to the background for a moment: the album stays open for it.
    private void Suspended() { if (!_sharing) Close(); }

    private void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var c = e.Surface.Canvas;
        var (w, h) = (e.Info.Width, e.Info.Height);
        var unit = w / 1920f;
        c.Clear(new SKColor(10, 14, 24, 235));
        using var font = new SKFont(PixelFont.Face, 34 * unit);
        using var small = new SKFont(PixelFont.Face, 26 * unit);
        using var paint = new SKPaint { Color = EditorPaint.Value, IsAntialias = false };
        if (_photos.Count == 0)
        {
            c.DrawText("Nenhuma foto ainda. Aperte X no parque para tirar uma.", w / 2f, h / 2f, SKTextAlign.Center, font, paint);
            return;
        }
        if (_shownIndex != _index)
        {
            _shown?.Dispose();
            _shown = SKBitmap.Decode(_photos[_index].Path);
            _shownIndex = _index;
        }
        var photo = _photos[_index];
        if (_shown is { } bitmap)
        {
            var scale = Math.Min(w * .86f / bitmap.Width, h * .72f / bitmap.Height);
            var dest = SKRect.Create((w - bitmap.Width * scale) / 2, 40 * unit, bitmap.Width * scale, bitmap.Height * scale);
            using var frame = new SKPaint { Color = new SKColor(0xf4, 0xf0, 0xe4) };
            c.DrawRect(SKRect.Inflate(dest, 10 * unit, 10 * unit), frame);
            using var crisp = new SKPaint { IsAntialias = false };
            c.DrawBitmap(bitmap, dest, crisp);
            var y = dest.Bottom + 70 * unit;
            c.DrawText(Fit(photo.Caption, font, w * .9f), w / 2f, y, SKTextAlign.Center, font, paint);
            paint.Color = EditorPaint.Label;
            c.DrawText($"{photo.TakenAt:d MMM yyyy, HH:mm}   ·   {_index + 1} / {_photos.Count}   ·   L R  fotos   X  compartilhar   B  fechar", w / 2f, y + 50 * unit, SKTextAlign.Center, small, paint);
            // The Share button, bottom right, for a finger.
            _shareButton = SKRect.Create(w - 280 * unit, h - 110 * unit, 240 * unit, 76 * unit);
            ParkCardPaint.Plate(c, _shareButton, true, unit);
            ParkCardPaint.Centered(c, _sharing ? "Compartilhando…" : "Compartilhar", _shareButton.MidX, _shareButton.MidY, font, EditorPaint.ChipInk);
        }
    }

    private static string Fit(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width) return text;
        while (text.Length > 1 && font.MeasureText(text + "…") > width) text = text[..^1];
        return text + "…";
    }

    private void Touch(object? sender, SKTouchEventArgs e)
    {
        e.Handled = true;
        if (e.ActionType != SKTouchAction.Released) return;
        var width = _view.CanvasSize.Width;
        if (_shareButton.Contains(e.Location)) _ = ShareAsync();
        else if (e.Location.X < width * .25f) Turn(-1);
        else if (e.Location.X > width * .75f) Turn(1);
        else Close();
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.L or PadButton.Left: Turn(-1); break;
            case PadButton.R or PadButton.Right: Turn(1); break;
            case PadButton.X: _ = ShareAsync(); break;
            case PadButton.B or PadButton.A: Close(); break;
        }
        return true;
    }

    // A copy with a readable name goes to Android's share sheet, its caption as the message.
    private async Task ShareAsync()
    {
        if (_sharing || _photos.Count == 0) return;
        var photo = _photos[_index];
        _sharing = true;
        _view.InvalidateSurface();
        try
        {
            var copy = Path.Combine(FileSystem.CacheDirectory, $"Poképark {photo.TakenAt:yyyy-MM-dd HH-mm-ss}.jpg");
            File.Copy(photo.Path, copy, overwrite: true);
            await Share.Default.RequestAsync(new ShareFileRequest { Title = photo.Caption, File = new ShareFile(copy, "image/jpeg") });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("album", $"Sharing a photo: {error.Message}");
        }
        finally
        {
            _sharing = false;
            if (!_closed) _view.InvalidateSurface();
        }
    }

    private void Turn(int delta)
    {
        if (_photos.Count == 0) return;
        _index = (_index + delta + _photos.Count) % _photos.Count;
        _view.InvalidateSurface();
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        App.Suspended -= Suspended;
        _router?.Remove(this);
        _host.Remove(_view);
        _shown?.Dispose();
        _done.TrySetResult();
    }
}
