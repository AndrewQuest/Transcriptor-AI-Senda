namespace Transcriptor.Core;

public sealed record InstalledModel(string Path, string Name, long Bytes, bool Available = true);

/// <summary>Only manages complete GGML files inside Senda's own model directory.</summary>
public sealed class ModelLibrary(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public IReadOnlyList<InstalledModel> Read()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        EnsureNotLinked(DirectoryPath);
        var models = new List<InstalledModel>();
        Span<byte> magic = stackalloc byte[4];
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.bin"))
        {
            try
            {
                EnsureNotLinked(path);
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length >= 1_000_000 && stream.Read(magic) == 4 && BitConverter.ToUInt32(magic) == 0x67676d6c)
                    models.Add(new(Path.GetFullPath(path), Path.GetFileName(path), stream.Length));
            }
            catch (IOException) { /* Incomplete or locked imports are not installed models. */ }
            catch (UnauthorizedAccessException) { }
        }
        return models.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Delete(string path, string activePath)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), DirectoryPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("Solo puedes borrar modelos de la carpeta de Senda."));
        if (!string.IsNullOrEmpty(activePath) && SamePath(full, activePath))
            throw new InvalidOperationException(UiText.Get("Elige otro modelo predeterminado antes de borrar este."));
        EnsureNotLinked(full);
        if (!Read().Any(model => SamePath(model.Path, full)))
            throw new InvalidOperationException(UiText.Get("El modelo ya no está disponible."));
        File.Delete(full);
    }

    public static bool SamePath(string left, string right) =>
        !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void EnsureNotLinked(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(UiText.Get("La carpeta de modelos no puede contener enlaces."));
    }
}
