using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;

namespace MeshViewer;

/// <summary>
/// Самообновление из GitHub Releases. При старте в фоне узнаёт последний релиз; если он новее — предлагает
/// обновиться. Скачивает zip, а файлы подменяет уже новый exe: работающий файл нельзя перезаписать, но можно
/// переименовать — старое стирается, когда прежний процесс выйдет.
/// </summary>
public static class Updater
{
    const string Latest = "https://api.github.com/repos/" + App.Repository + "/releases/latest";
    static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), App.Name);

    /// <summary>Релиз на GitHub: версия («0.2») и адрес zip-архива.</summary>
    public sealed record Release(string Version, Version Number, string Archive);

    /// <summary>Последний релиз — новее он или нет; null — нет сети или релизов.</summary>
    public static Release? Fetch()
    {
        try
        {
            using var json = JsonDocument.Parse(NativeHttp.Get(Latest, TimeSpan.FromSeconds(8)));
            JsonElement root = json.RootElement;
            string tag = root.TryGetProperty("tag_name", out var name) ? name.GetString() ?? "" : "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? number)) return null;
            string? archive = (from asset in root.GetProperty("assets").EnumerateArray()
                               where asset.TryGetProperty("browser_download_url", out _)
                               select asset.GetProperty("browser_download_url").GetString())
                .FirstOrDefault(url => url is not null && url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            return archive is null ? null : new Release(tag.TrimStart('v', 'V'), number, archive);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;        // нет сети или GitHub ответил неожиданно — не повод мешать смотреть модель
        }
    }

    /// <summary>Релиз, который стоит предложить: новее текущей версии и не пропущенный.</summary>
    public static Release? Find()
    {
        Release? release = Fetch();
        return release is null || release.Number <= App.Number || release.Version == Skipped ? null : release;
    }

    static string? Skipped => Registry.CurrentUser.OpenSubKey(@"Software\" + App.Name)?.GetValue("SkipVersion") as string;

    public static bool IsSkipped(Release release) => release.Version == Skipped;

    public static void Skip(Release release)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\" + App.Name);
        key.SetValue("SkipVersion", release.Version);
    }

    /// <summary>Скачивает обновление (или берёт готовый zip), ставит его на место программы и запускает уже новую версию.</summary>
    public static bool Install(string source, string[] again, bool quiet)
    {
        try
        {
            Directory.CreateDirectory(Home);
            string archive = File.Exists(source) ? source : Path.Combine(Home, "update.zip");      // локальный zip — тот же путь установки
            if (archive != source)
            {
                // сеть бывает капризной — несколько попыток, прежде чем признать неудачу
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.WriteAllBytes(archive, NativeHttp.Get(source, TimeSpan.FromMinutes(10)));
                        break;
                    }
                    catch (Exception e) when (attempt < 3 && e is not OutOfMemoryException)
                    {
                        Thread.Sleep(2000);
                    }
                }
            }
            string staging = Path.Combine(Home, "update");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(archive, staging);
            if (!Directory.EnumerateFiles(staging, App.Name + ".exe", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("в архиве нет MeshViewer.exe");
            bool done = HandOver(staging, again, quiet);
            if (done && archive != source) File.Delete(archive);        // скачанное больше не нужно
            return done;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            string message = $"Не удалось обновить: {e.Message}";
            if (quiet) Program.Out.WriteLine(message);
            else Diagnostics.Say(message);
            return false;
        }
    }

    /// <summary>
    /// Запускает новый exe из распакованного архива (если папка программы закрыта на запись — с правами
    /// администратора, UAC), дожидается, пока он подменит файлы, перезапускает программу и выходит.
    /// </summary>
    static bool HandOver(string staging, string[] again, bool quiet)
    {
        string target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string marker = Path.Combine(Home, "ready");
        File.Delete(marker);
        bool admin = !Writable(target);
        var start = new ProcessStartInfo
        {
            FileName = Directory.EnumerateFiles(staging, App.Name + ".exe", SearchOption.AllDirectories).First(),
            Arguments = $"--update-apply {Quote(target)} {Environment.ProcessId} {Quote(staging)} {Quote(marker)}",
            UseShellExecute = admin,
            Verb = admin ? "runas" : "",
            CreateNoWindow = true,
        };
        try
        {
            Process.Start(start);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)      // пользователь отказал в правах
        {
            Report("Обновление отменено: не даны права администратора.", quiet);
            return false;
        }
        for (int i = 0; i < 900 && !File.Exists(marker); i++) Thread.Sleep(100);     // UAC может ждать ответа
        if (!File.Exists(marker))
        {
            Report("Не удалось подменить файлы программы.", quiet);
            return false;
        }
        File.Delete(marker);
        Process.Start(new ProcessStartInfo(Path.Combine(target, App.Name + ".exe"), string.Join(' ', again.Select(Quote)))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        });
        return true;
    }

    /// <summary>
    /// Выполняет уже новый exe (иногда с правами администратора): подменяет файлы, ждёт, пока прежний процесс
    /// перезапустит программу и выйдет, и стирает старое.
    /// </summary>
    public static void Apply(string target, int parent, string staging, string marker)
    {
        try
        {
            // помощнику могут дать права администратора: работаем только со своей папкой подготовки, никакую другую не трогаем
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging)), Path.Combine(Home, "update"), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("папка подготовки обновления не принадлежит MeshViewer");
            string source = Path.GetDirectoryName(Directory.EnumerateFiles(staging, App.Name + ".exe", SearchOption.AllDirectories).First())!;
            foreach (string file in Directory.EnumerateFiles(source))
            {
                if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                string destination = Path.Combine(target, Path.GetFileName(file));
                try
                {
                    File.Copy(file, destination, overwrite: true);
                }
                catch (IOException)      // занят работающей программой: освобождаем имя переименованием, старое сотрём после её выхода
                {
                    File.Move(destination, destination + ".old", overwrite: true);
                    File.Copy(file, destination, overwrite: true);
                }
            }
            File.WriteAllText(marker, "");
            try
            {
                using var process = Process.GetProcessById(parent);
                process.WaitForExit(60000);
            }
            catch (ArgumentException)
            {
                // прежний процесс уже вышел
            }
            foreach (string old in Directory.EnumerateFiles(target, "*.old")) File.Delete(old);
            // сам помощник лежит в staging и себя не удалит — папку подметает команда после его выхода (ping — задержка без консоли)
            Process.Start(new ProcessStartInfo("cmd.exe", $"/s /c \" ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{staging}\" \"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Diagnostics.Log("Установка обновления: " + e);
        }
    }

    static void Report(string message, bool quiet)
    {
        if (quiet) Program.Out.WriteLine(message);
        else Diagnostics.Say(message);
    }

    static bool Writable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, "meshviewer.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    static string Quote(string argument) => argument.Contains(' ') || argument.Length == 0 ? $"\"{argument}\"" : argument;
}
