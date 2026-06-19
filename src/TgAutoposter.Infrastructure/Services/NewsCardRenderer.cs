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

    private static void DrawCard(
        SKCanvas canvas,
        SKBitmap visual,
        Channel channel,
        Post post,
        string rubric,
        string mainThesis)
    {
        canvas.Clear(new SKColor(5, 6, 8));
        DrawBackground(canvas);
        DrawVisual(canvas, visual);
        DrawFrame(canvas);
        DrawText(canvas, channel, post, rubric, mainThesis);
    }

    private static void DrawBackground(SKCanvas canvas)
    {
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(Width, Height),
                new[] { new SKColor(4, 5, 7), new SKColor(13, 15, 20), new SKColor(3, 4, 6) },
                [0f, 0.55f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, Width, Height, fill);

        using var linePaint = new SKPaint
        {
            Color = new SKColor(46, 55, 76, 95),
            StrokeWidth = 1,
            IsAntialias = true
        };

        canvas.DrawLine(Margin + 24, 214, Width - Margin - 24, 214, linePaint);
        canvas.DrawLine(Margin + 24, Height - 184, Width - Margin - 24, Height - 184, linePaint);
        canvas.DrawLine(542, 260, 542, Height - 238, linePaint);

        using var accentPaint = new SKPaint
        {
            Color = new SKColor(52, 118, 255, 230),
            StrokeWidth = 3,
            IsAntialias = true
        };
        canvas.DrawLine(Margin + 24, 155, Margin + 188, 155, accentPaint);
        canvas.DrawLine(Width - Margin - 196, Height - 155, Width - Margin - 24, Height - 155, accentPaint);

        using var dotPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 24),
            IsAntialias = true
        };

        for (var x = 610; x <= 978; x += 28)
        {
            for (var y = 190; y <= 1110; y += 28)
            {
                canvas.DrawCircle(x, y, 1.35f, dotPaint);
            }
        }
    }

    private static void DrawVisual(SKCanvas canvas, SKBitmap visual)
    {
        var destination = new SKRect(578, 300, 980, 980);
        var source = CropToCover(visual, destination.Width / destination.Height);

        canvas.Save();
        using var clipPath = new SKPath();
        clipPath.AddRoundRect(destination, 22, 22);
        canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

        using var imagePaint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High
        };
        canvas.DrawBitmap(visual, source, destination, imagePaint);

        using var overlay = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(destination.Left, destination.Top),
                new SKPoint(destination.Right, destination.Bottom),
                new[] { new SKColor(0, 0, 0, 36), new SKColor(0, 0, 0, 96) },
                [0f, 1f],
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(destination, overlay);
        canvas.Restore();

        using var border = new SKPaint
        {
            Color = new SKColor(85, 97, 126, 120),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        canvas.DrawRoundRect(destination, 22, 22, border);
    }

    private static void DrawFrame(SKCanvas canvas)
    {
        using var frame = new SKPaint
        {
            Color = new SKColor(82, 92, 118, 160),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        canvas.DrawRoundRect(new SKRect(Margin, Margin, Width - Margin, Height - Margin), 18, 18, frame);
    }

    private static void DrawText(
        SKCanvas canvas,
        Channel channel,
        Post post,
        string rubric,
        string mainThesis)
    {
        var brandName = string.IsNullOrWhiteSpace(channel.Name) ? "Daru Games" : channel.Name.Trim();
        var title = CleanDisplayText(mainThesis);
        var rubricText = CleanDisplayText(rubric).ToUpperInvariant();
        var meta = post.PublicationKind == PublicationKind.Trailer
            ? "VIDEO"
            : post.PublicationKind == PublicationKind.Deal
                ? "DEAL"
                : "NEWS";

        using var rubricPaint = CreatePaint(29, SKFontStyleWeight.SemiBold, new SKColor(137, 172, 255));
        using var titlePaint = CreatePaint(69, SKFontStyleWeight.SemiBold, new SKColor(247, 248, 248));
        using var brandPaint = CreatePaint(30, SKFontStyleWeight.SemiBold, new SKColor(247, 248, 248));
        using var metaPaint = CreatePaint(21, SKFontStyleWeight.Medium, new SKColor(138, 143, 152));

        DrawTextLine(canvas, rubricText, 82, 132, rubricPaint);
        DrawTextLine(canvas, meta, Width - Margin - 110, 132, metaPaint);

        var wrapped = WrapText(title, titlePaint, 450, 4);
        var titleHeight = wrapped.Count * 82;
        var y = 575 - titleHeight / 2f;
        foreach (var line in wrapped)
        {
            DrawTextLine(canvas, line, 82, y, titlePaint);
            y += 82;
        }

        DrawTextLine(canvas, brandName, 82, Height - 98, brandPaint);
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

    private static void DrawTextLine(SKCanvas canvas, string text, float x, float baselineY, SKPaint paint)
    {
        canvas.DrawText(text, x, baselineY, paint);
    }

    private static List<string> WrapText(string text, SKPaint paint, float maxWidth, int maxLines)
    {
        var words = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = string.IsNullOrWhiteSpace(current) ? word : $"{current} {word}";
            if (paint.MeasureText(candidate) <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                lines.Add(current);
            }

            current = word;
            if (lines.Count == maxLines)
            {
                break;
            }
        }

        if (lines.Count < maxLines && !string.IsNullOrWhiteSpace(current))
        {
            lines.Add(current);
        }

        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
        }

        if (lines.Count == maxLines && words.Count > string.Join(' ', lines).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
        {
            var last = lines[^1].TrimEnd('.', '…');
            while (paint.MeasureText($"{last}…") > maxWidth && last.Length > 3)
            {
                var lastSpace = last.LastIndexOf(' ');
                last = lastSpace > 0 ? last[..lastSpace] : last[..^1];
            }

            lines[^1] = $"{last}…";
        }

        return lines.Count == 0 ? [text] : lines;
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
