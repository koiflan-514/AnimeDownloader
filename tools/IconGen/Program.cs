using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

// 生成 AnimeDownloader 应用图标：
//   1. 绘制多尺寸（256/128/64/48/32/16）动漫风格图标（Win11 风格渐变背景 + 猫耳形象）
//   2. 每个尺寸导出 PNG 字节
//   3. 打包为多尺寸 ICO（PNG-compressed entries，Vista+ 支持）
// 用法: dotnet run --project tools/IconGen

var outputDir = args.Length > 0 ? args[0] : Path.Combine("src", "icons");
var outputPath = Path.Combine(outputDir, "AnimeDownloader.ico");
Directory.CreateDirectory(outputDir);

var sizes = new[] { 256, 128, 64, 48, 32, 16 };
var pngEntries = new List<(byte[] Data, int Size)>();

foreach (var size in sizes)
{
    using var bmp = DrawIcon(size);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    pngEntries.Add((ms.ToArray(), size));
    Console.WriteLine($"  {size}x{size} PNG: {ms.Length} bytes");
}

File.WriteAllBytes(outputPath, BuildIco(pngEntries));
Console.WriteLine($"Written: {Path.GetFullPath(outputPath)} ({new FileInfo(outputPath).Length} bytes)");

// 同时导出 64x64 PNG，供应用界面（侧栏 logo 等）使用
var pngPath = Path.Combine(outputDir, "AnimeDownloader.png");
using (var pngBmp = DrawIcon(64))
{
    pngBmp.Save(pngPath, ImageFormat.Png);
}
Console.WriteLine($"Written: {Path.GetFullPath(pngPath)}");

// ---------------- 绘制 ----------------

static Bitmap DrawIcon(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.Clear(Color.Transparent);

    var s = size / 256.0f; // 以 256 为基准的缩放

    // Win11 风格圆角背景 + 蓝紫渐变
    var bgRect = new RectangleF(0, 0, size, size);
    var bgPath = RoundedRect(bgRect, 56 * s);
    using var bgBrush = new LinearGradientBrush(
        bgRect,
        Color.FromArgb(255, 108, 99, 255),   // 顶：靛蓝
        Color.FromArgb(255, 244, 114, 182),  // 底：粉
        90f);
    g.FillPath(bgBrush, bgPath);

    // 顶部高光
    using var highlight = new LinearGradientBrush(
        new RectangleF(0, 0, size, size * 0.45f),
        Color.FromArgb(90, 255, 255, 255),
        Color.FromArgb(0, 255, 255, 255),
        90f);
    var highlightPath = RoundedRect(new RectangleF(0, 0, size, size), 56 * s);
    g.FillPath(highlight, highlightPath);

    // 猫耳（两个三角）
    var earLeft = new[] {
        new PointF(62 * s, 78 * s),
        new PointF(112 * s, 66 * s),
        new PointF(96 * s, 128 * s),
    };
    var earRight = new[] {
        new PointF(194 * s, 66 * s),
        new PointF(144 * s, 78 * s),
        new PointF(160 * s, 128 * s),
    };
    using (var earBrush = new SolidBrush(Color.FromArgb(255, 46, 30, 96)))
    {
        g.FillPolygon(earBrush, earLeft);
        g.FillPolygon(earBrush, earRight);
    }
    // 猫耳内衬（浅色）
    var innerLeft = new[] {
        new PointF(76 * s, 82 * s),
        new PointF(102 * s, 76 * s),
        new PointF(94 * s, 110 * s),
    };
    var innerRight = new[] {
        new PointF(180 * s, 76 * s),
        new PointF(154 * s, 82 * s),
        new PointF(162 * s, 110 * s),
    };
    using (var innerBrush = new SolidBrush(Color.FromArgb(255, 255, 158, 200)))
    {
        g.FillPolygon(innerBrush, innerLeft);
        g.FillPolygon(innerBrush, innerRight);
    }

    // 猫脸（白色圆角方形）
    var faceRect = new RectangleF(72 * s, 96 * s, 112 * s, 108 * s);
    using var faceBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
    using var facePath = RoundedRect(faceRect, 40 * s);
    g.FillPath(faceBrush, facePath);

    // 眼睛（两个深色椭圆）
    using var eyeBrush = new SolidBrush(Color.FromArgb(255, 36, 26, 66));
    g.FillEllipse(eyeBrush, new RectangleF(98 * s, 134 * s, 18 * s, 30 * s));
    g.FillEllipse(eyeBrush, new RectangleF(140 * s, 134 * s, 18 * s, 30 * s));

    // 眼睛高光
    using var shineBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
    g.FillEllipse(shineBrush, new RectangleF(102 * s, 138 * s, 6 * s, 8 * s));
    g.FillEllipse(shineBrush, new RectangleF(144 * s, 138 * s, 6 * s, 8 * s));

    // 嘴巴（小弧线）
    using var mouthPen = new Pen(Color.FromArgb(255, 36, 26, 66), 5 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    g.DrawArc(mouthPen, 112 * s, 150 * s, 32 * s, 22 * s, 20f, 140f);

    // 腮红
    using var blushBrush = new SolidBrush(Color.FromArgb(90, 255, 120, 170));
    g.FillEllipse(blushBrush, new RectangleF(80 * s, 162 * s, 18 * s, 9 * s));
    g.FillEllipse(blushBrush, new RectangleF(158 * s, 162 * s, 18 * s, 9 * s));

    return bmp;
}

// ---------------- 工具 ----------------

static GraphicsPath RoundedRect(RectangleF rect, float radius)
{
    var path = new GraphicsPath();
    var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
    path.AddArc(rect.X, rect.Y, d, d, 180, 90);
    path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
    path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
    path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

/// <summary>
/// 构建 ICO 文件：ICONDIR + 每条目 ICONDIRENTRY + PNG 数据。
/// PNG-compressed entries 自 Vista 起支持；Windows 10/11 会读取内部尺寸。
/// </summary>
static byte[] BuildIco(List<(byte[] Data, int Size)> entries)
{
    const int headerSize = 6;
    const int entrySize = 16;
    using var ms = new MemoryStream();

    // ICONDIR
    ms.WriteByte(0);
    ms.WriteByte(0);              // reserved
    ms.WriteByte(1);
    ms.WriteByte(0);              // type: icon
    WriteUInt16(ms, (ushort)entries.Count);

    var dataOffset = headerSize + entries.Count * entrySize;
    foreach (var (data, size) in entries)
    {
        // ICONDIRENTRY
        ms.WriteByte((byte)(size >= 256 ? 0 : size));  // width (0 = 256)
        ms.WriteByte((byte)(size >= 256 ? 0 : size));  // height
        ms.WriteByte(0);                               // colors
        ms.WriteByte(0);                               // reserved
        WriteUInt16(ms, 1);                            // planes
        WriteUInt16(ms, 32);                           // bit count
        WriteUInt32(ms, (uint)data.Length);            // size of data
        WriteUInt32(ms, (uint)dataOffset);             // offset
        dataOffset += data.Length;
    }

    foreach (var (data, _) in entries)
    {
        ms.Write(data, 0, data.Length);
    }

    return ms.ToArray();
}

static void WriteUInt16(MemoryStream ms, ushort value)
{
    ms.WriteByte((byte)(value & 0xFF));
    ms.WriteByte((byte)(value >> 8));
}

static void WriteUInt32(MemoryStream ms, uint value)
{
    ms.WriteByte((byte)(value & 0xFF));
    ms.WriteByte((byte)((value >> 8) & 0xFF));
    ms.WriteByte((byte)((value >> 16) & 0xFF));
    ms.WriteByte((byte)((value >> 24) & 0xFF));
}
