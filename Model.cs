using Microsoft.Xna.Framework;

namespace MeshViewer;

/// <summary>
/// Загруженная модель в терминах сцены: узлы с локальными преобразованиями, сетки, материалы, анимации.
/// Геометрия хранится в координатах узла, мировые матрицы считаются из TRS каждый кадр — узел можно
/// анимировать, не перезаливая вершины. От формата файла не зависит.
/// </summary>
public sealed class Model
{
    public required string Name { get; init; }

    /// <summary>Корень сцены: держит узлы верхнего уровня, сам преобразований не несёт.</summary>
    public Node Root { get; } = new();

    public List<Material> Materials { get; } = [];
    public List<Image> Images { get; } = [];

    /// <summary>Анимации файла: какие узлы и по каким ключам меняют свои TRS. Пока только хранятся.</summary>
    public List<Animation> Animations { get; } = [];

    readonly List<(Node Node, Matrix Parent)> _pending = [];
    readonly HashSet<Node> _posed = [];

    /// <summary>Анимации одной строкой — для заголовка окна и отчёта: «open (1.2 с), close (1.2 с)».</summary>
    public string DescribeAnimations() => string.Join(", ", Animations.Select(animation => $"{animation.Name} ({animation.Duration:0.0#} с)"));

    /// <summary>Габариты в мире при текущих позах узлов — по ним камера вписывает модель в кадр.</summary>
    public BoundingBox Bounds { get; private set; }

    public int Vertices => Nodes.Sum(node => node.Mesh!.Primitives.Sum(primitive => primitive.Positions.Length));
    public int Triangles => Nodes.Sum(node => node.Mesh!.Primitives.Sum(primitive => primitive.Triangles));

    /// <summary>Узлы с сеткой, в порядке обхода иерархии.</summary>
    public IEnumerable<Node> Nodes => Root.Descendants().Where(node => node.Mesh is not null);

    /// <summary>Мировые матрицы узлов по их локальным TRS — то, что будет пересчитывать проигрыватель анимации.</summary>
    public void UpdateTransforms()
    {
        // Без рекурсии: глубокая иерархия не упирается в стек, а цикл в графе узлов (битый файл) не роняет процесс
        _pending.Clear();
        _posed.Clear();
        foreach (Node root in Root.Children)
            if (_posed.Add(root)) _pending.Add((root, Matrix.Identity));
        for (int i = 0; i < _pending.Count; i++)                // список растёт по ходу — обход в ширину
        {
            var (node, parent) = _pending[i];
            node.World = node.Local * parent;
            foreach (Node child in node.Children)
                if (_posed.Add(child)) _pending.Add((child, node.World));
        }
    }

    /// <summary>Обмеряет модель в мире (после <see cref="UpdateTransforms"/>).</summary>
    public void Measure()
    {
        Vector3 min = Vector3.One * float.MaxValue, max = Vector3.One * float.MinValue;
        foreach (Node node in Nodes)
            foreach (Primitive primitive in node.Mesh!.Primitives)
                foreach (Vector3 position in primitive.Positions)
                {
                    Vector3 world = Vector3.Transform(position, node.World);
                    if (float.IsNaN(world.X) || float.IsNaN(world.Y) || float.IsNaN(world.Z)) continue;
                    min = Vector3.Min(min, world);
                    max = Vector3.Max(max, world);
                }
        Bounds = min.X <= max.X ? new BoundingBox(min, max) : new BoundingBox(-Vector3.One, Vector3.One);   // пустая модель — единичный куб
    }
}

/// <summary>
/// Узел сцены: локальные масштаб, поворот и сдвиг, дети и — не обязательно — сетка в координатах узла.
/// Анимация меняет <see cref="Translation"/>, <see cref="Rotation"/>, <see cref="Scale"/>, остальное считает сцена.
/// </summary>
public sealed class Node
{
    public string Name { get; init; } = "";
    public Vector3 Translation { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;

    /// <summary>Матрица вместо TRS — когда формат разрешает узлу либо то, либо другое (glTF).</summary>
    public Matrix? LocalMatrix { get; init; }

    public List<Node> Children { get; } = [];

    /// <summary>Сетка в координатах этого узла.</summary>
    public Mesh? Mesh { get; init; }

    /// <summary>Преобразование в мир (v · World); обновляет <see cref="Model.UpdateTransforms"/>.</summary>
    public Matrix World { get; internal set; } = Matrix.Identity;

    /// <summary>Локальное преобразование (строковые векторы XNA): масштаб, затем поворот, затем сдвиг.</summary>
    public Matrix Local => LocalMatrix ?? Matrix.CreateScale(Scale) * Matrix.CreateFromQuaternion(Rotation) * Matrix.CreateTranslation(Translation);

    /// <summary>Потомки в ширину; узел, встреченный повторно (общий ребёнок или цикл в битом файле), выдаётся один раз.</summary>
    public IEnumerable<Node> Descendants()
    {
        var seen = new HashSet<Node>();
        var queue = new Queue<Node>(Children);
        while (queue.Count > 0)
        {
            Node node = queue.Dequeue();
            if (!seen.Add(node)) continue;
            yield return node;
            foreach (Node child in node.Children) queue.Enqueue(child);
        }
    }
}

/// <summary>Сетка: треугольники, разбитые по материалам.</summary>
public sealed class Mesh
{
    public string Name { get; init; } = "";
    public List<Primitive> Primitives { get; } = [];
}

/// <summary>Треугольники одного материала в координатах узла.</summary>
public sealed class Primitive
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }
    /// <summary>Индексы по три на треугольник.</summary>
    public required int[] Indices { get; init; }
    public Material? Material { get; init; }
    public int Triangles => Indices.Length / 3;

    /// <summary>Сглаженные нормали, когда файл их не дал: сумма нормалей граней (площадь грани — вес).</summary>
    public static Vector3[] ComputeNormals(Vector3[] positions, int[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (int i = 0; i < indices.Length; i += 3)
        {
            Vector3 a = positions[indices[i]], b = positions[indices[i + 1]], c = positions[indices[i + 2]];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            normals[indices[i]] += normal;
            normals[indices[i + 1]] += normal;
            normals[indices[i + 2]] += normal;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() > 1e-20f ? Vector3.Normalize(normals[i]) : Vector3.Up;
        return normals;
    }
}

/// <summary>
/// Поверхность: цвет-множитель, текстура и способ смешивания. Цвет — в том же пространстве, что и тексели
/// текстуры (sRGB): конвейер BasicEffect нелинейность не поправляет, и линейные цвета glTF приводятся к текселам.
/// </summary>
public sealed class Material
{
    /// <summary>Для геометрии без материала: нейтральный серый.</summary>
    public static Material Default { get; } = new() { Name = "default", BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1f) };

    public string Name { get; init; } = "";

    /// <summary>Цвет-множитель; альфа — прозрачность.</summary>
    public Vector4 BaseColor { get; init; } = Vector4.One;
    public Image? Texture { get; init; }

    /// <summary>Номер набора текстурных координат вершин, которым пользуется текстура (TEXCOORD_0, _1, ...).</summary>
    public int TexCoord { get; init; }
    public TextureAddress AddressU { get; init; } = TextureAddress.Wrap;
    public TextureAddress AddressV { get; init; } = TextureAddress.Wrap;

    /// <summary>Blend — с прозрачностью. Mask показывается так же: отсечения по альфе у BasicEffect нет.</summary>
    public AlphaMode AlphaMode { get; init; }

    public bool DoubleSided { get; init; }

    /// <summary>Без света, цвет как есть (KHR_materials_unlit).</summary>
    public bool Unlit { get; init; }

    public float Metallic { get; init; }
    public float Roughness { get; init; } = 1f;
    public Vector3 Emissive { get; init; }
}

/// <summary>Картинка в том виде, в каком она хранится в файле модели (png, jpeg, webp); расшифровывает графика.</summary>
public sealed class Image
{
    public string Name { get; init; } = "";
    public required byte[] Data { get; init; }
}

public enum AlphaMode { Opaque, Mask, Blend }

public enum TextureAddress { Wrap, Clamp, Mirror }

/// <summary>Анимация: именованный набор каналов. Проигрывания пока нет — данные разобраны и хранятся.</summary>
public sealed class Animation
{
    public string Name { get; init; } = "";
    public List<AnimationChannel> Channels { get; } = [];

    /// <summary>Длительность в секундах — по последнему ключу самого длинного канала.</summary>
    public float Duration => Channels.Count == 0 ? 0 : Channels.Max(channel => channel.Sampler.Times.Length == 0 ? 0 : channel.Sampler.Times[^1]);
}

/// <summary>Канал: какой узел, какое из TRS и по каким ключам меняет.</summary>
public sealed class AnimationChannel
{
    public required Node Target { get; init; }
    public AnimationPath Path { get; init; }
    public required AnimationSampler Sampler { get; init; }
}

/// <summary>
/// Ключи канала: моменты времени и значения — векторы для сдвига и масштаба, кватернионы для поворота.
/// При CubicSpline значений втрое больше числа ключей: входящий тангенс, значение, исходящий — как в файле.
/// </summary>
public sealed class AnimationSampler
{
    public required float[] Times { get; init; }
    public Vector3[]? Vectors { get; init; }
    public Quaternion[]? Rotations { get; init; }
    public AnimationInterpolation Interpolation { get; init; }
}

public enum AnimationPath { Translation, Rotation, Scale }

public enum AnimationInterpolation { Linear, Step, CubicSpline }

/// <summary>Сведения о модели одной строкой — для --info, --shot и отчётов.</summary>
public static class Stats
{
    public static string Line(Model model, int undecodedTextures, int milliseconds) =>
        $"{model.Name}: {model.Vertices:N0} вершин, {model.Triangles:N0} треугольников, материалов {model.Materials.Count}, " +
        $"текстур {model.Images.Count - undecodedTextures}/{model.Images.Count}, " +
        $"анимации: {(model.Animations.Count == 0 ? "нет" : model.DescribeAnimations())}, габариты {model.Bounds}, готово за {milliseconds} мс";
}
