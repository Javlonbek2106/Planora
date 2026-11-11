using System.Buffers.Binary;
using System.IO.Compression;
using BuildingDraft.Domain.Export;
using BuildingDraft.Domain.Geometry;
using BuildingDraft.Domain.Geometry.Commands;
using BuildingDraft.Domain.Projects;

namespace BuildingDraft.Domain.Tests;

public class PngExportTests
{
    static readonly Project Project = Project.Create(Guid.NewGuid(), "Flat 12", null, null, LengthUnit.M, DateTimeOffset.UtcNow);
    static readonly Floor Ground = Project.AddFloor(1, 0, 2700, null, DateTimeOffset.UtcNow);

    static byte[] Export(RoomFixture f) => new PngExporter().Export(new(f.Model, Project.Floors, null, "Flat 12", 3));

    static RoomFixture Room(double[][]? corners = null) => new(floorId: Ground.Id, corners: corners);

    // An independent decoder: checks the signature, every chunk CRC, and unpacks the pixels.
    static (int Width, int Height, byte[] Rgb, List<string> Chunks) Decode(byte[] png)
    {
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        var chunks = new List<string>();
        var idat = new MemoryStream();
        int width = 0, height = 0, offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var body = png.AsSpan(offset + 4, 4 + length);
            Assert.Equal(Crc(body), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length)));
            chunks.Add(type);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 8));
                height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 12));
                Assert.Equal(8, png[offset + 16]);
                Assert.Equal(2, png[offset + 17]);
            }
            if (type == "IDAT")
                idat.Write(png, offset + 8, length);
            offset += 12 + length;
        }

        idat.Position = 0;
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
            zlib.CopyTo(raw);
        var data = raw.ToArray();
        Assert.Equal(height * (1 + width * 3), data.Length);

        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, data[y * (1 + width * 3)]);
            Array.Copy(data, y * (1 + width * 3) + 1, rgb, y * width * 3, width * 3);
        }
        return (width, height, rgb, chunks);
    }

    static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    static int Count(byte[] rgb, (byte R, byte G, byte B) color) =>
        Enumerable.Range(0, rgb.Length / 3).Count(i => rgb[i * 3] == color.R && rgb[i * 3 + 1] == color.G && rgb[i * 3 + 2] == color.B);

    [Fact]
    public void Output_is_a_well_formed_png_with_the_expected_chunks()
    {
        var (width, height, _, chunks) = Decode(Export(Room()));

        Assert.Equal(1600, width);
        Assert.Equal((int)Math.Ceiling(3200 * (1600 - 220) / 4200.0 + 220), height);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks);
    }

    [Fact]
    public void The_room_fill_covers_the_inner_area_at_the_drawing_scale()
    {
        var (width, height, rgb, _) = Decode(Export(Room()));

        // The plan is 4200 mm wide including the wall strips, drawn into 1600 - 2 * 110 pixels.
        var scale = (1600 - 220) / 4200.0;
        var expected = 3800 * 2800 * scale * scale;
        var roomPixels = Count(rgb, (232, 238, 244));

        Assert.InRange(roomPixels / expected, 0.97, 1.01);
        Assert.Equal(((byte)255, (byte)255, (byte)255), (rgb[0], rgb[1], rgb[2]));
        Assert.True(Count(rgb, (58, 58, 58)) > 20_000, "walls should be drawn");
    }

    [Fact]
    public void Doors_and_windows_get_their_own_stroke_colours()
    {
        var f = Room();
        f.Run(new AttachOpening(Guid.NewGuid(), f.Walls[0], OpeningKind.Door, 500, 900, 2100));
        f.Run(new AttachOpening(Guid.NewGuid(), f.Walls[2], OpeningKind.Window, 1000, 1200, 1200, 900));

        var (_, _, rgb, _) = Decode(Export(f));

        Assert.True(Count(rgb, (192, 57, 43)) > 100, "door stroke");
        Assert.True(Count(rgb, (41, 128, 185)) > 100, "window stroke");
    }

    [Fact]
    public void A_very_tall_plan_is_scaled_to_fit_the_maximum_height()
    {
        var (width, height, _, _) = Decode(Export(Room([[0, 0], [1000, 0], [1000, 20000], [0, 20000]])));

        Assert.Equal(1600, width);
        Assert.InRange(height, 2300, 2400);
    }

    [Fact]
    public void The_same_model_always_produces_the_same_bytes()
    {
        Assert.Equal(Export(Room()), Export(Room()));
    }

    [Fact]
    public void Room_names_with_any_characters_do_not_break_the_png()
    {
        var f = Room();
        f.Run(new DeleteRoom(f.RoomId));
        f.Run(new CreateRoom(Guid.NewGuid(), f.FloorId, "Yotoqxona o‘g‘il 你好", f.Walls));

        Assert.NotEmpty(Decode(Export(f)).Rgb);
    }
}
