using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;
using SixLabors.ImageSharp.PixelFormats;

namespace MeshViewer;

/// <summary>
/// Картинка модели → текстура в видеокарте. Декодирует png, jpeg и webp (кит 3dassets хранит текстуры
/// в webp) и считает мип-уровни: без них текстура рябит на удалении.
/// </summary>
public static class Textures
{
    public static Texture2D Upload(GraphicsDevice device, Image image)
    {
        using var decoded = SixLabors.ImageSharp.Image.Load<Rgba32>(image.Data);
        var pixels = new Rgba32[decoded.Width * decoded.Height];
        decoded.CopyPixelDataTo(pixels);

        int width = decoded.Width, height = decoded.Height;
        var texture = new Texture2D(device, width, height, true, SurfaceFormat.Color);
        byte[] level = MemoryMarshal.AsBytes<Rgba32>(pixels).ToArray();
        for (int mip = 0; mip < texture.LevelCount; mip++)
        {
            if (mip > 0)
            {
                level = Halve(level, width, height);
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            texture.SetData(mip, null, level, 0, level.Length);
        }
        return texture;
    }

    /// <summary>
    /// Следующий мип-уровень: среднее четвёрок текселей. Усредняем в premultiplied-альфе и возвращаем
    /// обычную — иначе прозрачные края темнеют.
    /// </summary>
    static byte[] Halve(byte[] source, int width, int height)
    {
        int halfWidth = Math.Max(1, width / 2), halfHeight = Math.Max(1, height / 2);
        var target = new byte[halfWidth * halfHeight * 4];
        for (int y = 0; y < halfHeight; y++)
            for (int x = 0; x < halfWidth; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                int count = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = x * 2 + dx, sy = y * 2 + dy;
                        if (sx >= width || sy >= height) continue;
                        int at = (sy * width + sx) * 4;
                        float alpha = source[at + 3] / 255f;
                        r += source[at] * alpha;
                        g += source[at + 1] * alpha;
                        b += source[at + 2] * alpha;
                        a += alpha;
                        count++;
                    }
                r /= count;
                g /= count;
                b /= count;
                a /= count;
                if (a > 0)
                {
                    r /= a;
                    g /= a;
                    b /= a;
                }
                int to = (y * halfWidth + x) * 4;
                target[to] = Byte(r);
                target[to + 1] = Byte(g);
                target[to + 2] = Byte(b);
                target[to + 3] = Byte(a * 255f);
            }
        return target;
    }

    static byte Byte(float value) => (byte)Math.Clamp(value + 0.5f, 0f, 255f);
}
