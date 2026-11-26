using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MeshViewer;

public static class Program
{
    /// <summary>Стандартный вывод в UTF-8: его читают сценарии (--shot, --info), консоли у WinExe нет.</summary>
    public static readonly TextWriter Out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

    /// <summary>Сколько прошло от старта самого процесса — так честнее меряется, как быстро программа готова.</summary>
    public static int ElapsedMilliseconds => (int)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;

    /// <summary>Коды выхода: 0 — порядок, 1 — ошибка, 2 — неверный вызов, 3 — рядом с exe нет SDL2.dll.</summary>
    public static int Main(string[] args)
    {
        Diagnostics.Watch();
        CommandLine command = CommandLine.Parse(args);
        if (command.Error is not null) return Fail(command, command.Error, 2);

        switch (command.Command)
        {
            case CommandLine.Action.Help:
                Out.WriteLine(CommandLine.Usage);
                return 0;
            case CommandLine.Action.Install:
                if (command.For is { } installTo) Installer.Install(installTo);
                else Installer.Choose();
                return 0;
            case CommandLine.Action.Uninstall:
                if (command.For is { } removeFrom) Installer.Uninstall(removeFrom);
                else Installer.Remove();
                return 0;
            case CommandLine.Action.ApplyUpdate:
                Updater.Apply(command.Target, command.Parent, command.Staging, command.Marker);
                return 0;
            case CommandLine.Action.Info:
                return Info(command.Model);
            case CommandLine.Action.CheckUpdate:
                return CheckUpdate();
            case CommandLine.Action.Update:
                return Update(command);
        }

        // Промежуточный exe из bin\ лежит без SDL2.dll: окно не откроется, процесс умрёт молча — говорим об этом сразу
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "SDL2.dll")))
            return Fail(command, $"Рядом с этим {App.Name}.exe нет SDL2.dll — окно открыть нечем.\nЗапущен: {Environment.ProcessPath}\nНужен {App.Name}.exe из папки publish — он лежит вместе со своими DLL.", 3);

        try
        {
            // модель читается параллельно с созданием окна — они не ждут друг друга
            using var game = new ViewerGame(Task.Run(() => ModelLoader.Load(command.Model)), command);
            game.Run();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Fail(command, $"Не удалось открыть «{Path.GetFileName(command.Model)}»:\n{e.Message}", 1);
        }
        return 0;
    }

    /// <summary>Безоконным режимам (сценарии, CI) ошибка уходит в стандартный вывод; иначе — сообщение и журнал Windows.</summary>
    static int Fail(CommandLine command, string message, int code)
    {
        if (command.Quiet) Out.WriteLine(message);
        else Diagnostics.Say(message);
        return code;
    }

    /// <summary>--info: загрузить модель и напечатать сведения, не открывая окна.</summary>
    static int Info(string path)
    {
        try
        {
            Model model = ModelLoader.Load(path);
            Out.WriteLine(Stats.Line(model, 0, Program.ElapsedMilliseconds));
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Out.WriteLine($"Не удалось загрузить «{Path.GetFileName(path)}»: {e.Message}");
            return 1;
        }
    }

    static int CheckUpdate()
    {
        Updater.Release? release = Updater.Fetch();
        Out.WriteLine(release is null ? "GitHub Releases недоступен или релизов нет."
            : release.Number > App.Number ? $"Доступна {release.Version} (у вас {App.Version}): {release.Archive}"
            : $"У вас последняя версия: {App.Version}.");
        return 0;
    }

    /// <summary>--update: обновиться из последнего релиза или из заданного zip-файла (адреса) и перезапуститься.</summary>
    static int Update(CommandLine command)
    {
        string? source = command.Source ?? Updater.Fetch()?.Archive;
        if (source is null)
        {
            Out.WriteLine("На GitHub Releases нет ни одного релиза.");
            return 1;
        }
        return Updater.Install(source, command.Again, quiet: true) ? 0 : 1;
    }
}

/// <summary>Аргументы командной строки.</summary>
public sealed class CommandLine
{
    public enum Action { View, Help, Info, Install, Uninstall, CheckUpdate, Update, ApplyUpdate }

    public Action Command { get; init; } = Action.View;

    public string Model { get; init; } = "";

    /// <summary>Снять кадр в файл и закрыться — для сценариев; null — обычный запуск.</summary>
    public string? Shot { get; init; }
    public float Yaw { get; init; } = 30f;
    public float Pitch { get; init; } = 18f;

    /// <summary>Каркас и какие грани отсекать — те же переключатели, что у клавиш Z и C.</summary>
    public bool Wireframe { get; init; }
    public Culling Culling { get; init; } = Culling.Back;

    /// <summary>Для кого ставим (--install, --uninstall); null — спросить или оба.</summary>
    public Scope? For { get; init; }

    /// <summary>Откуда обновляться (--update): zip-файл или адрес; null — последний релиз.</summary>
    public string? Source { get; init; }

    // --update-apply — куда ставить, кто ждёт, где лежат новые файлы и куда положить отметку «готово»
    public string Target { get; init; } = "";
    public int Parent { get; init; }
    public string Staging { get; init; } = "";
    public string Marker { get; init; } = "";

    /// <summary>С чем перезапустить программу после обновления — те же аргументы без --update.</summary>
    public string[] Again { get; init; } = [];
    public string? Error { get; init; }

    /// <summary>Без окон: результат и ошибки уходят в стандартный вывод.</summary>
    public bool Quiet => Shot is not null || Command is Action.Info or Action.CheckUpdate or Action.Update or Action.ApplyUpdate;

    public const string Usage = """
        MeshViewer модель.glb|модель.obj [параметры]      открыть модель
            --shot кадр.png [--yaw град] [--pitch град] [--wire] [--cull back|front|off]
                                                         снять кадр и закрыться
            --info модель                                загрузить и напечатать сведения, без окна
            --install [machine|user]                     установить и сопоставить .glb и .obj
            --uninstall [machine|user]                   убрать сопоставления и удалить программу
            --check-update                               есть ли новая версия на GitHub Releases
            --update [zip или адрес]                     обновиться (по умолчанию — последний релиз)
            --help                                       эта справка

        Мышь: ЛКМ — вращение, колесо — масштаб, ПКМ — панорама. Клавиши: F — вписать, Z — каркас, C — грани, Esc — выход.

        """;

    public static CommandLine Parse(string[] args)
    {
        var action = Action.View;
        string? model = null, shot = null, error = null, source = null, target = null, staging = null, marker = null;
        Scope? scope = null;
        float yaw = 30, pitch = 18;
        bool wireframe = false;
        Culling culling = Culling.Back;
        int parent = 0, updateAt = -1, updateLength = 0;

        string? Value(int i) => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            string? value = Value(i);
            if (argument == "--shot" && value is not null) { shot = value; i++; }
            else if (argument == "--yaw" && Number(value, ref yaw)) i++;
            else if (argument == "--pitch" && Number(value, ref pitch)) i++;
            else if (argument == "--wire") wireframe = true;
            else if (argument == "--cull" && value is not null && Enum.TryParse(value, true, out culling)) i++;
            else if (argument is "--help" or "-h" or "/?") action = Action.Help;
            else if (argument == "--info") action = Action.Info;
            else if (argument is "--install" or "--uninstall")
            {
                action = argument == "--install" ? Action.Install : Action.Uninstall;
                if (value is "machine" or "user")
                {
                    scope = value == "machine" ? Scope.Machine : Scope.User;
                    i++;
                }
            }
            else if (argument == "--check-update") action = Action.CheckUpdate;
            else if (argument == "--update")
            {
                action = Action.Update;
                updateAt = i;
                updateLength = value is null ? 1 : 2;
                source = value;
                i += updateLength - 1;
            }
            else if (argument == "--update-apply" && i + 4 < args.Length)
            {
                action = Action.ApplyUpdate;
                (target, parent, staging, marker) = (args[++i], int.Parse(args[++i], CultureInfo.InvariantCulture), args[++i], args[++i]);
            }
            else if (argument.StartsWith("--", StringComparison.Ordinal)) error ??= $"Не понял аргумент «{argument}»";
            else if (model is null) model = argument;
            else error ??= $"Модель одна, «{argument}» — лишний аргумент";
        }

        if (error is null && model is null && action is Action.View) error = "Укажите путь к модели:  MeshViewer модель.glb  (или .obj)";
        if (error is null && model is null && action is Action.Info) error = "После --info укажите путь к модели.";

        return new CommandLine
        {
            Command = action,
            Model = model ?? "",
            Shot = shot,
            Yaw = yaw,
            Pitch = pitch,
            Wireframe = wireframe,
            Culling = culling,
            For = scope,
            Source = source,
            Target = target ?? "",
            Parent = parent,
            Staging = staging ?? "",
            Marker = marker ?? "",
            Again = [.. args.Where((_, index) => index < updateAt || index >= updateAt + updateLength)],
            Error = error,
        };
    }

    static bool Number(string? text, ref float field)
    {
        if (text is null || !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) return false;
        field = value;
        return true;
    }
}
