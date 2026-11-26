using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MeshViewer;

/// <summary>
/// Куда попадает беда. Консоли у программы нет, поэтому ошибка показывается сообщением (через Win32 — работает
/// и без SDL) и одновременно пишется в журнал Windows «Приложение»: сбой не потеряется, даже если окно никто
/// не увидел. Необработанные исключения попадают туда же.
/// </summary>
public static class Diagnostics
{
    const string EventLog = @"SYSTEM\CurrentControlSet\Services\EventLog\Application";
    const uint Error = 0x10, Info = 0x40, Question = 0x23;        // MB_ICONERROR, MB_ICONINFORMATION, MB_YESNOCANCEL | MB_ICONQUESTION
    const uint Topmost = 0x1000;                                  // MB_SYSTEMMODAL — поверх остальных окон
    static string? _source;
    static bool _sourceKnown;

    /// <summary>Подписывается на исключения, которые иначе убили бы процесс молча.</summary>
    public static void Watch()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            string message = "Аварийное завершение: " + (args.ExceptionObject is Exception error ? error : args.ExceptionObject);
            Log(message);
            if (args.IsTerminating) Show(message, Error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("Фоновая задача: " + args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Ошибка: сообщение на экран и запись в журнал.</summary>
    public static void Say(string message)
    {
        Log(message);
        Show(message, Error);
    }

    public static void Note(string message) => Show(message, Info);

    /// <summary>Вопрос «да / нет / отмена»: 6, 7 или 2.</summary>
    public static int Ask(string message) => Show(message, Question);

    static int Show(string message, uint kind) => MessageBoxW(IntPtr.Zero, Limit(message), $"{App.Name} {App.Version}", kind | Topmost);

    static string Limit(string message) => message.Length <= 8000 ? message : message[..8000] + "…";

    /// <summary>Запись в журнал «Приложение». Без своего источника (его создаёт установка с правами) пишет в готовый источник .NET.</summary>
    public static void Log(string message)
    {
        message = Limit($"[{App.Name} {App.Version}] {message}");
        try
        {
            if (Source() is { } source)
            {
                IntPtr handle = RegisterEventSourceW(IntPtr.Zero, source);
                if (handle == IntPtr.Zero) return;
                try
                {
                    ReportEventW(handle, 1, 0, 0, IntPtr.Zero, 1, 0, [message], IntPtr.Zero);     // EVENTLOG_ERROR_TYPE
                }
                finally
                {
                    DeregisterEventSource(handle);
                }
            }
            else
            {
                Directory.CreateDirectory(Home);
                File.AppendAllText(Path.Combine(Home, "errors.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // журнал недоступен — сообщение на экране уже было
        }
    }

    static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), App.Name);

    static string? Source()
    {
        if (_sourceKnown) return _source;
        foreach (string name in new[] { App.Name, ".NET Runtime" })
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{EventLog}\{name}");
            if (key is not null)
            {
                _source = name;
                break;
            }
        }
        _sourceKnown = true;
        return _source;
    }

    /// <summary>Источник «MeshViewer» в журнале: создаёт установка для всех пользователей (нужны права администратора).</summary>
    public static void RegisterSource()
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey($@"{EventLog}\{App.Name}");
        // библиотека .NET с общим шаблоном «%1»: просмотр событий показывает текст записи как есть
        string messages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll");
        key.SetValue("EventMessageFile", File.Exists(messages) ? messages : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mscoree.dll"));
        key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
        _sourceKnown = false;
    }

    public static void DropSource()
    {
        Registry.LocalMachine.DeleteSubKeyTree($@"{EventLog}\{App.Name}", throwOnMissingSubKey: false);
        _sourceKnown = false;
    }

    [DllImport("user32", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int MessageBoxW(IntPtr window, string message, string caption, uint kind);

    [DllImport("advapi32", EntryPoint = "RegisterEventSourceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr RegisterEventSourceW(IntPtr server, string source);

    [DllImport("advapi32", EntryPoint = "ReportEventW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]     // без Unicode строки уходят как ANSI — и в журнале каша
    static extern bool ReportEventW(IntPtr handle, ushort type, ushort category, uint eventId, IntPtr user, ushort count, uint size, string[] strings, IntPtr data);

    [DllImport("advapi32")]
    static extern bool DeregisterEventSource(IntPtr handle);
}
