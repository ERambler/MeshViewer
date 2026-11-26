using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]     // просмотрщик только для Windows: журнал, реестр и Win32 — анализатору это известно

namespace MeshViewer;

/// <summary>
/// Имя, версия и адреса программы. Версия берётся из сборки — то есть из &lt;Version&gt; в MeshViewer.csproj:
/// она же в свойствах exe, в git-теге (v0.1), в релизе и в обновителе.
/// </summary>
public static class App
{
    public const string Name = "MeshViewer";
    public const string Repository = "ERambler/MeshViewer";
    public static string Site => $"https://github.com/{Repository}";
    public static string Releases => $"{Site}/releases";

    public static Version Number { get; } = typeof(App).Assembly.GetName().Version!;

    /// <summary>«0.1», а не «0.1.0.0».</summary>
    public static string Version { get; } = Number.Build == 0 ? $"{Number.Major}.{Number.Minor}" : $"{Number.Major}.{Number.Minor}.{Number.Build}";
}
