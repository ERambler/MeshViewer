using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace MeshViewer;

/// <summary>
/// Загрузчик GLB (glTF 2.0 в двоичном контейнере): геометрия, иерархия узлов, материалы, встроенные текстуры,
/// анимации. Координаты не преобразуются: glTF правосторонний, Y вверх, +Z — лицо модели. Понимает
/// interleaved-буферы, нормализованные целые (KHR_mesh_quantization) и webp-текстуры (EXT_texture_webp).
/// </summary>
public static class GlbLoader
{
    public static Model Load(string path) => new Glb(path).Build(Path.GetFileName(path));

    const int Byte = 5120, UnsignedByte = 5121, Short = 5122, UnsignedShort = 5123, UnsignedInt = 5125, Float = 5126;

    /// <summary>Прозрачность, которой показываем стекло (KHR_materials_transmission): его базовый цвет — не то, что видит глаз.</summary>
    const float GlassAlpha = 0.3f;

    private sealed class Glb
    {
        readonly JsonDocument _json;
        readonly byte[] _bin;
        readonly string _directory;
        readonly Image[] _images;
        readonly Material[] _materials;
        JsonElement Root => _json.RootElement;

        public Glb(string path)
        {
            _directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 20 || BitConverter.ToInt32(data, 0) != 0x46546C67)
                throw new InvalidDataException("Это не GLB: нет сигнатуры glTF");
            if (BitConverter.ToInt32(data, 4) != 2)
                throw new NotSupportedException($"Нужен glTF 2.0, в файле версия {BitConverter.ToInt32(data, 4)}");

            JsonDocument? json = null;
            byte[] bin = [];
            for (int at = 12; at + 8 <= data.Length;)
            {
                int length = BitConverter.ToInt32(data, at), type = BitConverter.ToInt32(data, at + 4);
                at += 8;
                if (length < 0 || at + length > data.Length) throw new InvalidDataException("Битый GLB: кусок выходит за пределы файла");
                if (type == 0x4E4F534A)
                {
                    json?.Dispose();
                    json = JsonDocument.Parse(Trim(data.AsMemory(at, length)));
                }
                else if (type == 0x004E4942) bin = data.AsMemory(at, length).ToArray();
                at += length;
            }
            _json = json ?? throw new InvalidDataException("Битый GLB: нет JSON-части");
            _bin = bin;
            _images = [.. Items(Get(Root, "images")).Select(ReadImage)];
            _materials = [.. Items(Get(Root, "materials")).Select(ReadMaterial)];
        }

        /// <summary>JSON дополнен пробелами (а некоторые писатели — нулями) до кратности четырём.</summary>
        static ReadOnlyMemory<byte> Trim(ReadOnlyMemory<byte> json)
        {
            int end = json.Length;
            while (end > 0 && json.Span[end - 1] is (byte)' ' or (byte)'\0' or (byte)'\n' or (byte)'\r' or (byte)'\t') end--;
            return json[..end];
        }

        public Model Build(string name)
        {
            var model = new Model { Name = name };
            foreach (Image image in _images) model.Images.Add(image);
            foreach (Material material in _materials) model.Materials.Add(material);

            JsonElement[] nodeJson = [.. Items(Get(Root, "nodes"))];
            Mesh[] meshes = [.. Items(Get(Root, "meshes")).Select(mesh => new Mesh { Name = Text(mesh, "name") })];
            Node[] nodes = [.. nodeJson.Select(node => ReadNode(node, meshes))];
            var parented = new bool[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
                foreach (JsonElement child in Items(Get(nodeJson[i], "children")))
                {
                    nodes[i].Children.Add(nodes[child.GetInt32()]);
                    parented[child.GetInt32()] = true;
                }

            JsonElement[] meshJson = [.. Items(Get(Root, "meshes"))];
            for (int i = 0; i < meshes.Length; i++)
                foreach (JsonElement primitive in Items(Get(meshJson[i], "primitives")))
                {
                    Material? material = Get(primitive, "material").ValueKind == JsonValueKind.Number ? _materials[Get(primitive, "material").GetInt32()] : null;
                    if (ReadPrimitive(primitive, material) is { } geometry) meshes[i].Primitives.Add(geometry);
                }

            JsonElement scenes = Get(Root, "scenes");
            if (scenes.ValueKind == JsonValueKind.Array && scenes.GetArrayLength() > 0)
                foreach (JsonElement node in Items(Get(scenes[Math.Clamp((int)Number(Root, "scene", 0), 0, scenes.GetArrayLength() - 1)], "nodes")))
                    model.Root.Children.Add(nodes[node.GetInt32()]);
            else
                for (int i = 0; i < nodes.Length; i++)
                    if (!parented[i]) model.Root.Children.Add(nodes[i]);

            JsonElement accessors = Get(Root, "accessors");
            foreach (JsonElement animation in Items(Get(Root, "animations")))
            {
                var clip = new Animation { Name = Text(animation, "name") };
                JsonElement samplers = Get(animation, "samplers");
                foreach (JsonElement channel in Items(Get(animation, "channels")))
                {
                    JsonElement sampler = samplers[Get(channel, "sampler").GetInt32()];
                    JsonElement target = Get(channel, "target");
                    string path = Text(target, "path", "rotation");
                    if (path == "weights") continue;                       // морфы просмотрщик не показывает
                    bool rotation = path == "rotation";
                    int width = rotation ? 4 : 3;                          // компонент в значении: кватернион или вектор
                    float[] output = Floats(accessors[Get(sampler, "output").GetInt32()]);
                    int count = output.Length / width;                      // при CubicSpline значений втрое больше: входящий тангенс, значение, исходящий — хранятся как есть
                    var vectors = new Vector3[rotation ? 0 : count];
                    var turns = new Quaternion[rotation ? count : 0];
                    for (int i = 0; i < count; i++)
                    {
                        if (rotation) turns[i] = new Quaternion(output[i * width], output[i * width + 1], output[i * width + 2], output[i * width + 3]);
                        else vectors[i] = new Vector3(output[i * width], output[i * width + 1], output[i * width + 2]);
                    }
                    clip.Channels.Add(new AnimationChannel
                    {
                        Target = nodes[Get(target, "node").GetInt32()],
                        Path = path == "translation" ? AnimationPath.Translation : path == "scale" ? AnimationPath.Scale : AnimationPath.Rotation,
                        Sampler = new AnimationSampler
                        {
                            Times = Floats(accessors[Get(sampler, "input").GetInt32()]),
                            Vectors = rotation ? null : vectors,
                            Rotations = rotation ? turns : null,
                            Interpolation = Text(sampler, "interpolation", "LINEAR") switch
                            {
                                "STEP" => AnimationInterpolation.Step,
                                "CUBICSPLINE" => AnimationInterpolation.CubicSpline,
                                _ => AnimationInterpolation.Linear,
                            },
                        },
                    });
                }
                model.Animations.Add(clip);
            }

            model.UpdateTransforms();
            model.Measure();
            return model;
        }

        Node ReadNode(JsonElement node, Mesh[] meshes)
        {
            bool matrix = Get(node, "matrix").ValueKind == JsonValueKind.Array;
            return new Node
            {
                Name = Text(node, "name"),
                LocalMatrix = matrix ? Matrix16(Get(node, "matrix")) : null,
                Translation = !matrix && Get(node, "translation").ValueKind == JsonValueKind.Array ? Vec3(Get(node, "translation")) : Vector3.Zero,
                Rotation = !matrix && Get(node, "rotation").ValueKind == JsonValueKind.Array ? Quat(Get(node, "rotation")) : Quaternion.Identity,
                Scale = !matrix && Get(node, "scale").ValueKind == JsonValueKind.Array ? Vec3(Get(node, "scale")) : Vector3.One,
                Mesh = Get(node, "mesh").ValueKind == JsonValueKind.Number ? meshes[Get(node, "mesh").GetInt32()] : null,
            };
        }

        /// <summary>glTF хранит матрицу по столбцам; прочитанная построчно в XNA (векторы-строки) она задаёт то же преобразование.</summary>
        static Matrix Matrix16(JsonElement matrix)
        {
            float[] m = [.. matrix.EnumerateArray().Select(value => (float)value.GetDouble())];
            return new Matrix(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }

        Primitive? ReadPrimitive(JsonElement primitive, Material? material)
        {
            int mode = (int)Number(primitive, "mode", 4);
            if (mode is not (4 or 5 or 6)) return null;            // точки и линии просмотрщик не показывает
            JsonElement accessors = Get(Root, "accessors");
            JsonElement attributes = primitive.GetProperty("attributes");

            float[] position = Floats(accessors[attributes.GetProperty("POSITION").GetInt32()]);
            int count = position.Length / 3;
            if (count == 0) return null;
            var positions = new Vector3[count];
            for (int i = 0; i < count; i++) positions[i] = new Vector3(position[i * 3], position[i * 3 + 1], position[i * 3 + 2]);

            Vector3[] normals = [];
            if (attributes.TryGetProperty("NORMAL", out var normal))
            {
                float[] data = Floats(accessors[normal.GetInt32()]);
                normals = new Vector3[count];
                for (int i = 0; i < count; i++) normals[i] = new Vector3(data[i * 3], data[i * 3 + 1], data[i * 3 + 2]);
            }

            var uvs = new Vector2[count];
            if (attributes.TryGetProperty("TEXCOORD_" + (material?.TexCoord ?? 0), out var uv))
            {
                float[] data = Floats(accessors[uv.GetInt32()]);
                for (int i = 0; i < count; i++) uvs[i] = new Vector2(data[i * 2], data[i * 2 + 1]);
            }

            int[] triangles = Get(primitive, "indices").ValueKind == JsonValueKind.Number
                ? Indices(accessors[Get(primitive, "indices").GetInt32()])
                : [.. Enumerable.Range(0, count)];
            if (mode != 4)
            {
                var list = new List<int>();
                for (int i = 2; i < triangles.Length; i++)
                {
                    if (mode == 5 && i % 2 == 1)                                    // в полосе каждый второй треугольник обходится навстречу
                    {
                        list.Add(triangles[i - 1]);
                        list.Add(triangles[i - 2]);
                    }
                    else
                    {
                        list.Add(triangles[mode == 6 ? 0 : i - 2]);
                        list.Add(triangles[i - 1]);
                    }
                    list.Add(triangles[i]);
                }
                triangles = [.. list];
            }
            if (triangles.Any(index => (uint)index >= (uint)count)) throw new InvalidDataException("Индексы ссылаются за пределы вершин");
            return new Primitive
            {
                Positions = positions,
                Normals = normals.Length > 0 ? normals : Primitive.ComputeNormals(positions, triangles),
                Uvs = uvs,
                Indices = triangles,
                Material = material,
            };
        }

        /// <summary>Аксессор как плотный массив float (count × компоненты): interleaved-буферы, целые и нормализованные целые.</summary>
        float[] Floats(JsonElement accessor)
        {
            int count = accessor.GetProperty("count").GetInt32();
            int components = Text(accessor, "type") switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
                var type => throw new NotSupportedException($"Тип аксессора {type} не поддерживается"),
            };
            var result = new float[count * components];
            if (Get(accessor, "sparse").ValueKind == JsonValueKind.Object) throw new NotSupportedException("Разреженные аксессоры (sparse) не поддерживаются");
            if (Get(accessor, "bufferView").ValueKind != JsonValueKind.Number) return result;       // без буфера — нули, так разрешает спецификация
            JsonElement view = Root.GetProperty("bufferViews")[Get(accessor, "bufferView").GetInt32()];
            int componentType = accessor.GetProperty("componentType").GetInt32();
            bool normalized = Get(accessor, "normalized").ValueKind is JsonValueKind.True;
            int size = Size(componentType);
            int start = (int)Number(view, "byteOffset", 0) + (int)Number(accessor, "byteOffset", 0);
            int stride = (int)Number(view, "byteStride", components * size);
            if (count > 0 && (ulong)start + (ulong)(count - 1) * (ulong)stride + (ulong)(components * size) > (ulong)_bin.Length)
                throw new InvalidDataException("Аксессор выходит за пределы двоичного буфера");
            if (componentType == Float && stride == components * 4)
            {
                // самый частый случай — плотный массив float: копируем целиком, без чтения по числу
                MemoryMarshal.Cast<byte, float>(_bin.AsSpan(start, result.Length * 4)).CopyTo(result);
                return result;
            }
            ReadOnlySpan<byte> bin = _bin;
            for (int i = 0; i < result.Length; i++)
                result[i] = Read(bin.Slice(start + i / components * stride + i % components * size, size), componentType, normalized);
            return result;
        }

        static float Read(ReadOnlySpan<byte> bytes, int componentType, bool normalized)
        {
            switch (componentType)
            {
                case Float: return BitConverter.ToSingle(bytes);
                case UnsignedByte: return normalized ? bytes[0] / 255f : bytes[0];
                case Byte: return normalized ? Math.Max(unchecked((sbyte)bytes[0]) / 127f, -1f) : unchecked((sbyte)bytes[0]);
                case UnsignedShort: return normalized ? BitConverter.ToUInt16(bytes) / 65535f : BitConverter.ToUInt16(bytes);
                case Short: return normalized ? Math.Max(BitConverter.ToInt16(bytes) / 32767f, -1f) : BitConverter.ToInt16(bytes);
                case UnsignedInt: return normalized ? BitConverter.ToUInt32(bytes) / 4294967295f : BitConverter.ToUInt32(bytes);
                default: throw new NotSupportedException($"componentType {componentType} не поддерживается");
            }
        }

        /// <summary>Индексы треугольников: UNSIGNED_BYTE, UNSIGNED_SHORT или UNSIGNED_INT.</summary>
        int[] Indices(JsonElement accessor)
        {
            int count = accessor.GetProperty("count").GetInt32();
            JsonElement view = Root.GetProperty("bufferViews")[Get(accessor, "bufferView").GetInt32()];
            int type = accessor.GetProperty("componentType").GetInt32();
            int start = (int)Number(view, "byteOffset", 0) + (int)Number(accessor, "byteOffset", 0);
            int size = Size(type);
            if (count > 0 && (ulong)start + (ulong)count * (ulong)size > (ulong)_bin.Length) throw new InvalidDataException("Индексы выходят за пределы двоичного буфера");
            var result = new int[count];
            ReadOnlySpan<byte> bin = _bin;
            for (int i = 0; i < count; i++)
                result[i] = type switch
                {
                    UnsignedByte => bin[start + i],
                    UnsignedShort => BitConverter.ToUInt16(bin.Slice(start + i * 2, 2)),
                    UnsignedInt => (int)BitConverter.ToUInt32(bin.Slice(start + i * 4, 4)),
                    _ => throw new NotSupportedException($"Индексы с componentType {type} не поддерживаются"),
                };
            return result;
        }

        static int Size(int componentType) => componentType switch
        {
            Byte or UnsignedByte => 1,
            Short or UnsignedShort => 2,
            UnsignedInt or Float => 4,
            _ => throw new NotSupportedException($"componentType {componentType} не поддерживается"),
        };

        Material ReadMaterial(JsonElement material)
        {
            JsonElement pbr = Get(material, "pbrMetallicRoughness");
            Vector4 color = Get(pbr, "baseColorFactor").ValueKind == JsonValueKind.Array ? Display(Vec4(Get(pbr, "baseColorFactor"))) : Vector4.One;
            Image? texture = null;
            TextureAddress addressU = TextureAddress.Wrap, addressV = TextureAddress.Wrap;
            if (Get(pbr, "baseColorTexture").ValueKind == JsonValueKind.Object)
            {
                JsonElement info = Get(pbr, "baseColorTexture");
                JsonElement descriptor = Root.GetProperty("textures")[Get(info, "index").GetInt32()];
                // webp-текстуры прячут источник в EXT_texture_webp; у остальных он в source
                JsonElement source = Get(Get(descriptor, "extensions"), "EXT_texture_webp");
                texture = _images[(source.ValueKind == JsonValueKind.Object ? source : descriptor).GetProperty("source").GetInt32()];
                JsonElement sampler = Get(descriptor, "sampler").ValueKind == JsonValueKind.Number ? Root.GetProperty("samplers")[Get(descriptor, "sampler").GetInt32()] : default;
                addressU = Wrap(sampler, "wrapS");
                addressV = Wrap(sampler, "wrapT");
            }
            AlphaMode alpha = Text(material, "alphaMode", "OPAQUE") switch { "BLEND" => AlphaMode.Blend, "MASK" => AlphaMode.Mask, _ => AlphaMode.Opaque };
            if (Number(Get(Get(material, "extensions"), "KHR_materials_transmission"), "transmissionFactor", 0) > 0)
            {
                alpha = AlphaMode.Blend;
                color.W = GlassAlpha;
            }
            return new Material
            {
                Name = Text(material, "name"),
                BaseColor = color,
                Texture = texture,
                TexCoord = (int)Number(Get(pbr, "baseColorTexture"), "texCoord", 0),
                AddressU = addressU,
                AddressV = addressV,
                AlphaMode = alpha,
                DoubleSided = Get(material, "doubleSided").ValueKind is JsonValueKind.True,
                Unlit = Get(Get(material, "extensions"), "KHR_materials_unlit").ValueKind == JsonValueKind.Object,
                // metallicFactor по спецификации по умолчанию 1 — но тогда простые цветные материалы (брезент, верёвка) показывались бы чёрными
                Metallic = Number(pbr, "metallicFactor", 0),
                Roughness = Number(pbr, "roughnessFactor", 1),
                Emissive = Get(material, "emissiveFactor").ValueKind == JsonValueKind.Array ? Display(Vec3(Get(material, "emissiveFactor"))) : Vector3.Zero,
            };
        }

        static TextureAddress Wrap(JsonElement sampler, string name) => (int)Number(sampler, name, 10497) switch
        {
            33071 => TextureAddress.Clamp, 33648 => TextureAddress.Mirror, _ => TextureAddress.Wrap,
        };

        Image ReadImage(JsonElement image)
        {
            string name = Text(image, "name");
            if (Get(image, "bufferView").ValueKind == JsonValueKind.Number)
            {
                JsonElement view = Root.GetProperty("bufferViews")[Get(image, "bufferView").GetInt32()];
                return new Image { Name = name, Data = _bin.AsSpan((int)Number(view, "byteOffset", 0), view.GetProperty("byteLength").GetInt32()).ToArray() };
            }
            string uri = image.GetProperty("uri").GetString()!;                       // внешние файлы и data:-URI
            if (uri.StartsWith("data:", StringComparison.Ordinal)) return new Image { Name = name, Data = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]) };
            string file = Path.Combine(_directory, Uri.UnescapeDataString(uri));
            return new Image { Name = name, Data = File.ReadAllBytes(file) };
        }

        /// <summary>Элементы массива, которого может и не быть.</summary>
        static IEnumerable<JsonElement> Items(JsonElement array) => array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

        static JsonElement Get(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : default;

        static string Text(JsonElement element, string name, string fallback = "") => Get(element, name).ValueKind == JsonValueKind.String ? Get(element, name).GetString()! : fallback;

        static float Number(JsonElement element, string name, float fallback) => Get(element, name).ValueKind is JsonValueKind.Number ? (float)Get(element, name).GetDouble() : fallback;

        static Vector3 Vec3(JsonElement array) => new((float)array[0].GetDouble(), (float)array[1].GetDouble(), (float)array[2].GetDouble());

        static Quaternion Quat(JsonElement array) => new((float)array[0].GetDouble(), (float)array[1].GetDouble(), (float)array[2].GetDouble(), (float)array[3].GetDouble());

        static Vector4 Vec4(JsonElement array) => new(Vec3(array), (float)array[3].GetDouble());

        /// <summary>glTF хранит цвета в линейном пространстве, а тексели текстур — в sRGB: приводим множитель к текселам.</summary>
        static Vector4 Display(Vector4 color) => new(ToDisplay(color.X), ToDisplay(color.Y), ToDisplay(color.Z), color.W);

        static Vector3 Display(Vector3 color) => new(ToDisplay(color.X), ToDisplay(color.Y), ToDisplay(color.Z));

        static float ToDisplay(float linear) => linear <= 0.0031308f ? 12.92f * linear : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }
}
