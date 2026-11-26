using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MeshViewer;

/// <summary>Какие грани отсекать. Файлы приходят и с прямой, и с обратной намоткой — поэтому переключается.</summary>
public enum Culling { Back, Front, Off }

/// <summary>Что показывает окно, кроме самой модели.</summary>
public sealed class DisplayOptions
{
    public bool Wireframe { get; set; }
    public Culling Culling { get; set; } = Culling.Back;
}

/// <summary>
/// Рисует модель на встроенном BasicEffect (свой шейдер потребовал бы Content Pipeline). Буферы вершин
/// заливаются один раз, в координатах узла; каждый кадр меняются только мировые матрицы — анимация узлов
/// не потребует перезаливать геометрию.
/// </summary>
public sealed class ModelRenderer : IDisposable
{
    /// <summary>Фон: средне-серый — на нём читаются и тёмные, и светлые модели.</summary>
    public static readonly Color Background = new(56, 60, 67);

    readonly GraphicsDevice _device;
    readonly BasicEffect _effect;
    readonly Dictionary<Primitive, GpuMesh> _meshes = [];
    readonly Dictionary<Image, Texture2D?> _textures = [];
    readonly Dictionary<(TextureAddress, TextureAddress), SamplerState> _samplers = [];
    readonly Dictionary<(FillMode, CullMode), RasterizerState> _rasterizers = [];
    readonly List<(Node Node, Primitive Primitive, GpuMesh Mesh)> _scene = [];
    readonly List<Draw> _opaque = [], _blended = [];

    /// <summary>Сколько текстур не расшифровались — они рисуются цветом материала.</summary>
    public int UndecodedTextures { get; private set; }

    public ModelRenderer(GraphicsDevice device, Model model)
    {
        _device = device;
        _effect = new BasicEffect(device)
        {
            LightingEnabled = true,
            PreferPerPixelLighting = true,
            VertexColorEnabled = false,
            FogEnabled = false,
            AmbientLightColor = Vector3.One * 0.45f,
        };
        foreach (Node node in model.Nodes)
            foreach (Primitive primitive in node.Mesh!.Primitives)
            {
                GpuMesh mesh = _meshes.TryGetValue(primitive, out GpuMesh? known) ? known : _meshes[primitive] = Upload(primitive);
                _scene.Add((node, primitive, mesh));
            }
        foreach (Image image in model.Images)
            try
            {
                _textures[image] = Textures.Upload(device, image);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _textures[image] = null;
                UndecodedTextures++;
                Program.Out.WriteLine($"Текстура «{image.Name}» не расшифровалась: {e.Message}");
            }
    }

    public void Render(Model model, OrbitCamera camera, DisplayOptions display)
    {
        _opaque.Clear();
        _blended.Clear();
        Vector3 eye = camera.Eye;
        foreach (var (node, primitive, mesh) in _scene)
        {
            var draw = new Draw(node.World, primitive, mesh, Vector3.DistanceSquared(node.World.Translation, eye));
            (mesh.Transparent ? _blended : _opaque).Add(draw);
        }
        _blended.Sort((a, b) => b.Distance.CompareTo(a.Distance));     // прозрачные — дальние раньше ближних

        SetLights(camera);
        _effect.View = camera.View;
        _effect.Projection = camera.Projection(_device.Viewport.AspectRatio);
        _device.DepthStencilState = DepthStencilState.Default;
        foreach (Draw draw in _opaque) DrawOne(draw, display);
        foreach (Draw draw in _blended) DrawOne(draw, display);
    }

    /// <summary>Свет при камере («студийный»): освещено то, что видно, с какой стороны ни смотри. Направление — куда свет идёт.</summary>
    void SetLights(OrbitCamera camera)
    {
        Vector3 forward = camera.Forward, right = camera.Right, up = camera.Up;
        _effect.DirectionalLight0.Enabled = true;
        _effect.DirectionalLight0.DiffuseColor = Vector3.One * 0.62f;                   // рисующий: сверху-слева-спереди
        _effect.DirectionalLight0.SpecularColor = Vector3.One;
        _effect.DirectionalLight0.Direction = Vector3.Normalize(forward - up * 0.8f + right * 0.6f);
        _effect.DirectionalLight1.Enabled = true;
        _effect.DirectionalLight1.DiffuseColor = new Vector3(0.22f, 0.23f, 0.26f);      // заполняющий, холодный
        _effect.DirectionalLight1.SpecularColor = Vector3.Zero;
        _effect.DirectionalLight1.Direction = Vector3.Normalize(forward - right * 0.9f + up * 0.25f);
        _effect.DirectionalLight2.Enabled = true;
        _effect.DirectionalLight2.DiffuseColor = Vector3.One * 0.16f;
        _effect.DirectionalLight2.SpecularColor = Vector3.One;
        _effect.DirectionalLight2.Direction = Vector3.Normalize(-forward - up * 0.7f + right * 0.3f);   // контровой
    }

    void DrawOne(Draw draw, DisplayOptions display)
    {
        Material material = draw.Primitive.Material ?? Material.Default;
        Texture2D? texture = material.Texture is { } image ? _textures.GetValueOrDefault(image) : null;
        Vector4 color = material.BaseColor;
        Vector3 rgb = new(color.X, color.Y, color.Z);

        _effect.World = draw.World;
        _effect.LightingEnabled = !material.Unlit;
        _effect.TextureEnabled = texture is not null;
        _effect.Texture = texture;
        // металл почти не рассеивает свет, но карты окружения у нас нет — чистый металл был бы чёрным;
        // оставляем ему часть цвета, а остальное — в блик, ширина которого — шероховатость
        _effect.DiffuseColor = rgb * (1f - material.Metallic * 0.6f);
        _effect.Alpha = draw.Mesh.Transparent ? color.W : 1f;
        _effect.EmissiveColor = material.Emissive;
        float gloss = (1f - material.Roughness) * (1f - material.Roughness);
        _effect.SpecularColor = Vector3.Lerp(new Vector3(0.04f), rgb, material.Metallic) * (gloss * 1.5f);
        _effect.SpecularPower = Math.Clamp(2f / (MathF.Pow(material.Roughness, 4) + 1e-3f) - 2f, 8f, 64f);

        _device.BlendState = draw.Mesh.Transparent ? BlendState.NonPremultiplied : BlendState.Opaque;
        _device.SamplerStates[0] = texture is null ? SamplerState.LinearWrap : Sampler(material);
        _device.RasterizerState = Rasterizer(display.Wireframe ? FillMode.WireFrame : FillMode.Solid, material.DoubleSided ? Culling.Off : display.Culling);
        _device.SetVertexBuffer(draw.Mesh.Vertices);
        _device.Indices = draw.Mesh.Indices;
        foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, draw.Mesh.Triangles);
        }
    }

    SamplerState Sampler(Material material)
    {
        var key = (material.AddressU, material.AddressV);
        return _samplers.TryGetValue(key, out SamplerState? known)
            ? known
            : (_samplers[key] = new SamplerState { AddressU = Mode(key.Item1), AddressV = Mode(key.Item2), Filter = TextureFilter.Anisotropic });
    }

    static TextureAddressMode Mode(TextureAddress address) => address switch
    {
        TextureAddress.Clamp => TextureAddressMode.Clamp,
        TextureAddress.Mirror => TextureAddressMode.Mirror,
        _ => TextureAddressMode.Wrap,
    };

    /// <summary>Грани glTF и OBJ обходятся против часовой — CullClockwise оставляет именно их.</summary>
    RasterizerState Rasterizer(FillMode fill, Culling culling)
    {
        var cull = culling switch { Culling.Back => CullMode.CullClockwiseFace, Culling.Front => CullMode.CullCounterClockwiseFace, _ => CullMode.None };
        return _rasterizers.TryGetValue((fill, cull), out RasterizerState? known)
            ? known
            : (_rasterizers[(fill, cull)] = new RasterizerState { FillMode = fill, CullMode = cull });
    }

    GpuMesh Upload(Primitive primitive)
    {
        var vertices = new VertexPositionNormalTexture[primitive.Positions.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new VertexPositionNormalTexture(primitive.Positions[i], primitive.Normals[i], primitive.Uvs[i]);
        var buffer = new VertexBuffer(_device, VertexPositionNormalTexture.VertexDeclaration, vertices.Length, BufferUsage.WriteOnly);
        buffer.SetData(vertices);

        IndexBuffer indices;
        if (vertices.Length <= ushort.MaxValue)
        {
            indices = new IndexBuffer(_device, IndexElementSize.SixteenBits, primitive.Indices.Length, BufferUsage.WriteOnly);
            indices.SetData(primitive.Indices.Select(index => (ushort)index).ToArray());
        }
        else
        {
            indices = new IndexBuffer(_device, IndexElementSize.ThirtyTwoBits, primitive.Indices.Length, BufferUsage.WriteOnly);
            indices.SetData(primitive.Indices);
        }
        bool transparent = primitive.Material is { AlphaMode: not AlphaMode.Opaque } or { BaseColor.W: < 1f };
        return new GpuMesh(buffer, indices, primitive.Triangles, transparent);
    }

    public void Dispose()
    {
        foreach (GpuMesh mesh in _meshes.Values) mesh.Dispose();
        foreach (Texture2D? texture in _textures.Values) texture?.Dispose();
        foreach (RasterizerState state in _rasterizers.Values) state.Dispose();
        foreach (SamplerState sampler in _samplers.Values) sampler.Dispose();
        _effect.Dispose();
    }

    readonly record struct Draw(Matrix World, Primitive Primitive, GpuMesh Mesh, float Distance);

    /// <summary>Геометрия примитива в видеокарте.</summary>
    sealed class GpuMesh(VertexBuffer vertices, IndexBuffer indices, int triangles, bool transparent) : IDisposable
    {
        public VertexBuffer Vertices { get; } = vertices;
        public IndexBuffer Indices { get; } = indices;
        public int Triangles { get; } = triangles;

        /// <summary>Рисуется с прозрачностью — таким нужен порядок.</summary>
        public bool Transparent { get; } = transparent;

        public void Dispose()
        {
            Vertices.Dispose();
            Indices.Dispose();
        }
    }
}
