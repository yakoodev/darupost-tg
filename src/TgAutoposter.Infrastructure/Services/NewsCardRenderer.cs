using SkiaSharp;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

public static class NewsCardRenderer
{
    private const int Width = 1080;
    private const int Height = 1350;
    private const int Margin = 56;

    public static async Task<string?> RenderAsync(
        HttpClient httpClient,
        MediaOptions mediaOptions,
        Channel channel,
        Post post,
        string sourceImageUrl,
        string rubric,
        string mainThesis,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceImageUrl))
        {
            return null;
        }

        var visual = await DownloadBitmapAsync(httpClient, sourceImageUrl, cancellationToken);
        if (visual is null)
        {
            return null;
        }

        using var visualBitmap = visual;
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return null;
        }

        var canvas = surface.Canvas;
        DrawCard(canvas, visualBitmap, channel, post, rubric, mainThesis);

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 96);
        if (encoded is null)
        {
            return null;
        }

        var fileName = $"{post.Id:N}.png";
        var publicPath = LocalMediaPaths.BuildPublicPath("generated", fileName);
        var fullPath = LocalMediaPaths.BuildFullPath(mediaOptions, publicPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var stream = File.Open(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        encoded.SaveTo(stream);

        return publicPath;
    }

    private static async Task<SKBitmap?> DownloadBitmapAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return SKBitmap.Decode(stream);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static readonly SKColor Accent = new(94, 106, 210);

    private static void DrawCard(
        SKCanvas canvas,
        SKBitmap visual,
        Channel channel,
        Post post,
        string rubric,
        string mainThesis)
    {
        canvas.Clear(new SKColor(8, 8, 12));
        DrawFullBleedVisual(canvas, visual);
        DrawRubricChip(canvas, post, rubric);
        DrawHeadline(canvas, channel, mainThesis);
    }

    private static void DrawFullBleedVisual(SKCanvas canvas, SKBitmap visual)
    {
        var destination = new SKRect(0, 0, Width, Height);
        var source = CropToCover(visual, Width / (float)Height);
        using var imagePaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(visual, source, destination, imagePaint);

        // Top scrim so the rubric chip stays readable over bright images.
        using var topScrim = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, 320),
                new[] { new SKColor(0, 0, 0, 150), new SKColor(0, 0, 0, 0) },
                [0f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, Width, 320, topScrim);

        // Bottom scrim carrying the headline + brand.
        using var bottomScrim = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, Height - 720),
                new SKPoint(0, Height),
                new[] { new SKColor(0, 0, 0, 0), new SKColor(6, 6, 10, 160), new SKColor(4, 4, 8, 244) },
                [0f, 0.55f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, Height - 720, Width, Height, bottomScrim);
    }

    private static void DrawRubricChip(SKCanvas canvas, Post post, string rubric)
    {
        var rubricText = CleanDisplayText(rubric).ToUpperInvariant();
        var meta = post.PublicationKind switch
        {
            PublicationKind.Trailer => "VIDEO",
            PublicationKind.Deal => "DEAL",
            PublicationKind.Meme => "MEME",
            _ => "NEWS"
        };

        using var chipText = CreatePaint(28, SKFontStyleWeight.Bold, SKColors.White);
        var textWidth = chipText.MeasureText(rubricText);
        const float padX = 20f;
        const float padY = 13f;
        var chip = new SKRect(Margin, Margin, Margin + textWidth + padX * 2, Margin + chipText.TextSize + padY * 2);

        using var chipFill = new SKPaint { IsAntialias = true, Color = Accent };
        canvas.DrawRoundRect(chip, 10, 10, chipFill);
        canvas.DrawText(rubricText, chip.Left + padX, chip.Top + padY + chipText.TextSize - 4, chipText);

        using var metaPaint = CreatePaint(22, SKFontStyleWeight.Medium, new SKColor(220, 222, 235));
        canvas.DrawText(meta, Width - Margin - metaPaint.MeasureText(meta), chip.MidY + 8, metaPaint);
    }

    private static void DrawHeadline(SKCanvas canvas, Channel channel, string mainThesis)
    {
        var brandName = string.IsNullOrWhiteSpace(channel.Name) ? "Только игры" : channel.Name.Trim();
        var title = CleanDisplayText(mainThesis);

        var maxWidth = Width - Margin * 2f;
        const int maxLines = 4;
        SKPaint titlePaint = CreatePaint(92, SKFontStyleWeight.Bold, SKColors.White);
        var wrapped = new List<string>();
        for (var size = 92; size >= 50; size -= 4)
        {
            titlePaint.Dispose();
            titlePaint = CreatePaint(size, SKFontStyleWeight.Bold, SKColors.White);
            var (lines, truncated) = WrapText(title, titlePaint, maxWidth, maxLines);
            wrapped = lines;
            if (!truncated)
            {
                break;
            }
        }

        titlePaint.ImageFilter = SKImageFilter.CreateDropShadow(0, 2, 6, 6, new SKColor(0, 0, 0, 200));

        // Brand line at the very bottom; headline stacked just above it.
        var brandBaseline = Height - 70f;
        using var accentLine = new SKPaint { Color = Accent, IsAntialias = true, StrokeWidth = 5 };
        canvas.DrawLine(Margin, brandBaseline - 92, Margin + 66, brandBaseline - 92, accentLine);

        using var brandPaint = CreatePaint(31, SKFontStyleWeight.SemiBold, new SKColor(232, 233, 242));
        canvas.DrawText(brandName, Margin, brandBaseline, brandPaint);

        var lineHeight = titlePaint.TextSize * 1.16f;
        var bottomBaseline = brandBaseline - 150f;
        for (var i = 0; i < wrapped.Count; i++)
        {
            var baseline = bottomBaseline - (wrapped.Count - 1 - i) * lineHeight;
            canvas.DrawText(wrapped[i], Margin, baseline, titlePaint);
        }

        titlePaint.Dispose();
    }

    private static SKPaint CreatePaint(float textSize, SKFontStyleWeight weight, SKColor color)
    {
        return new SKPaint
        {
            IsAntialias = true,
            Color = color,
            Typeface = SKTypeface.FromFamilyName("Inter", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                       ?? SKTypeface.FromFamilyName("DejaVu Sans", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                       ?? SKTypeface.Default,
            TextSize = textSize,
            SubpixelText = true,
            LcdRenderText = true
        };
    }

    private static (List<string> Lines, bool Truncated) WrapText(string text, SKPaint paint, float maxWidth, int maxLines)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lines = new List<string>();
        var current = string.Empty;
        var consumed = 0;

        foreach (var word in words)
        {
            // A single word wider than the column: hard-break by characters (last resort).
            var pieces = paint.MeasureText(word) > maxWidth ? HardBreak(word, paint, maxWidth) : [word];

            foreach (var piece in pieces)
            {
                var candidate = current.Length == 0 ? piece : $"{current} {piece}";
                if (paint.MeasureText(candidate) <= maxWidth)
                {
                    current = candidate;
                }
                else
                {
                    if (current.Length > 0)
                    {
                        lines.Add(current);
                    }

                    current = piece;
                    if (lines.Count >= maxLines)
                    {
                        break;
                    }
                }
            }

            if (lines.Count >= maxLines)
            {
                break;
            }

            consumed++;
        }

        if (lines.Count < maxLines && current.Length > 0)
        {
            lines.Add(current);
            consumed = words.Length;
        }

        var truncated = consumed < words.Length;
        if (truncated && lines.Count > 0)
        {
            // Trim a trailing hanging short word (preposition/conjunction), then add an ellipsis,
            // shrinking at word boundary until the ellipsis fits.
            var last = lines[^1].TrimEnd('.', '…', ',', ' ');
            var parts = last.Split(' ');
            if (parts.Length > 1 && parts[^1].Length <= 2)
            {
                last = string.Join(' ', parts[..^1]);
            }

            while (last.Contains(' ') && paint.MeasureText($"{last}…") > maxWidth)
            {
                last = last[..last.LastIndexOf(' ')];
            }

            lines[^1] = $"{last}…";
        }

        return lines.Count == 0 ? ([text], false) : (lines, truncated);
    }

    private static List<string> HardBreak(string word, SKPaint paint, float maxWidth)
    {
        var pieces = new List<string>();
        var chunk = string.Empty;
        foreach (var ch in word)
        {
            if (paint.MeasureText(chunk + ch) > maxWidth && chunk.Length > 0)
            {
                pieces.Add(chunk);
                chunk = string.Empty;
            }

            chunk += ch;
        }

        if (chunk.Length > 0)
        {
            pieces.Add(chunk);
        }

        return pieces;
    }

    private static SKRect CropToCover(SKBitmap bitmap, float targetAspect)
    {
        var sourceAspect = bitmap.Width / (float)bitmap.Height;
        if (sourceAspect > targetAspect)
        {
            var width = bitmap.Height * targetAspect;
            var left = (bitmap.Width - width) / 2f;
            return new SKRect(left, 0, left + width, bitmap.Height);
        }

        var height = bitmap.Width / targetAspect;
        var top = (bitmap.Height - height) / 2f;
        return new SKRect(0, top, bitmap.Width, top + height);
    }

    private static string CleanDisplayText(string value)
    {
        return string.Join(' ', value
            .ReplaceLineEndings(" ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim(' ', '.', ',', ':', ';');
    }
}
