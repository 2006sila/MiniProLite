using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace IconGen;

/// <summary>
/// MiniProLite 图标生成器：画 8 个候选方案 → 输出 256 PNG + 多尺寸 ICO + 一张对比图。
/// 全部在 1024×1024 画布上绘制后缩放，描边都很粗，保证 16px 下仍可辨认。
/// 用法：dotnet run --project icon-gen -c Release -- icons
/// </summary>
internal static class Program
{
    private const int Canvas = 1024;
    private static readonly Color Accent = Color.FromArgb(0x2F, 0x6F, 0xED);
    private static readonly Color AccentDark = Color.FromArgb(0x18, 0x45, 0xAE);
    private static readonly Color White = Color.White;

    /// <param name="Key">文件名前缀（A–H）。</param>
    /// <param name="Slug">ASCII 短名；文件名只用 Key+Slug，避免中文名在不同压缩/传输环节乱码。</param>
    /// <param name="Name">对比图上显示的中文标签，保持中文。</param>
    private sealed record Concept(string Key, string Slug, string Name, Action<Graphics> Draw);

    private static void Main(string[] args)
    {
        var outDir = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "icons");
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        var concepts = new List<Concept>
        {
            new("A", "fan-circle", "风扇-圆底", g => DrawFan(g)),
            new("B", "gauge-circle", "仪表盘-圆底", DrawGauge),
            new("C", "shield-circle", "盾牌-圆底", DrawShield),
            new("D", "bars-circle", "档位柱-圆底", g => DrawBars(g)),
            new("E", "letter-p-circle", "字母P-圆底", DrawLetterP),
            new("F", "ring-circle", "气流环-圆底", DrawRing),
            new("G", "bars-square", "档位柱-方底", g => DrawBars(g, square: true)),
            new("H", "fan-square", "风扇-方底", g => DrawFan(g, square: true)),
        };

        foreach (var concept in concepts)
        {
            using var bmp = Render(concept.Draw);
            var baseName = Path.Combine(outDir, $"{concept.Key}-{concept.Slug}");
            using (var small = Resize(bmp, 256))
            {
                small.Save(baseName + ".png", ImageFormat.Png);
            }

            SaveIco(bmp, baseName + ".ico");
            Console.WriteLine($"  {concept.Key} {concept.Slug} ({concept.Name})");
        }

        using var sheet = BuildSheet(concepts);
        sheet.Save(Path.Combine(outDir, "preview-all.png"), ImageFormat.Png);
        Console.WriteLine($"输出目录：{outDir}");
    }

    // ---------------- 基础设施 ----------------

    private static Bitmap Render(Action<Graphics> draw)
    {
        var bmp = new Bitmap(Canvas, Canvas, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        draw(g);
        return bmp;
    }

    private static Bitmap Resize(Bitmap source, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, size, size), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        return bmp;
    }

    /// <summary>写 Vista+ 风格 ICO：每个尺寸都是一段 PNG。</summary>
    private static void SaveIco(Bitmap source, string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var payloads = new List<(int Size, byte[] Data)>();
        foreach (var size in sizes)
        {
            using var bmp = Resize(source, size);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            payloads.Add((size, ms.ToArray()));
        }

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)payloads.Count);

        var offset = 6 + payloads.Count * 16;
        foreach (var (size, data) in payloads)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write((uint)data.Length);
            w.Write((uint)offset);
            offset += data.Length;
        }

        foreach (var (_, data) in payloads)
        {
            w.Write(data);
        }
    }

    /// <summary>底：圆形或圆角方形，蓝色渐变。</summary>
    private static void Backdrop(Graphics g, bool square)
    {
        using var gradient = new LinearGradientBrush(
            new Rectangle(0, 0, Canvas, Canvas), Accent, AccentDark, 60f);

        if (square)
        {
            using var path = Rounded(new RectangleF(12, 12, Canvas - 24, Canvas - 24), 210);
            g.FillPath(gradient, path);
        }
        else
        {
            g.FillEllipse(gradient, 12, 12, Canvas - 24, Canvas - 24);
        }
    }

    private static void Disc(Graphics g, float cx, float cy, float r, Color fill)
    {
        using var b = new SolidBrush(fill);
        g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---------------- A / H：三叶风扇 ----------------

    private static void DrawFan(Graphics g, bool square = false)
    {
        Backdrop(g, square);
        using var white = new SolidBrush(White);

        for (var i = 0; i < 3; i++)
        {
            var state = g.Save();
            g.TranslateTransform(512, 512);
            g.RotateTransform(i * 120f);

            // 一片扇叶：胶囊形叶片 + 轻微后掠角，比纯椭圆更有“扇叶”感，16px 也不糊
            g.RotateTransform(-16f);
            using var blade = Rounded(new RectangleF(-135, -478, 270, 420), 135);
            g.FillPath(white, blade);
            g.Restore(state);
        }

        Disc(g, 512, 512, 128, White);
        Disc(g, 512, 512, 58, AccentDark);
    }

    // ---------------- B：转速表 ----------------

    private static void DrawGauge(Graphics g)
    {
        Backdrop(g, false);

        // 轨道（半透明白）
        using var track = new Pen(Color.FromArgb(80, 255, 255, 255), 132f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawArc(track, 200, 200, 624, 624, 150, 240);

        // 已用区间（纯白）：60%
        using var active = new Pen(White, 132f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(active, 200, 200, 624, 624, 150, 144);

        // 刻度（深色小圆点，坐在白色弧上）
        for (var i = 0; i <= 4; i++)
        {
            var angle = (150 + 240 * i / 4.0) * Math.PI / 180.0;
            var cx = 512 + (float)Math.Cos(angle) * 312;
            var cy = 512 + (float)Math.Sin(angle) * 312;
            Disc(g, cx, cy, 22, AccentDark);
        }

        // 指针（深色，粗三角），指向 60% 位置
        var state = g.Save();
        g.TranslateTransform(512, 512);
        g.RotateTransform(294f);
        using var needle = new SolidBrush(AccentDark);
        g.FillPolygon(needle, [new PointF(-52, 60), new PointF(52, 60), new PointF(0, -300)]);
        g.Restore(state);

        Disc(g, 512, 512, 108, AccentDark);
        Disc(g, 512, 512, 46, White);
    }

    // ---------------- C：盾牌 + 三条档位 ----------------

    private static void DrawShield(Graphics g)
    {
        Backdrop(g, false);

        using var shield = new GraphicsPath();
        const float left = 196, right = 828, top = 132;
        shield.AddArc(left, top, right - left, 260, 180, 180);   // 顶部圆角
        shield.AddLine(right, top + 130, right, 520);
        shield.AddBezier(right, 520, right, 700, 660, 820, 512, 900);
        shield.AddBezier(512, 900, 364, 820, left, 700, left, 520);
        shield.AddLine(left, top + 130, left, top + 130);
        shield.CloseFigure();

        using var white = new SolidBrush(White);
        g.FillPath(white, shield);

        // 内部三根档位柱（挖空成底色）
        using var cut = new SolidBrush(AccentDark);
        float[] heights = [150, 260, 370];
        for (var i = 0; i < 3; i++)
        {
            var h = heights[i];
            var x = 330 + i * 140;
            using var bar = Rounded(new RectangleF(x, 690 - h, 96, h), 48);
            g.FillPath(cut, bar);
        }
    }

    // ---------------- D / G：档位柱 ----------------

    private static void DrawBars(Graphics g, bool square = false)
    {
        Backdrop(g, square);
        using var white = new SolidBrush(White);

        float[] heights = [280, 450, 620];
        for (var i = 0; i < 3; i++)
        {
            var h = heights[i];
            var x = 208 + i * 208;
            using var bar = Rounded(new RectangleF(x, 726 - h, 168, h), 84);
            g.FillPath(white, bar);
        }
    }

    // ---------------- E：字母 P ----------------

    private static void DrawLetterP(Graphics g)
    {
        Backdrop(g, false);
        using var white = new SolidBrush(White);

        using var font = new Font("Arial Black", 470f, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString("P", font);
        g.DrawString("P", font, white, 250 - size.Width / 2, 512 - size.Height / 2);

        // 右侧三条风速线（越往下越短，代表档位）
        float[] lens = [330, 250, 170];
        for (var i = 0; i < 3; i++)
        {
            var y = 372 + i * 140;
            using var pen = new Pen(White, 74f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 566, y, 566 + lens[i], y);
        }
    }

    // ---------------- F：气流环 ----------------

    private static void DrawRing(Graphics g)
    {
        Backdrop(g, false);

        using var pen = new Pen(White, 148f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, 236, 236, 552, 552, -64, 296);

        Disc(g, 512, 512, 118, White);
        Disc(g, 512, 512, 52, AccentDark);
    }

    // ---------------- 对比图 ----------------

    private static Bitmap BuildSheet(List<Concept> concepts)
    {
        const int cellW = 300, cellH = 340, cols = 4;
        var rows = (concepts.Count + cols - 1) / cols;
        var width = cellW * cols;
        var height = cellH * rows + 78;

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using (var bg = new SolidBrush(Color.FromArgb(0xF3, 0xF4, 0xF6)))
        {
            g.FillRectangle(bg, 0, 0, width, height);
        }

        using var titleFont = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelFont = new Font("Microsoft YaHei UI", 17f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.FromArgb(0x22, 0x26, 0x2B));
        using var subtitleBrush = new SolidBrush(Color.FromArgb(0x6B, 0x72, 0x80));

        g.DrawString("MiniProLite 图标候选（上排浅色任务栏 / 下排深色任务栏；右下角是 32px 与 16px 实际大小）", titleFont, titleBrush, 16, 14);
        g.DrawString("每套都含 .ico（16/20/24/32/40/48/64/128/256）与 256 PNG", labelFont, subtitleBrush, 16, 46);

        for (var i = 0; i < concepts.Count; i++)
        {
            var c = concepts[i];
            var col = i % cols;
            var row = i / cols;
            var x = col * cellW;
            var y = row * cellH + 78;

            using var light = new SolidBrush(Color.White);
            g.FillRectangle(light, x + 8, y + 4, cellW - 16, 146);
            using var darkBg = new SolidBrush(Color.FromArgb(0x1A, 0x1F, 0x27));
            g.FillRectangle(darkBg, x + 8, y + 150, cellW - 16, 106);

            using var icon = Render(c.Draw);
            using var big = Resize(icon, 120);
            g.DrawImage(big, x + 20, y + 16, 120, 120);
            using var mid = Resize(icon, 32);
            g.DrawImage(mid, x + 168, y + 26, 32, 32);
            using var small = Resize(icon, 16);
            g.DrawImage(small, x + 168, y + 74, 16, 16);
            using var dk = Resize(icon, 84);
            g.DrawImage(dk, x + 20, y + 160, 84, 84);

            using var nameBrush = new SolidBrush(Color.FromArgb(0x22, 0x26, 0x2B));
            g.DrawString($"{c.Key}  {c.Name}", labelFont, nameBrush, x + 12, y + 272);
        }

        return bmp;
    }
}
