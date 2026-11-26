namespace MeshViewer;

/// <summary>
/// Загрузка модели по пути. Формат выбирается по расширению — здесь будет жить сопоставление типов файлов
/// с просмотрщиком: новый формат — одна строка и свой загрузчик.
/// </summary>
public static class ModelLoader
{
    public static Model Load(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"Файл не найден: {full}");
        return Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".glb" => GlbLoader.Load(full),
            ".obj" => ObjLoader.Load(full),
            var extension => throw new NotSupportedException($"Формат «{extension}» не поддерживается: открываются .glb и .obj"),
        };
    }
}
