using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MeshViewer;

/// <summary>Для кого ставим: всех пользователей (Program Files, нужны права администратора) или только текущего (профиль).</summary>
public enum Scope { Machine, User }

/// <summary>
/// Установка тем же exe, без отдельного инсталлятора: --install копирует программу на место и регистрирует
/// классы .glb и .obj, --uninstall возвращает прежние сопоставления и всё убирает. Права администратора
/// просит сам (UAC); без них ставится в профиль пользователя.
/// </summary>
public static class Installer
{
    const string ProgId = "MeshViewer.Model";
    const string Settings = @"Software\" + App.Name;
    static readonly string[] Extensions = [".glb", ".obj"];

    public static string Location(Scope scope) => Path.Combine(scope == Scope.Machine
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        App.Name);

    public static bool IsInstalled(Scope scope) => File.Exists(Path.Combine(Location(scope), App.Name + ".exe"));

    /// <summary>Права есть, когда раздел классов в HKLM открывается на запись.</summary>
    public static bool IsAdmin
    {
        get
        {
            try
            {
                return Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes", writable: true) is not null;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>--install без уточнения: спрашивает, для кого ставить.</summary>
    public static void Choose()
    {
        int answer = Diagnostics.Ask("Куда установить MeshViewer?\n\nДа — для всех пользователей в Program Files (понадобится разрешение администратора)\nНет — только для меня, в профиль пользователя\nОтмена — ничего не менять");
        if (answer == 6) Install(Scope.Machine);
        else if (answer == 7) Install(Scope.User);
    }

    public static void Install(Scope scope)
    {
        if (scope == Scope.Machine && !IsAdmin)
        {
            Elevate("--install machine");
            return;
        }
        string directory = Location(scope);
        try
        {
            Copy(AppContext.BaseDirectory, directory);
        }
        catch (IOException e)
        {
            Diagnostics.Say($"Не удалось скопировать файлы в «{directory}» — возможно, MeshViewer запущен.\n{e.Message}");
            return;
        }
        catch (UnauthorizedAccessException)
        {
            Diagnostics.Say($"Нет прав на папку «{directory}».");
            return;
        }
        Register(scope, directory);
        Changed();
        string[] taken = [.. Extensions.Where(Taken)];
        Diagnostics.Note($"{App.Name} {App.Version} установлен — {(scope == Scope.Machine ? "для всех пользователей" : "для текущего пользователя")}.\n\n{Path.Combine(directory, App.Name + ".exe")}\n\n" +
            (taken.Length == 0 ? "Файлы .glb и .obj открываются двойным щелчком." : "Файлы .glb и .obj открываются через «Открыть с помощью» → MeshViewer."));
        OfferDefaults(taken);
    }

    /// <summary>--uninstall без уточнения: убирает обе установки — сначала свою, затем общую (для неё попросит права).</summary>
    public static void Remove()
    {
        if (!IsInstalled(Scope.User) && !IsInstalled(Scope.Machine))
        {
            Diagnostics.Note("MeshViewer не установлен.");
            return;
        }
        if (IsInstalled(Scope.User)) Uninstall(Scope.User);
        if (IsInstalled(Scope.Machine)) Uninstall(Scope.Machine);
    }

    public static void Uninstall(Scope scope)
    {
        if (scope == Scope.Machine && !IsAdmin)
        {
            Elevate("--uninstall machine");
            return;
        }
        bool installed = IsInstalled(scope);
        Unregister(scope);
        RemoveFiles(scope);
        Changed();
        Diagnostics.Note(installed ? "MeshViewer удалён." : "Записи MeshViewer удалены.");
    }

    static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string name = Path.GetFileName(file);
            bool wanted = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            string destination = Path.Combine(target, name);
            if (wanted && !string.Equals(Path.GetFullPath(file), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                File.Copy(file, destination, overwrite: true);
        }
    }

    static RegistryKey Hive(Scope scope) => scope == Scope.Machine ? Registry.LocalMachine : Registry.CurrentUser;

    static void Register(Scope scope, string directory)
    {
        string exe = Path.Combine(directory, App.Name + ".exe");
        string run = $"\"{exe}\" \"%1\"";
        using RegistryKey hive = Hive(scope);
        using RegistryKey classes = hive.CreateSubKey(@"Software\Classes");
        using (RegistryKey model = classes.CreateSubKey(ProgId))
        {
            model.SetValue(null, "3D-модель MeshViewer");        // так Explorer называет тип файла
            using RegistryKey icon = model.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            using RegistryKey command = model.CreateSubKey(@"shell\open\command");
            command.SetValue(null, run);
        }

        // запись в «Приложения по умолчанию» (Параметры Windows): оттуда программу можно назначить для типа файла
        string application = $@"Software\Classes\Applications\{App.Name}.exe";
        using (RegistryKey app = hive.CreateSubKey(application))
        {
            app.SetValue("FriendlyAppName", App.Name);
            app.SetValue("DefaultIcon", $"\"{exe}\",0");
            using RegistryKey command = app.CreateSubKey(@"shell\open\command");
            command.SetValue(null, run);
            using RegistryKey types = app.CreateSubKey("SupportedTypes");
            foreach (string extension in Extensions) types.SetValue(extension, "");
        }
        using (RegistryKey registered = hive.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(App.Name, application);

        using RegistryKey own = hive.CreateSubKey(Settings);
        own.SetValue("InstallDir", directory);
        own.SetValue("Version", App.Version);
        foreach (string extension in Extensions)
        {
            bool created = classes.OpenSubKey(extension) is null;
            using RegistryKey key = classes.CreateSubKey(extension);
            if (created) own.SetValue("Created" + extension, 1);                                             // при удалении уберём раздел целиком
            if (key.GetValue(null) is string previous && previous != ProgId) own.SetValue("Previous" + extension, previous);   // вернём при удалении
            key.SetValue(null, ProgId);
            using RegistryKey openWith = key.CreateSubKey("OpenWithProgIds");
            openWith.SetValue(ProgId, "");
        }

        using RegistryKey entry = hive.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{App.Name}");
        entry.SetValue("DisplayName", App.Name);
        entry.SetValue("DisplayVersion", App.Version);
        entry.SetValue("Publisher", "ERambler");
        entry.SetValue("URLInfoAbout", App.Site);
        entry.SetValue("InstallLocation", directory);
        entry.SetValue("DisplayIcon", $"\"{exe}\",0");
        entry.SetValue("UninstallString", $"\"{exe}\" --uninstall {(scope == Scope.Machine ? "machine" : "user")}");
        entry.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        entry.SetValue("EstimatedSize", (int)(new DirectoryInfo(directory).EnumerateFiles().Sum(file => file.Length) / 1024), RegistryValueKind.DWord);
        entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
        entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        entry.SetValue("WindowsInstaller", 0, RegistryValueKind.DWord);
        if (scope == Scope.Machine) Diagnostics.RegisterSource();
    }

    static void Unregister(Scope scope)
    {
        using RegistryKey hive = Hive(scope);
        using RegistryKey classes = hive.CreateSubKey(@"Software\Classes");
        using RegistryKey own = hive.CreateSubKey(Settings);
        foreach (string extension in Extensions)
        {
            if (own.GetValue("Created" + extension) is not null)
            {
                classes.DeleteSubKeyTree(extension, throwOnMissingSubKey: false);        // раздел существовал только из-за нас
                continue;
            }
            using RegistryKey key = classes.CreateSubKey(extension);
            if ((key.GetValue(null) as string) == ProgId)
            {
                if (own.GetValue("Previous" + extension) is string previous) key.SetValue(null, previous);
                else key.DeleteValue(string.Empty, throwOnMissingValue: false);
            }
            using RegistryKey openWith = key.CreateSubKey("OpenWithProgIds");
            openWith.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        classes.DeleteSubKeyTree($@"Applications\{App.Name}.exe", throwOnMissingSubKey: false);
        using (RegistryKey registered = hive.CreateSubKey(@"Software\RegisteredApplications"))
            registered.DeleteValue(App.Name, throwOnMissingValue: false);
        hive.DeleteSubKey(Settings, throwOnMissingSubKey: false);
        hive.DeleteSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{App.Name}", throwOnMissingSubKey: false);
        if (scope == Scope.Machine) Diagnostics.DropSource();
    }

    /// <summary>
    /// Windows 10/11 хранит выбор пользователя (UserChoice) и намеренно не даёт сменить его записью в реестре —
    /// программные входы в системный диалог выбора она тоже закрывает. Если тип файла закреплён за другой
    /// программой, остаётся сам пользователь: объясняем короткий путь и по желанию открываем Параметры.
    /// </summary>
    static void OfferDefaults(string[] taken)
    {
        if (taken.Length == 0) return;
        int answer = Diagnostics.Ask($"Windows сейчас открывает {string.Join(", ", taken)} другой программой, и сменить это из программы нельзя.\n\nКороткий путь: в Проводнике — правой кнопкой по такому файлу → «Открыть с помощью» → «Выбрать другое приложение» → MeshViewer → «Всегда использовать это приложение».\n\nОткрыть Параметры Windows («Приложения по умолчанию»)?");
        if (answer == 6)
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }

    static bool Taken(string extension)
    {
        using RegistryKey? choice = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice");
        return choice?.GetValue("ProgId") is string progId && progId != ProgId;
    }

    /// <summary>Стирает папку установки. Сам запущенный exe удалить нельзя — его догоняет команда после выхода.</summary>
    static void RemoveFiles(Scope scope)
    {
        string directory = Location(scope);
        if (!Directory.Exists(directory)) return;
        string running = Environment.ProcessPath ?? "";
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            if (!string.Equals(file, running, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        if (!string.Equals(Path.GetDirectoryName(running), directory, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(directory, recursive: true);
            return;
        }
        // ping вместо timeout: задержка без консоли (timeout без неё сразу выходит — и папку убирают под работающим exe)
        Process.Start(new ProcessStartInfo("cmd.exe", $"/s /c \" ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{directory}\" \"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>Перезапускает себя с правами администратора: Windows спрашивает пользователя (UAC).</summary>
    static void Elevate(string arguments)
    {
        try
        {
            using Process? child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            child?.WaitForExit();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)      // пользователь отказал
        {
            Diagnostics.Note("Нужны права администратора, а запрос отменён.\nМожно установить только для текущего пользователя:  MeshViewer.exe --install user");
        }
    }

    static void Changed() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);     // SHCNE_ASSOCCHANGED, SHCNF_IDLIST

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint @event, uint flags, IntPtr first, IntPtr second);
}
