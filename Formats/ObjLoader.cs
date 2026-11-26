using System.Globalization;
using Microsoft.Xna.Framework;

namespace MeshViewer;

/// <summary>
/// Загрузчик Wavefront OBJ с материалами MTL: v/vt/vn/f, группы o/g, usemtl, Kd, d/Tr, map_Kd.
/// Текстурные координаты переворачиваются по V (в OBJ ноль снизу). Координаты и намотка — как в файле.
/// </summary>
public static class ObjLoader
{
    public static Model Load(string path) => new Obj(path).Build();

    private sealed class Obj
    {
        readonly string _directory;
        readonly Model _model;
        readonly List<Vector3> _positions = [];
        readonly List<Vector3> _normals = [];
        readonly List<Vector2> _uvs = [];

        /// <summary>Материалы по именам — из всех mtllib файла.</summary>
        readonly Dictionary<string, Material> _materials = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Image> _imagesByPath = new(StringComparer.OrdinalIgnoreCase);

        string _group;
        Mesh? _mesh;                  // создаётся с первой гранью группы — пустых узлов o/g не остаётся
        Material? _material;
        Part? _part;                  // грани одного материала в текущей группе

        public Obj(string path)
        {
            _directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            _model = new Model { Name = Path.GetFileName(path) };
            _group = Path.GetFileNameWithoutExtension(path);
        }

        public Model Build()
        {
            foreach (string raw in File.ReadLines(Path.Combine(_directory, _model.Name)))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] tokens = Fields(line);
                if (tokens.Length == 0) continue;
                switch (tokens[0])
                {
                    case "v" when tokens.Length >= 4:
                        _positions.Add(new Vector3(Number(tokens[1]), Number(tokens[2]), Number(tokens[3])));
                        break;
                    case "vt" when tokens.Length >= 3:
                        _uvs.Add(new Vector2(Number(tokens[1]), Number(tokens[2])));
                        break;
                    case "vn" when tokens.Length >= 4:
                        _normals.Add(new Vector3(Number(tokens[1]), Number(tokens[2]), Number(tokens[3])));
                        break;
                    case "mtllib":
                        foreach (string library in tokens[1..]) LoadMaterials(library);
                        break;
                    case "usemtl" when tokens.Length >= 2:
                        Flush();
                        _material = _materials.GetValueOrDefault(tokens[1]);
                        break;
                    case "o" or "g":
                        Flush();
                        _group = tokens.Length > 1 ? tokens[1] : "";
                        _mesh = null;
                        break;
                    case "f" when tokens.Length >= 4:
                        Face(tokens[1..]);
                        break;
                }
            }
            Flush();
            _model.UpdateTransforms();
            _model.Measure();
            return _model;
        }

        void Face(string[] corners)
        {
            if (_mesh is null)
            {
                _mesh = new Mesh { Name = _group };
                _model.Root.Children.Add(new Node { Name = _group, Mesh = _mesh });
            }
            _part ??= new Part(_material);
            int first = Corner(corners[0]), previous = Corner(corners[1]);
            for (int i = 2; i < corners.Length; i++)             // многоугольник — веером от первого угла
            {
                int next = Corner(corners[i]);
                _part.Indices.Add(first);
                _part.Indices.Add(previous);
                _part.Indices.Add(next);
                previous = next;
            }
        }

        int Corner(string corner)
        {
            // индексы через дробь: позиция, текстурная координата, нормаль; двух последних может не быть
            string[] index = corner.Split('/');
            return _part!.Vertex(
                Reference(index[0], _positions.Count),
                index.Length > 1 ? Reference(index[1], _uvs.Count) : -1,
                index.Length > 2 ? Reference(index[2], _normals.Count) : -1,
                _positions, _uvs, _normals);
        }

        /// <summary>Индекс OBJ: положительные считаются с единицы, отрицательные — от конца списка; пустой — нет.</summary>
        static int Reference(string value, int count)
        {
            if (value.Length == 0) return -1;
            int index = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            return index < 0 ? count + index : index - 1;
        }

        void Flush()
        {
            if (_part?.Finish() is { } primitive) _mesh!.Primitives.Add(primitive);
            _part = null;
        }

        void LoadMaterials(string library)
        {
            string? path = Find(_directory, library);
            if (path is null) return;
            string directory = Path.GetDirectoryName(path)!;
            string? name = null, mapColour = null, mapAmbient = null;
            Vector3? colour = null;
            float opacity = 1f;

            void Commit()
            {
                if (name is null) return;
                // map_Ka выручает, когда map_Kd нет
                string? texture = Find(directory, mapColour) ?? Find(directory, mapAmbient);
                _model.Materials.Add(_materials[name] = new Material
                {
                    Name = name,
                    BaseColor = new Vector4(colour ?? new Vector3(0.8f), opacity),
                    Texture = texture is null ? null : LoadImage(texture),
                    AlphaMode = opacity < 1f ? AlphaMode.Blend : AlphaMode.Opaque,
                });
            }

            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] tokens = Fields(line);
                if (tokens.Length < 2) continue;
                switch (tokens[0])
                {
                    case "newmtl":
                        Commit();
                        name = tokens[1];
                        colour = null;
                        opacity = 1f;
                        mapColour = null;
                        mapAmbient = null;
                        break;
                    case "Kd" when tokens.Length >= 4 && IsNumber(tokens[1]):
                        colour = new Vector3(Number(tokens[1]), Number(tokens[2]), Number(tokens[3]));
                        break;
                    case "d":
                        opacity = Number(tokens.Length > 2 && tokens[1] == "-halo" ? tokens[2] : tokens[1]);
                        break;
                    case "Tr":
                        opacity = 1f - Number(tokens[1]);        // Tr — прозрачность, а не непрозрачность
                        break;
                    case "map_Kd":
                        mapColour = tokens[^1];                  // перед именем файла могут стоять опции: берём последнее слово
                        break;
                    case "map_Ka":
                        mapAmbient = tokens[^1];
                        break;
                }
            }
            Commit();
        }

        /// <summary>Файл относительно библиотеки материалов, а если его там нет — относительно самого OBJ.</summary>
        string? Find(string directory, string? file)
        {
            if (file is null) return null;
            file = file.Trim('"').Replace('\\', Path.DirectorySeparatorChar);
            return new[] { Path.Combine(directory, file), Path.Combine(_directory, file) }.FirstOrDefault(File.Exists);
        }

        Image LoadImage(string path)
        {
            if (!_imagesByPath.TryGetValue(path, out Image? image))
                _model.Images.Add(_imagesByPath[path] = image = new Image { Name = Path.GetFileName(path), Data = File.ReadAllBytes(path) });
            return image;
        }

        /// <summary>Слова строки — до комментария.</summary>
        static string[] Fields(string line)
        {
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int end = Array.FindIndex(tokens, token => token[0] == '#');
            return end < 0 ? tokens : tokens[..end];
        }

        static float Number(string token) => float.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);

        static bool IsNumber(string token) => float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        /// <summary>У OBJ ноль текстурной координаты снизу, у текстуры в видеокарте — сверху.</summary>
        static Vector2 Flip(Vector2 uv) => new(uv.X, 1f - uv.Y);

        static readonly Vector3 Missing = new(float.NaN);

        /// <summary>Строит примитив: вершины с одним набором индексов v/vt/vn — одна вершина.</summary>
        sealed class Part(Material? material)
        {
            readonly Dictionary<(int Position, int Uv, int Normal), int> _corners = [];
            readonly List<Vector3> _positions = [];
            readonly List<Vector3> _normals = [];
            readonly List<Vector2> _uvs = [];
            public List<int> Indices { get; } = [];

            public int Vertex(int position, int uv, int normal, List<Vector3> positions, List<Vector2> uvs, List<Vector3> normals)
            {
                if (_corners.TryGetValue((position, uv, normal), out int known)) return known;
                if ((uint)position >= (uint)positions.Count || (uv >= 0 && (uint)uv >= (uint)uvs.Count) || (normal >= 0 && (uint)normal >= (uint)normals.Count))
                    throw new InvalidDataException($"Грань ссылается на несуществующую вершину ({position + 1}/{uv + 1}/{normal + 1})");
                int index = _positions.Count;
                _corners[(position, uv, normal)] = index;
                _positions.Add(positions[position]);
                _uvs.Add(uv < 0 ? Vector2.Zero : Flip(uvs[uv]));
                _normals.Add(normal < 0 ? Missing : normals[normal]);
                return index;
            }

            public Primitive? Finish()
            {
                if (Indices.Count == 0) return null;
                Vector3[] normals = [.. _normals];
                if (Array.Exists(normals, normal => float.IsNaN(normal.X)))
                {
                    // нормалей не было (или не у всех вершин) — сглаженные по граням
                    Vector3[] computed = Primitive.ComputeNormals([.. _positions], [.. Indices]);
                    for (int i = 0; i < normals.Length; i++)
                        if (float.IsNaN(normals[i].X)) normals[i] = computed[i];
                }
                return new Primitive { Positions = [.. _positions], Normals = normals, Uvs = [.. _uvs], Indices = [.. Indices], Material = material };
            }
        }
    }
}
