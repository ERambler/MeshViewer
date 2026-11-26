using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MeshViewer;

/// <summary>
/// Окно просмотрщика. Мышь: ЛКМ — вращение, колесо — масштаб, ПКМ — панорама. Клавиши: F — вписать модель,
/// Z — каркас, C — какие грани отсекать, Esc — выход. Справка и счётчики — в заголовке окна: консоли нет.
/// Модель читается параллельно с созданием окна, а в фоне узнаётся, нет ли новой версии.
/// </summary>
public sealed class ViewerGame : Game
{
    readonly GraphicsDeviceManager _graphics;
    readonly Task<Model> _loading;
    readonly CommandLine _command;
    readonly OrbitCamera _camera = new();
    readonly DisplayOptions _display = new();
    ModelRenderer? _renderer;
    Model _model = null!;
    MouseState _mouse;
    KeyboardState _keys;
    Task<Updater.Release?>? _update;
    bool _updateAsked;
    int _frames;

    public ViewerGame(Task<Model> loading, CommandLine command)
    {
        _loading = loading;
        _command = command;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 800,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.AllowUserResizing = true;
        IsMouseVisible = true;
        _display.Wireframe = command.Wireframe;
        _display.Culling = command.Culling;
    }

    protected override void LoadContent()
    {
        _model = _loading.GetAwaiter().GetResult();          // окно создавалось, пока модель читалась
        _renderer = new ModelRenderer(GraphicsDevice, _model);
        _camera.Yaw = MathHelper.ToRadians(_command.Yaw);
        _camera.Pitch = MathHelper.ToRadians(_command.Pitch);
        Frame();
        _mouse = Mouse.GetState();
        _keys = Keyboard.GetState();
        UpdateTitle();
        Program.Out.WriteLine(Stats.Line(_model, _renderer.UndecodedTextures, Program.ElapsedMilliseconds));
        if (_command.Shot is null) _update = Task.Run(Updater.Find);
    }

    void Frame() => _camera.Frame(_model.Bounds, GraphicsDevice.Viewport.AspectRatio);

    string Animations => _model.Animations.Count == 0 ? "нет" : _model.DescribeAnimations();

    protected override void Update(GameTime gameTime)
    {
        MouseState mouse = Mouse.GetState();
        int dx = mouse.X - _mouse.X, dy = mouse.Y - _mouse.Y;
        if (mouse.LeftButton == ButtonState.Pressed) _camera.Rotate(dx, dy);
        if (mouse.RightButton == ButtonState.Pressed) _camera.Pan(dx, dy, GraphicsDevice.Viewport.Height);
        if (mouse.ScrollWheelValue != _mouse.ScrollWheelValue) _camera.Zoom(mouse.ScrollWheelValue - _mouse.ScrollWheelValue);
        _mouse = mouse;

        KeyboardState keys = Keyboard.GetState();
        if (keys.IsKeyDown(Keys.Escape))
        {
            Exit();
            return;
        }
        bool changed = false;
        if (JustPressed(keys, Keys.F)) Frame();
        if (JustPressed(keys, Keys.Z)) changed = _display.Wireframe ^= true;
        if (JustPressed(keys, Keys.C))
        {
            _display.Culling = _display.Culling switch { Culling.Back => Culling.Front, Culling.Front => Culling.Off, _ => Culling.Back };
            changed = true;
        }
        if (changed) UpdateTitle();
        _keys = keys;

        // новая версия найдена — спрашиваем, не мешая смотреть
        if (_update is { IsCompleted: true } && !_updateAsked)
        {
            _updateAsked = true;
            if (_update.Result is { } release && !Updater.IsSkipped(release)) Offer(release);
        }
        base.Update(gameTime);
    }

    /// <summary>Вопрос об обновлении: обновить, пропустить эту версию или напомнить позже.</summary>
    void Offer(Updater.Release release)
    {
        int answer = Diagnostics.Ask($"Вышла MeshViewer {release.Version} — у вас {App.Version}.\n\nДа — обновить: скачает, установит и перезапустит\nНет — пропустить эту версию\nОтмена — напомнить в следующий раз");
        if (answer == 7) Updater.Skip(release);
        else if (answer == 6 && Updater.Install(release.Archive, _command.Again, quiet: false)) Exit();
    }

    bool JustPressed(KeyboardState now, Keys key) => now.IsKeyDown(key) && _keys.IsKeyUp(key);

    void UpdateTitle()
    {
        string culling = _display.Culling switch { Culling.Back => "задние", Culling.Front => "лицевые", _ => "никакие" };
        string animations = _model.Animations.Count > 0 ? $" · анимации: {_model.DescribeAnimations()}" : "";
        Window.Title = $"{_model.Name} — MeshViewer {App.Version}   |   {_model.Vertices:N0} вершин · {_model.Triangles:N0} треугольников" +
                       $" · материалов {_model.Materials.Count} · текстур {_model.Images.Count - _renderer!.UndecodedTextures}/{_model.Images.Count}{animations}" +
                       $"   |   ЛКМ — вращение · колесо — масштаб · ПКМ — панорама · F — вписать · Z — каркас" +
                       (_display.Wireframe ? " (включён)" : "") + $" · C — отсекать: {culling} · Esc — выход";
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(ModelRenderer.Background);
        _model.UpdateTransforms();                       // поза узлов может меняться (анимация) — матрицы считаются каждый кадр
        _renderer!.Render(_model, _camera, _display);

        if (_command.Shot is not null && ++_frames == 2)
        {
            Shot.Save(GraphicsDevice, _command.Shot);
            Exit();
        }
        base.Draw(gameTime);
    }
}
