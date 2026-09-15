using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace TgAutoposter.Infrastructure.Services.Cards;

/// <summary>What goes on a card. <see cref="Visual"/> is the real source image (tweet photo, video thumbnail...); null draws the branded pattern.</summary>
public sealed record BrandCardContent(
    string Rubric,
    string Headline,
    string? Kicker = null,
    SKBitmap? Visual = null,
    string? SourceLabel = null,
    bool Urgent = false);

/// <summary>
/// Vtubika-styled 1080×1350 news card: real image on top, short headline in Nunito Black, brand footer.
/// Palette and type follow vtubika.store (bg #0B1018, accent #FF6A2B, Nunito / Manrope, logo drawn as vector).
/// No AI-generated art: when there is no source image a typographic card with brand sparkles is drawn.
/// </summary>
public sealed partial class BrandCardRenderer : IDisposable
{
    public const int Width = 1080;
    public const int Height = 1350;
    private const float Margin = 64f;
    private const float FooterHeight = 72f;

    private static readonly SKColor Bg = SKColor.Parse("#0B1018");
    private static readonly SKColor Accent = SKColor.Parse("#FF6A2B");
    private static readonly SKColor AccentWarm = SKColor.Parse("#FF8A4C");
    private static readonly SKColor AccentLight = SKColor.Parse("#FFA06B");
    private static readonly SKColor Peach = SKColor.Parse("#FFD7BF");
    private static readonly SKColor Mint = SKColor.Parse("#8EDFD1");
    private static readonly SKColor Lavender = SKColor.Parse("#BDA7F7");
    private static readonly SKColor TextPrimary = SKColor.Parse("#F5F7FB");
    private static readonly SKColor TextMuted = SKColor.Parse("#98A2B3");
    private static readonly SKColor UrgentRed = SKColor.Parse("#FF3B4E");

    private const string SparklePath = "M12 0c1 6.5 4.5 10 12 12-7.5 2-11 5.5-12 12-1-6.5-4.5-10-12-12 7.5-2 11-5.5 12-12Z";

    private readonly SKTypeface _heavy;
    private readonly SKTypeface _uiBold;
    private readonly SKTypeface _ui;

    public BrandCardRenderer(string fontsDirectory)
    {
        _heavy = LoadTypeface(fontsDirectory, "Nunito-Black.ttf", SKFontStyleWeight.Black);
        _uiBold = LoadTypeface(fontsDirectory, "Manrope-Bold.ttf", SKFontStyleWeight.Bold);
        _ui = LoadTypeface(fontsDirectory, "Manrope-Medium.ttf", SKFontStyleWeight.Medium);
    }

    public static string DefaultFontsDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

    /// <summary>Renders the card and returns JPEG bytes.</summary>
    public byte[] Render(BrandCardContent content)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                            ?? throw new InvalidOperationException("Не удалось создать поверхность для карточки.");
        var canvas = surface.Canvas;
        canvas.Clear(Bg);

        var hasVisual = content.Visual is not null;
        var headline = Sanitize(content.Headline, _heavy);
        var kicker = string.IsNullOrWhiteSpace(content.Kicker) ? null : Sanitize(content.Kicker!, _uiBold).ToUpperInvariant();
        var contentWidth = Width - Margin * 2;

        var footerTop = Height - Margin - FooterHeight;
        var blockBottom = footerTop - 64f;

        var (titlePaint, lines) = FitHeadline(headline, contentWidth, hasVisual ? 4 : 5, hasVisual ? 90f : 110f, 44f);
        using var _ = titlePaint;
        var lineHeight = titlePaint.TextSize * 1.08f;
        var kickerSize = 30f;
        var kickerBlock = kicker is null ? 0f : kickerSize + 26f;
        var blockTop = blockBottom - lines.Count * lineHeight - kickerBlock;

        var visualBottom = Math.Clamp(blockTop + 70f, 520f, 900f);
        if (hasVisual)
        {
            DrawVisual(canvas, content.Visual!, visualBottom);
        }
        else
        {
            DrawPattern(canvas, blockTop);
        }

        DrawChip(canvas, content.Rubric, content.Urgent);
        if (!string.IsNullOrWhiteSpace(content.SourceLabel))
        {
            DrawSourceLabel(canvas, Sanitize(content.SourceLabel!, _uiBold));
        }

        var y = blockTop;
        if (kicker is not null)
        {
            using var kickerPaint = CreatePaint(_uiBold, kickerSize, Peach);
            kicker = Ellipsize(kicker, kickerPaint, contentWidth - 28f);
            var fm = kickerPaint.FontMetrics;
            var baseline = y - fm.Ascent;
            using var dot = new SKPaint { IsAntialias = true, Color = content.Urgent ? UrgentRed : Accent };
            canvas.DrawCircle(Margin + 7f, baseline + (fm.Ascent + fm.Descent) / 2f, 7f, dot);
            DrawSpaced(canvas, kicker, Margin + 28f, baseline, kickerPaint, 1.5f);
            y += kickerBlock;
        }

        var titleMetrics = titlePaint.FontMetrics;
        using var shadow = titlePaint.Clone();
        shadow.Color = new SKColor(0, 0, 0, 150);
        shadow.ImageFilter = SKImageFilter.CreateBlur(10f, 10f);
        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = y - titleMetrics.Ascent * 0.92f + i * lineHeight;
            if (hasVisual)
            {
                canvas.DrawText(lines[i], Margin, baseline + 3f, shadow);
            }

            canvas.DrawText(lines[i], Margin, baseline, titlePaint);
        }

        DrawFooter(canvas, footerTop);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    // ---- regions ----

    private static void DrawVisual(SKCanvas canvas, SKBitmap bitmap, float regionBottom)
    {
        var region = new SKRect(0, 0, Width, regionBottom);
        var regionAspect = region.Width / region.Height;
        var sourceAspect = bitmap.Width / (float)bitmap.Height;
        using var hq = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };

        canvas.Save();
        canvas.ClipRect(region);
        // Wide sources (video thumbnails, tweet banners) are cropped at the sides rather than letterboxed:
        // a contained 16:9 frame leaves an empty band above the headline. Tall/square ones keep the blurred fill.
        var ratio = MathF.Log(sourceAspect / regionAspect);
        if (ratio is >= -0.3f and <= 0.55f)
        {
            // Close enough: fill the region, keep faces (upper third) for taller sources.
            canvas.DrawBitmap(bitmap, CoverCrop(bitmap, regionAspect, sourceAspect < regionAspect ? 0.3f : 0.5f), region, hq);
        }
        else
        {
            // Very different shape (square meme, portrait art): blurred fill behind, whole image in front.
            var bleed = new SKRect(-90, -90, Width + 90, regionBottom + 90);
            using (var blur = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium, ImageFilter = SKImageFilter.CreateBlur(46f, 46f) })
            {
                canvas.DrawBitmap(bitmap, CoverCrop(bitmap, bleed.Width / bleed.Height, 0.5f), bleed, blur);
            }

            using (var dim = new SKPaint { Color = new SKColor(11, 16, 24, 140) })
            {
                canvas.DrawRect(region, dim);
            }

            var target = ContainRect(bitmap, new SKRect(0, 0, Width, regionBottom - 40f), 2.4f);
            canvas.DrawBitmap(bitmap, target, hq);
        }

        canvas.Restore();

        using var topShade = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, 240),
                [new SKColor(0, 0, 0, 120), new SKColor(0, 0, 0, 0)], [0f, 1f], SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, Width, 240, topShade);

        var fadeTop = regionBottom - 330f;
        using var bottomFade = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, fadeTop), new SKPoint(0, regionBottom),
                [Bg.WithAlpha(0), Bg.WithAlpha(190), Bg], [0f, 0.55f, 1f], SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, fadeTop, Width, regionBottom - fadeTop + 1f, bottomFade);
    }

    private static void DrawPattern(SKCanvas canvas, float blockTop)
    {
        using (var glow = new SKPaint
               {
                   Shader = SKShader.CreateRadialGradient(new SKPoint(Width * 0.8f, 250), 700,
                       [Accent.WithAlpha(95), Accent.WithAlpha(0)], [0f, 1f], SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawRect(0, 0, Width, Height, glow);
        }

        using (var glow2 = new SKPaint
               {
                   Shader = SKShader.CreateRadialGradient(new SKPoint(80, blockTop), 620,
                       [Lavender.WithAlpha(55), Lavender.WithAlpha(0)], [0f, 1f], SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawRect(0, 0, Width, Height, glow2);
        }

        var area = Math.Max(260f, blockTop - 150f);
        DrawSparkle(canvas, Width - 330f, 150f + area * 0.18f, Math.Min(250f, area * 0.62f), AccentWarm, 255);
        DrawSparkle(canvas, Width - 150f, 150f + area * 0.62f, Math.Min(96f, area * 0.24f), Peach, 235);
        DrawSparkle(canvas, Width - 520f, 150f + area * 0.1f, Math.Min(58f, area * 0.15f), Mint, 230);
        DrawSparkle(canvas, 150f, 150f + area * 0.55f, Math.Min(74f, area * 0.18f), Lavender, 200);
    }

    private void DrawChip(SKCanvas canvas, string rubric, bool urgent)
    {
        var text = Sanitize(rubric, _heavy).ToUpperInvariant();
        using var paint = CreatePaint(_heavy, 28f, SKColors.White);
        const float height = 60f;
        const float padX = 24f;
        const float icon = 22f;
        const float gap = 12f;
        var textWidth = MeasureSpaced(paint, text, 2.2f);
        var rect = new SKRect(Margin, Margin, Margin + padX + icon + gap + textWidth + padX, Margin + height);

        using var fill = new SKPaint { IsAntialias = true, Color = urgent ? UrgentRed : Accent };
        canvas.DrawRoundRect(rect, height / 2f, height / 2f, fill);
        DrawSparkle(canvas, rect.Left + padX, rect.MidY - icon / 2f, icon, SKColors.White, 255);

        var fm = paint.FontMetrics;
        var baseline = rect.MidY - (fm.Ascent + fm.Descent) / 2f;
        DrawSpaced(canvas, text, rect.Left + padX + icon + gap, baseline, paint, 2.2f);
    }

    private void DrawSourceLabel(SKCanvas canvas, string label)
    {
        using var paint = CreatePaint(_uiBold, 24f, TextPrimary);
        label = Ellipsize(label, paint, 420f);
        const float height = 52f;
        const float padX = 20f;
        var width = paint.MeasureText(label) + padX * 2;
        var rect = new SKRect(Width - Margin - width, Margin + 4f, Width - Margin, Margin + 4f + height);
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(11, 16, 24, 185) };
        canvas.DrawRoundRect(rect, height / 2f, height / 2f, fill);
        var fm = paint.FontMetrics;
        canvas.DrawText(label, rect.Left + padX, rect.MidY - (fm.Ascent + fm.Descent) / 2f, paint);
    }

    private void DrawFooter(SKCanvas canvas, float top)
    {
        using (var line = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 24), StrokeWidth = 2f })
        {
            canvas.DrawLine(Margin, top - 30f, Width - Margin, top - 30f, line);
        }

        DrawLogo(canvas, Margin, top, FooterHeight);

        using var word = CreatePaint(_heavy, 46f, TextPrimary);
        var fm = word.FontMetrics;
        var baseline = top + FooterHeight / 2f - (fm.Ascent + fm.Descent) / 2f;
        canvas.DrawText("vtubika", Margin + FooterHeight + 18f, baseline, word);

        using var site = CreatePaint(_ui, 28f, TextMuted);
        const string siteText = "vtubika.store";
        var sfm = site.FontMetrics;
        canvas.DrawText(siteText, Width - Margin - site.MeasureText(siteText), top + FooterHeight / 2f - (sfm.Ascent + sfm.Descent) / 2f, site);
    }

    // ---- brand shapes ----

    /// <summary>The vtubika.store logo (stack of listing cards with awning and price tag), drawn from its SVG geometry.</summary>
    private static void DrawLogo(SKCanvas canvas, float x, float y, float size)
    {
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(size / 76f);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        canvas.Save();
        canvas.RotateDegrees(-10, 32, 40);
        fill.Color = Lavender;
        canvas.DrawRoundRect(new SKRect(8, 14, 56, 66), 11, 11, fill);
        canvas.Restore();

        canvas.Save();
        canvas.RotateDegrees(-5, 35, 39);
        fill.Color = Mint;
        canvas.DrawRoundRect(new SKRect(10, 12, 60, 66), 11, 11, fill);
        canvas.Restore();

        fill.Color = AccentWarm;
        canvas.DrawRoundRect(new SKRect(14, 8, 66, 66), 12, 12, fill);
        fill.Color = SKColors.White;
        canvas.DrawRoundRect(new SKRect(17.5f, 11.5f, 62.5f, 62.5f), 9, 9, fill);

        using var awning = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(17.5f, 11.5f), new SKPoint(62.5f, 25f), [AccentLight, AccentWarm], [0f, 1f], SKShaderTileMode.Clamp)
        };
        FillPath(canvas, "M17.5 20.5v-4a5 5 0 0 1 5-5h35a5 5 0 0 1 5 5v4h-45Z", awning);
        FillPath(canvas, "M17.5 20.5h9a4.5 4.5 0 0 1-9 0Z", awning);
        FillPath(canvas, "M26.5 20.5h9a4.5 4.5 0 0 1-9 0Z", fill);
        FillPath(canvas, "M35.5 20.5h9a4.5 4.5 0 0 1-9 0Z", awning);
        FillPath(canvas, "M44.5 20.5h9a4.5 4.5 0 0 1-9 0Z", fill);
        FillPath(canvas, "M53.5 20.5h9a4.5 4.5 0 0 1-9 0Z", awning);

        fill.Color = Peach;
        canvas.DrawCircle(33, 36, 6.5f, fill);
        FillPath(canvas, "M22.5 53c1.6-5.5 5.7-8.5 10.5-8.5S41.9 47.5 43.5 53v1.5a2 2 0 0 1-2 2H24.5a2 2 0 0 1-2-2V53Z", fill);

        fill.Color = AccentWarm;
        canvas.DrawRoundRect(new SKRect(24, 58.5f, 37, 62.1f), 1.8f, 1.8f, fill);
        fill.Color = Mint;
        canvas.DrawCircle(43, 60.3f, 2.1f, fill);
        fill.Color = Lavender;
        canvas.DrawCircle(49.5f, 60.3f, 2.1f, fill);

        canvas.Save();
        canvas.RotateDegrees(14, 58, 38);
        const string tag = "M52 30.5 58.5 27a3 3 0 0 1 2.9 0l6 3.4a3 3 0 0 1 1.6 2.7v10.4a3 3 0 0 1-3 3H55a3 3 0 0 1-3-3V33.2a3 3 0 0 1 0-2.7Z";
        fill.Color = AccentWarm;
        FillPath(canvas, tag, fill);
        using (var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f, Color = SKColors.White })
        {
            FillPath(canvas, tag, stroke);
        }

        fill.Color = SKColors.White;
        FillPath(canvas, "M60 32.5c.5 2.8 1.9 4.2 4.7 4.7-2.8.5-4.2 1.9-4.7 4.7-.5-2.8-1.9-4.2-4.7-4.7 2.8-.5 4.2-1.9 4.7-4.7Z", fill);
        canvas.Restore();

        canvas.Restore();
    }

    private static void DrawSparkle(SKCanvas canvas, float x, float y, float size, SKColor color, byte alpha)
    {
        using var path = SKPath.ParseSvgPathData(SparklePath);
        using var paint = new SKPaint { IsAntialias = true, Color = color.WithAlpha(alpha) };
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(size / 24f);
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }

    private static void FillPath(SKCanvas canvas, string svg, SKPaint paint)
    {
        using var path = SKPath.ParseSvgPathData(svg);
        canvas.DrawPath(path, paint);
    }

    // ---- text ----

    private (SKPaint Paint, List<string> Lines) FitHeadline(string text, float width, int maxLines, float maxSize, float minSize)
    {
        for (var size = maxSize; size >= minSize; size -= 4f)
        {
            var paint = CreatePaint(_heavy, size, TextPrimary);
            var lines = Wrap(text, paint, width);
            if (lines.Count <= maxLines)
            {
                return (paint, lines);
            }

            paint.Dispose();
        }

        var smallest = CreatePaint(_heavy, minSize, TextPrimary);
        var wrapped = Wrap(text, smallest, width).Take(maxLines).ToList();
        wrapped[^1] = Ellipsize(wrapped[^1] + " …", smallest, width);
        return (smallest, wrapped);
    }

    private static List<string> Wrap(string text, SKPaint paint, float width)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (paint.MeasureText(candidate) <= width)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }

            current = word;
            while (paint.MeasureText(current) > width && current.Length > 1)
            {
                var cut = current.Length - 1;
                while (cut > 1 && paint.MeasureText(current[..cut]) > width)
                {
                    cut--;
                }

                lines.Add(current[..cut]);
                current = current[cut..];
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        // Avoid a lonely short word on the last line when the previous line can give one away.
        if (lines.Count >= 2 && lines[^1].Length <= 4 && lines[^2].Contains(' '))
        {
            var prev = lines[^2];
            var moved = prev[(prev.LastIndexOf(' ') + 1)..];
            var merged = $"{moved} {lines[^1]}";
            if (paint.MeasureText(merged) <= width)
            {
                lines[^2] = prev[..prev.LastIndexOf(' ')];
                lines[^1] = merged;
            }
        }

        return lines.Count == 0 ? [text] : lines;
    }

    private static string Ellipsize(string text, SKPaint paint, float width)
    {
        if (paint.MeasureText(text) <= width)
        {
            return text;
        }

        var value = text.TrimEnd(' ', '…');
        while (value.Length > 1 && paint.MeasureText(value + "…") > width)
        {
            var space = value.LastIndexOf(' ');
            value = space > 0 ? value[..space] : value[..^1];
        }

        return value.TrimEnd(' ', ',', '.', ':', ';', '—', '-') + "…";
    }

    private static float MeasureSpaced(SKPaint paint, string text, float spacing)
    {
        var total = 0f;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            total += paint.MeasureText((string)e.Current) + spacing;
        }

        return Math.Max(0, total - spacing);
    }

    private static void DrawSpaced(SKCanvas canvas, string text, float x, float baseline, SKPaint paint, float spacing)
    {
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            canvas.DrawText(element, x, baseline, paint);
            x += paint.MeasureText(element) + spacing;
        }
    }

    /// <summary>Drops markdown, emoji and any glyph the brand font can't draw (CJK, symbols) so nothing renders as tofu.</summary>
    private static string Sanitize(string text, SKTypeface typeface)
    {
        using var probe = new SKPaint { Typeface = typeface };
        var cleaned = text.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty).ReplaceLineEndings(" ");
        var builder = new StringBuilder(cleaned.Length);
        var e = StringInfo.GetTextElementEnumerator(cleaned);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (char.IsWhiteSpace(element[0]))
            {
                builder.Append(' ');
            }
            else if (probe.ContainsGlyphs(element))
            {
                builder.Append(element);
            }
        }

        return WhitespaceRegex().Replace(builder.ToString(), " ").Trim(' ', ',', ':', ';', '—', '-', '|');
    }

    private static SKPaint CreatePaint(SKTypeface typeface, float size, SKColor color)
    {
        return new SKPaint
        {
            IsAntialias = true,
            SubpixelText = true,
            Typeface = typeface,
            TextSize = size,
            Color = color
        };
    }

    // ---- geometry ----

    private static SKRect CoverCrop(SKBitmap bitmap, float targetAspect, float verticalFocus)
    {
        var sourceAspect = bitmap.Width / (float)bitmap.Height;
        if (sourceAspect > targetAspect)
        {
            var width = bitmap.Height * targetAspect;
            var left = (bitmap.Width - width) / 2f;
            return new SKRect(left, 0, left + width, bitmap.Height);
        }

        var height = bitmap.Width / targetAspect;
        var top = (bitmap.Height - height) * verticalFocus;
        return new SKRect(0, top, bitmap.Width, top + height);
    }

    private static SKRect ContainRect(SKBitmap bitmap, SKRect bounds, float maxUpscale)
    {
        var scale = Math.Min(bounds.Width / bitmap.Width, bounds.Height / bitmap.Height);
        scale = Math.Min(scale, maxUpscale);
        var width = bitmap.Width * scale;
        var height = bitmap.Height * scale;
        var left = bounds.Left + (bounds.Width - width) / 2f;
        var top = bounds.Top + (bounds.Height - height) / 2f;
        return new SKRect(left, top, left + width, top + height);
    }

    private static SKTypeface LoadTypeface(string directory, string file, SKFontStyleWeight fallbackWeight)
    {
        var path = Path.Combine(directory, file);
        if (File.Exists(path))
        {
            var typeface = SKTypeface.FromFile(path);
            if (typeface is not null)
            {
                return typeface;
            }
        }

        return SKTypeface.FromFamilyName("DejaVu Sans", fallbackWeight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
    }

    public void Dispose()
    {
        _heavy.Dispose();
        _uiBold.Dispose();
        _ui.Dispose();
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
