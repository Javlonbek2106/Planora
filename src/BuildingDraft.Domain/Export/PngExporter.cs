using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using BuildingDraft.Domain.Geometry;

namespace BuildingDraft.Domain.Export;

// Raster preview of one floor, written without any native or font dependency: a scanline polygon fill with
// 2x supersampling, and a tiny built-in digit font. Numbers (areas, wall lengths) are drawn; room names are not,
// because they need a real font. Use SVG or PDF when names matter.
public sealed class PngExporter : IGeometryExporter
{
    const int Width = 1600;
    const int MaxHeight = 2400;
    const int Margin = 110;
    const int Supersample = 2;

    static readonly (byte R, byte G, byte B) Background = (255, 255, 255);
    static readonly (byte R, byte G, byte B) RoomFill = (232, 238, 244);
    static readonly (byte R, byte G, byte B) OpenAreaFill = (238, 246, 232);
    static readonly (byte R, byte G, byte B) WallFill = (58, 58, 58);
    static readonly (byte R, byte G, byte B) DoorStroke = (192, 57, 43);
    static readonly (byte R, byte G, byte B) WindowStroke = (41, 128, 185);
    static readonly (byte R, byte G, byte B) LabelColor = (34, 34, 34);
    static readonly (byte R, byte G, byte B) DimensionColor = (110, 110, 110);

    public ExportFormat Format => ExportFormat.Png;
    public string ContentType => "image/png";
    public string Extension => "png";

    public byte[] Export(ExportContext context)
    {
        var plan = PlanDrawing.Build(context.Model, context.ResolvePlanFloor().Id);
        var planWidth = plan.Max.X - plan.Min.X;
        var planHeight = plan.Max.Y - plan.Min.Y;
        var scale = Math.Min((Width - 2.0 * Margin) / planWidth, (MaxHeight - 2.0 * Margin) / planHeight);
        var height = (int)Math.Ceiling(planHeight * scale + 2 * Margin);

        var canvas = new Canvas(Width * Supersample, height * Supersample, Background);
        (double X, double Y) Px(Vec2 p) => (((p.X - plan.Min.X) * scale + Margin) * Supersample, ((plan.Max.Y - p.Y) * scale + Margin) * Supersample);
        IEnumerable<(double X, double Y)> Px2(IEnumerable<Vec2> points) => points.Select(Px);

        foreach (var room in plan.Rooms)
            canvas.Fill(Px2(room.Outline), room.AreaType == RoomAreaType.Enclosed ? RoomFill : OpenAreaFill);
        foreach (var wall in plan.Walls)
            canvas.Fill(Px2(wall.Outline), WallFill);

        var stroke = 2.5 * Supersample;
        foreach (var opening in plan.Openings)
        {
            var color = opening.Kind == OpeningKind.Door ? DoorStroke : WindowStroke;
            var o = opening.Outline.Select(Px).ToArray();
            canvas.Fill(o, color);
            canvas.Fill(Inset(o, stroke), Background);
        }

        var glyph = 3 * Supersample;
        foreach (var room in plan.Rooms)
            canvas.Text($"{room.AreaM2.ToString("0.00", CultureInfo.InvariantCulture)} m2", Px(room.Label), glyph * 1.4, LabelColor);
        foreach (var wall in plan.Walls)
        {
            var text = ((int)Math.Round(wall.LengthMm)).ToString(CultureInfo.InvariantCulture);
            var at = Px(wall.Label);

            // Push the label outwards by half of its own extent in that direction, so it never touches the wall.
            var (dx, dy) = (wall.Label.X - wall.Mid.X, -(wall.Label.Y - wall.Mid.Y));
            var length = Math.Sqrt(dx * dx + dy * dy);
            var extra = (Math.Abs(dx / length) * text.Length * 4 * glyph + Math.Abs(dy / length) * 5 * glyph) / 2;
            canvas.Text(text, (at.X + dx / length * extra, at.Y + dy / length * extra), glyph, DimensionColor);
        }

        return PngWriter.Encode(canvas.Downsample(Supersample), Width, height);
    }

    // Shrinks a strip (corners A, B, C, D around its long axis) by the stroke width on every side.
    static (double X, double Y)[] Inset((double X, double Y)[] q, double s)
    {
        (double X, double Y) Norm((double X, double Y) a, (double X, double Y) b)
        {
            var (dx, dy) = (b.X - a.X, b.Y - a.Y);
            var len = Math.Sqrt(dx * dx + dy * dy);
            return len < 1e-9 ? (0, 0) : (dx / len, dy / len);
        }

        var along = Norm(q[0], q[1]);
        var across = Norm(q[3], q[0]);
        (double X, double Y) Move((double X, double Y) p, double a, double c) => (p.X + along.X * a + across.X * c, p.Y + along.Y * a + across.Y * c);
        return [Move(q[0], s, -s), Move(q[1], -s, -s), Move(q[2], -s, s), Move(q[3], s, s)];
    }

    sealed class Canvas
    {
        readonly int width;
        readonly int height;
        readonly byte[] rgb;

        public Canvas(int width, int height, (byte R, byte G, byte B) background)
        {
            (this.width, this.height) = (width, height);
            rgb = new byte[width * height * 3];
            for (var i = 0; i < rgb.Length; i += 3)
                (rgb[i], rgb[i + 1], rgb[i + 2]) = background;
        }

        // Even-odd scanline fill sampled at pixel centres.
        public void Fill(IEnumerable<(double X, double Y)> polygon, (byte R, byte G, byte B) color)
        {
            var p = polygon.ToArray();
            if (p.Length < 3)
                return;

            var top = Math.Max(0, (int)Math.Floor(p.Min(v => v.Y)));
            var bottom = Math.Min(height - 1, (int)Math.Ceiling(p.Max(v => v.Y)));
            var crossings = new List<double>();
            for (var y = top; y <= bottom; y++)
            {
                crossings.Clear();
                var sampleY = y + 0.5;
                for (var i = 0; i < p.Length; i++)
                {
                    var (a, b) = (p[i], p[(i + 1) % p.Length]);
                    if ((a.Y <= sampleY && b.Y > sampleY) || (b.Y <= sampleY && a.Y > sampleY))
                        crossings.Add(a.X + (sampleY - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }

                crossings.Sort();
                for (var i = 0; i + 1 < crossings.Count; i += 2)
                {
                    var from = Math.Max(0, (int)Math.Ceiling(crossings[i] - 0.5));
                    var to = Math.Min(width - 1, (int)Math.Ceiling(crossings[i + 1] - 0.5) - 1);
                    for (var x = from; x <= to; x++)
                        Set(x, y, color);
                }
            }
        }

        public void Text(string text, (double X, double Y) centre, double pixel, (byte R, byte G, byte B) color)
        {
            var advance = 4 * pixel;
            var x0 = centre.X - text.Length * advance / 2;
            var y0 = centre.Y - 2.5 * pixel;
            for (var c = 0; c < text.Length; c++)
            {
                if (!Font.TryGetValue(text[c], out var rows))
                    continue;
                for (var row = 0; row < 5; row++)
                    for (var col = 0; col < 3; col++)
                        if ((rows[row] >> (2 - col) & 1) == 1)
                            Fill([(x0 + c * advance + col * pixel, y0 + row * pixel), (x0 + c * advance + (col + 1) * pixel, y0 + row * pixel),
                                (x0 + c * advance + (col + 1) * pixel, y0 + (row + 1) * pixel), (x0 + c * advance + col * pixel, y0 + (row + 1) * pixel)], color);
            }
        }

        public byte[] Downsample(int factor)
        {
            var (w, h) = (width / factor, height / factor);
            var result = new byte[w * h * 3];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    for (var ch = 0; ch < 3; ch++)
                    {
                        var sum = 0;
                        for (var dy = 0; dy < factor; dy++)
                            for (var dx = 0; dx < factor; dx++)
                                sum += rgb[((y * factor + dy) * width + x * factor + dx) * 3 + ch];
                        result[(y * w + x) * 3 + ch] = (byte)(sum / (factor * factor));
                    }
            return result;
        }

        void Set(int x, int y, (byte R, byte G, byte B) c)
        {
            var i = (y * width + x) * 3;
            (rgb[i], rgb[i + 1], rgb[i + 2]) = c;
        }
    }

    // 3 x 5 pixel glyphs, one bit per column (bit 2 = left).
    static readonly Dictionary<char, byte[]> Font = new()
    {
        ['0'] = [7, 5, 5, 5, 7], ['1'] = [2, 6, 2, 2, 7], ['2'] = [7, 1, 7, 4, 7], ['3'] = [7, 1, 7, 1, 7], ['4'] = [5, 5, 7, 1, 1],
        ['5'] = [7, 4, 7, 1, 7], ['6'] = [7, 4, 7, 5, 7], ['7'] = [7, 1, 1, 1, 1], ['8'] = [7, 5, 7, 5, 7], ['9'] = [7, 5, 7, 1, 7],
        ['.'] = [0, 0, 0, 0, 2], ['m'] = [0, 0, 7, 7, 5], [' '] = [0, 0, 0, 0, 0]
    };
}

static class PngWriter
{
    public static byte[] Encode(byte[] rgb, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolour RGB
        Chunk(output, "IHDR", header);

        using var scanlines = new MemoryStream();
        using (var zlib = new ZLibStream(scanlines, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // filter: none
                zlib.Write(rgb, y * width * 3, width * 3);
            }

        Chunk(output, "IDAT", scanlines.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    static void Chunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body.AsSpan(4));
        output.Write(body);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(body));
        output.Write(crc);
    }
}

static class Crc32
{
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
