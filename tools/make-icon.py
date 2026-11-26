"""Иконка MeshViewer: каркасный куб со знаком воспроизведения — 3D-модели и анимация.

    python tools/make-icon.py [лист-проверки.png]

Пишет Icon.ico (16…256 — иконка exe и файлов), Icon.bmp (64 — иконка окна) и docs/icon.png (256).
Без зависимостей: каждая линия и грань — расстояние со знаком, сглаживание аналитическое.
"""
import math
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
YAW, PITCH = math.radians(34), math.radians(24)       # ракурс куба: сверху, спереди и слева
SIZES = [16, 24, 32, 48, 64, 128, 256]


def chunk(kind, data):
    body = kind + data
    return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xffffffff)


def rgba(pixel):
    """Цвет канвы (альфа 0…1) — в байты RGBA."""
    r, g, b, a = pixel
    return int(round(r)), int(round(g)), int(round(b)), int(round(a * 255))


def png(pixels):
    height, width = len(pixels), len(pixels[0])
    raw = b''.join(b'\x00' + bytes(channel for pixel in row for channel in rgba(pixel)) for row in pixels)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


def bgra_rows(pixels):
    return [b''.join(struct.pack('<4B', b, g, r, a) for r, g, b, a in (rgba(p) for p in reversed(row))) for row in pixels]


def bmp_frame(size, pixels):
    """Кадр внутри .ico: BITMAPINFOHEADER, пиксели снизу вверх, пустая маска."""
    mask_row = ((size + 31) // 32) * 4
    rows = b''.join(bgra_rows(pixels))
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, len(rows) + mask_row * size, 0, 0, 0, 0)
    return header + rows + bytes(mask_row * size)


def bmp_file(pixels):
    rows = b''.join(bgra_rows(pixels))
    return (struct.pack('<2sIHHI', b'BM', 54 + len(rows), 0, 0, 54)
            + struct.pack('<IiiHHIIiiII', 40, len(pixels), len(pixels), 1, 32, 0, len(rows), 0, 0, 0, 0) + rows)


def ico(images):
    entries, body = b'', b''
    for size, data in images:
        entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), 6 + 16 * len(images) + len(body))
        body += data
    return struct.pack('<HHH', 0, 1, len(images)) + entries + body


def cube():
    """Вершины куба в проекции и его рёбра: видимые и скрытые."""
    vertices = []
    for x, y, z in [(-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1), (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]:
        x1 = x * math.cos(YAW) + z * math.sin(YAW)
        z1 = -x * math.sin(YAW) + z * math.cos(YAW)
        vertices.append((x1, y * math.cos(PITCH) - z1 * math.sin(PITCH), y * math.sin(PITCH) + z1 * math.cos(PITCH)))
    faces = [((0, 1, 2, 3), (0, 0, -1)), ((4, 5, 6, 7), (0, 0, 1)), ((0, 1, 5, 4), (0, -1, 0)),
             ((3, 2, 6, 7), (0, 1, 0)), ((1, 2, 6, 5), (1, 0, 0)), ((0, 3, 7, 4), (-1, 0, 0))]
    sides = {}
    for corners, normal in faces:
        x, y, z = normal
        z1 = -x * math.sin(YAW) + z * math.cos(YAW)
        visible = y * math.sin(PITCH) + z1 * math.cos(PITCH) > 1e-6      # грань смотрит на нас
        for k in range(4):
            sides.setdefault(tuple(sorted((corners[k], corners[(k + 1) % 4]))), []).append(visible)
    return vertices, [edge for edge, seen in sides.items() if any(seen)], [edge for edge, seen in sides.items() if not any(seen)]


def clamp(value):
    return max(0.0, min(1.0, value))


def segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = 0.0 if dx == dy == 0 else clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy))
    return math.hypot(px - ax - t * dx, py - ay - t * dy)


def triangle(px, py, a, b, c):
    """Расстояние со знаком до треугольника (внутри — отрицательное)."""
    ex, ey = b[0] - a[0], b[1] - a[1]
    fx, fy = a[0] - c[0], a[1] - c[1]
    orient = 1.0 if ex * fy - ey * fx >= 0 else -1.0
    best = None
    for p, q in ((a, b), (b, c), (c, a)):
        ex, ey = q[0] - p[0], q[1] - p[1]
        vx, vy = px - p[0], py - p[1]
        t = clamp((vx * ex + vy * ey) / (ex * ex + ey * ey))
        candidate = ((vx - ex * t) ** 2 + (vy - ey * t) ** 2, orient * (vx * ey - vy * ex))
        if best is None or candidate < best:
            best = candidate
    return -(best[0] ** 0.5) * (1.0 if best[1] >= 0 else -1.0)


def render(size):
    s = float(size)
    pixels = [[(0, 0, 0, 0)] * size for _ in range(size)]

    def paint(bounds, colour, distance, alpha=1.0):
        """Закрашивает фигуру по расстоянию со знаком (перекрытие пикселя — аналитическое)."""
        x0, y0, x1, y1 = bounds
        for y in range(max(0, int(y0)), min(size, int(y1) + 1)):
            for x in range(max(0, int(x0)), min(size, int(x1) + 1)):
                a = alpha * clamp(0.5 - distance(x + 0.5, y + 0.5))
                if a <= 0.002:
                    continue
                pr, pg, pb, pa = pixels[y][x]
                a = min(a, 1.0)
                out = a + pa * (1 - a)
                pixels[y][x] = (int(round((colour[0] * a + pr * pa * (1 - a)) / out)),
                                int(round((colour[1] * a + pg * pa * (1 - a)) / out)),
                                int(round((colour[2] * a + pb * pa * (1 - a)) / out)), out)

    def line(a, b, width, colour, alpha=1.0):
        reach = width / 2 + 1
        paint((min(a[0], b[0]) - reach, min(a[1], b[1]) - reach, max(a[0], b[0]) + reach, max(a[1], b[1]) + reach),
              colour, lambda px, py: segment(px, py, a[0], a[1], b[0], b[1]) - width / 2, alpha)

    # плитка — скруглённый квадрат: иконка читается и на светлом, и на тёмном рабочем столе
    half, radius = s / 2 - 0.03 * s, 0.21 * s

    def tile(px, py):
        qx = abs(px - s / 2) - (half - radius)
        qy = abs(py - s / 2) - (half - radius)
        return math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - radius

    paint((0, 0, size - 1, size - 1), (28, 41, 64), tile)

    # куб: скрытые рёбра тусклее, видимые — светлые
    vertices, visible, hidden = cube()
    scale, cx, cy = 0.225 * s, 0.455 * s, 0.45 * s

    def point(index):
        return cx + vertices[index][0] * scale, cy - vertices[index][1] * scale

    width = max(1.3, 0.055 * s)
    for a, b in hidden:
        line(point(a), point(b), width * 0.9, (52, 132, 168), 0.9)
    for a, b in visible:
        line(point(a), point(b), width, (150, 232, 255))

    # знак воспроизведения — анимация; кайма цвета плитки отделяет его от куба
    bx, by, r = 0.70 * s, 0.70 * s, 0.20 * s
    tip, top, bottom = (bx + r - 0.03 * s, by), (bx - 0.5 * r - 0.03 * s, by - 0.866 * r), (bx - 0.5 * r - 0.03 * s, by + 0.866 * r)
    paint((bx - r, by - r, bx + r, by + r), (20, 30, 48), lambda px, py: triangle(px, py, tip, top, bottom) - 0.055 * s)
    paint((bx - r, by - r, bx + r, by + r), (74, 222, 128), lambda px, py: triangle(px, py, tip, top, bottom) + 0.035 * s)
    return pixels


def sheet(frames):
    """Лист для проверки: все размеры в натуральную величину и мелкие увеличенные."""
    width = 16 + sum(SIZES) + 16 * len(SIZES) + 128 + 16 + 128 + 16
    canvas = [[(200, 200, 200, 1.0)] * width for _ in range(16 + 256 + 16)]
    x = 16
    for size, k in [(size, 1) for size in SIZES] + [(16, 8), (32, 4)]:
        frame, n = frames[size], len(frames[size])
        for y in range(n * k):
            for i in range(n * k):
                r, g, b, a = frame[y // k][i // k]
                pr, pg, pb, pa = canvas[16 + 256 - n * k + y][x + i]
                out = a + pa * (1 - a)
                canvas[16 + 256 - n * k + y][x + i] = ((r * a + pr * pa * (1 - a)) / out, (g * a + pg * pa * (1 - a)) / out,
                                                        (b * a + pb * pa * (1 - a)) / out, out)
        x += n * k + 16
    return canvas


def main():
    frames = {size: render(size) for size in SIZES}
    images = ([(size, bmp_frame(size, frames[size])) for size in SIZES if size <= 48]
              + [(size, png(frames[size])) for size in SIZES if size >= 64])
    os.makedirs(os.path.join(ROOT, 'docs'), exist_ok=True)
    for name, data in [('Icon.ico', ico(images)), ('Icon.bmp', bmp_file(frames[64])), ('docs/icon.png', png(frames[256]))]:
        with open(os.path.join(ROOT, name), 'wb') as handle:
            handle.write(data)
    print('ok:', ', '.join('%s %d bytes' % (name, os.path.getsize(os.path.join(ROOT, name))) for name in ('Icon.ico', 'Icon.bmp', 'docs/icon.png')))
    if len(sys.argv) > 1:
        with open(sys.argv[1], 'wb') as handle:
            handle.write(png(sheet(frames)))


if __name__ == '__main__':
    main()
