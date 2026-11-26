using System.IO.Compression;
using System.Text;
using Microsoft.Xna.Framework.Graphics;

namespace MeshViewer;

/// <summary>
/// Снимает задний буфер в PNG минимальным кодером — без платформенных средств MonoGame.
/// На DesktopGL GetBackBufferData отдаёт строки сверху вниз, переворота не нужно.
/// </summary>
public static class Shot
{
    public static void Save(GraphicsDevice device, string path)
    {
        PresentationParameters parameters = device.PresentationParameters;
        int width = parameters.BackBufferWidth, height = parameters.BackBufferHeight;
        var rgba = new byte[width * height * 4];
        device.GetBackBufferData(rgba);

        var raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
            Array.Copy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);   // перед каждой строкой — байт фильтра 0

        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Chunk(png, "IHDR", [.. BigEndian(width), .. BigEndian(height), 8, 6, 0, 0, 0]);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        File.WriteAllBytes(path, png.ToArray());
    }

    static void Chunk(Stream png, string type, byte[] data)
    {
        byte[] name = Encoding.ASCII.GetBytes(type);
        png.Write(BigEndian(data.Length));
        png.Write(name);
        png.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in name.Concat(data))
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = crc >> 1 ^ (0xEDB88320u & (0u - (crc & 1)));
        }
        png.Write(BigEndian((int)~crc));
    }

    static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
