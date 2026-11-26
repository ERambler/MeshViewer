"""Синтетические модели для проверок: кубы OBJ (+MTL) и GLB с асимметричной текстурой-меткой.

Буква F, синяя верхняя строка и красный левый столбец метки одним кадром выявляют намотку, зеркало и ориентацию
текстурных координат. В GLB три варианта: A — interleaved-буфер позиции/нормали/uv и узел с матрицей,
B — поворот кватернионом и материал на каждую грань, C — полосы без нормалей и индексов.

    python tests/fixtures/make.py
"""
import json
import math
import os
import struct
import sys
import zlib

OUT = os.path.dirname(os.path.abspath(__file__))
S = 64


def chunk(kind, data):
    body = kind + data
    return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xffffffff)


def png(pixels):
    raw = b''.join(b'\x00' + b''.join(bytes(p) for p in row) for row in pixels)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', S, S, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


# метка: шахматка, синяя верхняя строка, красный левый столбец, зелёная буква F
px = [[(255, 255, 255, 255) if ((x // 8) + (y // 8)) % 2 == 0 else (200, 200, 200, 255) for x in range(S)] for y in range(S)]
px[0] = [(0, 0, 255, 255)] * S
for y in range(S):
    px[y][0] = (255, 0, 0, 255)
for y in range(10, 55):
    px[y][20:27] = [(0, 160, 0, 255)] * 7
for x in range(20, 47):
    for y in range(10, 16):
        px[y][x] = (0, 160, 0, 255)
for x in range(20, 41):
    for y in range(30, 36):
        px[y][x] = (0, 160, 0, 255)
marker = png(px)
with open(os.path.join(OUT, 'marker.png'), 'wb') as handle:
    handle.write(marker)

P = [(0, 0, 0), (1, 0, 0), (1, 0, 1), (0, 0, 1), (0, 1, 0), (1, 1, 0), (1, 1, 1), (0, 1, 1)]
# грань, углы [BL, BR, TR, TL] снаружи (против часовой), цвет без текстуры
FACES = [('bottom', [0, 1, 2, 3], (0, 0, 0)), ('top', [7, 6, 5, 4], (1, 1, 1)), ('front', [3, 2, 6, 7], (0, 0, 1)),
         ('right', [2, 1, 5, 6], (1, 0, 0)), ('back', [1, 0, 4, 5], (1, 1, 0)), ('left', [0, 3, 7, 4], (0, 1, 0))]
UV_G = [(0, 1), (1, 1), (1, 0), (0, 0)]     # glTF: (0,0) — верхний левый угол картинки
UV_O = [(0, 0), (1, 0), (1, 1), (0, 1)]     # OBJ: (0,0) — нижний левый


def face_verts(corners, uvs, with_normal=True):
    a, b, c = (P[i] for i in corners[:3])
    u = [b[k] - a[k] for k in range(3)]
    v = [c[k] - a[k] for k in range(3)]
    n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
    return [P[i] + (n if with_normal else ()) + uv for i, uv in zip(corners, uvs)]


def quads_indices(nquads):
    r = []
    for q in range(nquads):
        r += [q * 4, q * 4 + 1, q * 4 + 2, q * 4, q * 4 + 2, q * 4 + 3]
    return r


# ---------------- OBJ: цветной куб (материал на грань) и текстурный ----------------
lines = ['# synthetic cubes: coloured (per-face materials) and textured (marker.png)', 'mtllib cube.mtl']
for dx in (0.0, 2.5):
    for v in P:
        lines.append('v %g %g %g' % (v[0] + dx, v[1], v[2]))
for t in UV_O:
    lines.append('vt %g %g' % t)
lines.append('o colours')
for name, c, _ in FACES:
    lines += ['usemtl ' + name, 'f ' + ' '.join('%d/%d' % (i + 1, j + 1) for j, i in enumerate(c))]
lines.append('o textured')
for name, c, _ in FACES:
    lines += ['usemtl marker', 'f ' + ' '.join('%d/%d' % (i + 9, j + 1) for j, i in enumerate(c))]
with open(os.path.join(OUT, 'cube.obj'), 'w', newline='\n') as handle:
    handle.write('\n'.join(lines) + '\n')
mtl = ['newmtl marker', 'Kd 1 1 1', 'd 1', 'map_Kd marker.png']
for name, c, rgb in FACES:
    mtl += ['newmtl ' + name, 'Kd %g %g %g' % rgb, 'd 1']
with open(os.path.join(OUT, 'cube.mtl'), 'w', newline='\n') as handle:
    handle.write('\n'.join(mtl) + '\n')

# ---------------- GLB ----------------
bin_ = bytearray()
views, accs, meshes = [], [], []


def add(data):
    bin_.extend(b'\0' * ((4 - len(bin_) % 4) % 4))
    offset = len(bin_)
    bin_.extend(data)
    return offset, len(data)


def part(verts, indices, material, mode=4):
    flat = [c for v in verts for c in v]
    with_normal = len(verts[0]) == 8
    stride = (5 if with_normal else 3) * 4
    offset, length = add(struct.pack('<%df' % len(flat), *flat))
    views.append({'buffer': 0, 'byteOffset': offset, 'byteLength': length, 'byteStride': stride, 'target': 34962})
    a = len(accs)
    n = len(verts)
    accs.append({'bufferView': len(views) - 1, 'byteOffset': 0, 'componentType': 5126, 'count': n, 'type': 'VEC3'})
    attrs = {'POSITION': a}
    if with_normal:
        accs.append({'bufferView': len(views) - 1, 'byteOffset': 12, 'componentType': 5126, 'count': n, 'type': 'VEC3'})
        attrs['NORMAL'] = a + 1
    accs.append({'bufferView': len(views) - 1, 'byteOffset': 12 + (12 if with_normal else 0), 'componentType': 5126, 'count': n, 'type': 'VEC2'})
    attrs['TEXCOORD_0'] = len(accs) - 1
    prim = {'attributes': attrs, 'material': material, 'mode': mode}
    if mode == 4:
        offset, length = add(struct.pack('<%dH' % len(indices), *indices))
        views.append({'buffer': 0, 'byteOffset': offset, 'byteLength': length, 'target': 34963})
        accs.append({'bufferView': len(views) - 1, 'componentType': 5123, 'count': len(indices), 'type': 'SCALAR'})
        prim['indices'] = len(accs) - 1
    return {'primitives': [prim]}


# A: текстурный куб, interleaved position + normal + uv
meshes.append(part([v for f in FACES for v in face_verts(f[1], UV_G)], quads_indices(6), 0))
# B: цветной куб, примитив на материал; узел-поворот на 90° вокруг Y
for m, f in enumerate(FACES):
    meshes.append(part(face_verts(f[1], UV_G), quads_indices(1), m + 1))
# C: полосы без нормалей и индексов — нормали обязан посчитать загрузчик
for f in FACES:
    corners = [f[1][0], f[1][1], f[1][3], f[1][2]]            # BL, BR, TL, TR — обход полосы
    meshes.append(part(face_verts(corners, [UV_G[0], UV_G[1], UV_G[3], UV_G[2]], False), None, 0, mode=5))

to, tl = add(marker)
views.append({'buffer': 0, 'byteOffset': to, 'byteLength': tl})

c = math.cos(math.radians(45))
s = math.sin(math.radians(45))
nodes = [
    {'name': 'A_pivot', 'matrix': [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1], 'children': [1]},
    {'name': 'A_body', 'mesh': 0},
    {'name': 'B_pivot', 'translation': [2.5, 0.5, 0], 'rotation': [0, s, 0, c], 'children': [3]},
    {'name': 'B_body', 'translation': [0, -0.5, 0], 'children': [4, 5, 6, 7, 8, 9]},
]
nodes += [{'name': 'B_%d' % k, 'mesh': k + 1} for k in range(6)]
nodes.append({'name': 'C', 'translation': [-2.5, 0, 0], 'children': list(range(11, 17))})
nodes += [{'name': 'C_%d' % k, 'mesh': k + 7} for k in range(6)]
materials = [{'name': 'marker', 'pbrMetallicRoughness': {'baseColorTexture': {'index': 0}}}]
materials += [{'name': f[0], 'pbrMetallicRoughness': {'baseColorFactor': [*f[2], 1]}} for f in FACES]
gl = {'asset': {'version': '2.0', 'generator': 'MeshViewer fixtures'}, 'scene': 0, 'scenes': [{'nodes': [0, 2, 10]}],
      'nodes': nodes, 'meshes': meshes, 'materials': materials, 'textures': [{'sampler': 0, 'source': 0}],
      'samplers': [{'wrapS': 10497, 'wrapT': 10497, 'magFilter': 9729, 'minFilter': 9987}],
      'images': [{'name': 'marker', 'bufferView': len(views) - 1, 'mimeType': 'image/png'}],
      'accessors': accs, 'bufferViews': views}
bin_.extend(b'\0' * ((4 - len(bin_) % 4) % 4))
gl['buffers'] = [{'byteLength': len(bin_)}]
js = json.dumps(gl, separators=(',', ':')).encode('utf-8')
js += b' ' * ((4 - len(js) % 4) % 4)
glb = (b'glTF' + struct.pack('<II', 2, 12 + 8 + len(js) + 8 + len(bin_))
       + struct.pack('<I4s', len(js), b'JSON') + js + struct.pack('<I4s', len(bin_), b'BIN\x00') + bytes(bin_))
with open(os.path.join(OUT, 'cubes.glb'), 'wb') as handle:
    handle.write(glb)
print('fixtures:', sorted(f for f in os.listdir(OUT) if f != os.path.basename(__file__)))
