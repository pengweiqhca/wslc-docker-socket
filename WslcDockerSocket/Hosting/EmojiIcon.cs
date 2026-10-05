namespace WslcDockerSocket.Hosting;

using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;

/// <summary>
/// Renders an emoji glyph into a tray-sized <see cref="Icon"/> in color, so the tray icon needs no bundled
/// image asset and doesn't borrow any project's logo (Docker's, WSL's, etc.). GDI+ (<c>System.Drawing</c>
/// alone) can only draw "Segoe UI Emoji" as flat monochrome shapes; Skia's own text renderer understands the
/// font's embedded color bitmap glyphs, which is what makes this actually colorful.
/// </summary>
internal static class EmojiIcon
{
    private const int SizePixels = 32;

    public static Icon Render(string emoji)
    {
        using var surface = SKSurface.Create(new SKImageInfo(SizePixels, SizePixels, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // Color emoji glyphs come from a fixed-size bitmap strike embedded in the font, which only fills a
        // fraction of the nominal em-box; raising the font size just adds whitespace around it instead of
        // making the glyph bigger. Measuring the actual drawn bounds and scaling that up to fill the icon is
        // what makes the glyph itself take up the space, rather than the font's built-in padding.
        const float measureTextSize = 64f;
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI Emoji"), measureTextSize);
        using var paint = new SKPaint { IsAntialias = true };

        var bounds = new SKRect();
        font.MeasureText(emoji, out bounds, paint);

        var padding = SizePixels * 0.05f;
        var availableSize = SizePixels - 2 * padding;
        var scale = Math.Min(availableSize / bounds.Width, availableSize / bounds.Height);

        canvas.Translate(SizePixels / 2f, SizePixels / 2f);
        canvas.Scale(scale);
        canvas.Translate(-(bounds.Left + bounds.Width / 2f), -(bounds.Top + bounds.Height / 2f));
        canvas.DrawText(emoji, 0, 0, SKTextAlign.Left, font, paint);

        using var skImage = surface.Snapshot();
        using var pngData = skImage.Encode(SKEncodedImageFormat.Png, 100);
        using var pngStream = pngData.AsStream();
        using var bitmap = (Bitmap)Image.FromStream(pngStream);

        var hIcon = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle keeps referencing the native HICON rather than owning it, so it's cloned into a
            // fully managed Icon before the original handle is destroyed below.
            using var handleIcon = Icon.FromHandle(hIcon);
            return (Icon)handleIcon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }
}
