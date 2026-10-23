using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Transcriptor.Core;

/// <summary>Downloads an immutable model revision and verifies the published LFS SHA-256.</summary>
public sealed class ModelDownload(HttpClient http)
{
    public const string Repository = "ggerganov/whisper.cpp";
    public const string FileName = "ggml-base-q5_1.bin";
    public const string SmallFileName = "ggml-small-q5_1.bin";

    public async Task<string> DownloadAsync(string directory, IProgress<double>? progress, CancellationToken cancellationToken, string fileName = FileName)
    {
        if (fileName is not (FileName or SmallFileName)) throw new ArgumentException(UiText.Get("Elige un modelo del catálogo de Senda."), nameof(fileName));
        using var metadata = await http.GetFromJsonAsync<JsonDocument>(
            $"https://huggingface.co/api/models/{Repository}", cancellationToken)
            ?? throw new InvalidDataException(UiText.Get("No se pudo consultar la versión del modelo."));
        var revision = metadata.RootElement.GetProperty("sha").GetString()!;
        if (revision.Length != 40 || !revision.All(Uri.IsHexDigit)) throw new InvalidDataException(UiText.Get("Versión del modelo no válida."));
        using var tree = await http.GetFromJsonAsync<JsonDocument>(
            $"https://huggingface.co/api/models/{Repository}/tree/{revision}?recursive=false", cancellationToken)
            ?? throw new InvalidDataException(UiText.Get("No se pudo consultar la firma del modelo."));
        var file = tree.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("path").GetString() == fileName);
        if (file.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException(UiText.Get("El modelo no aparece en el repositorio oficial."));
        var hash = file.GetProperty("lfs").GetProperty("oid").GetString()!;
        var size = file.GetProperty("size").GetInt64();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || size <= 0) throw new InvalidDataException(UiText.Get("La suma de verificación del modelo no es válida."));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path) && new FileInfo(path).Length == size)
        {
            await using var existing = File.OpenRead(path);
            var existingHash = await SHA256.HashDataAsync(existing, cancellationToken);
            if (Convert.ToHexString(existingHash).Equals(hash, StringComparison.OrdinalIgnoreCase))
            { progress?.Report(1); return path; }
        }
        using var response = await http.GetAsync($"https://huggingface.co/{Repository}/resolve/{revision}/{fileName}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await SaveVerifiedAsync(input, path, hash, size, progress, cancellationToken);
        return path;
    }

    public static async Task SaveVerifiedAsync(Stream input, string path, string sha256, long size, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > size) throw new InvalidDataException(UiText.Get("El tamaño del modelo no coincide con el publicado."));
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report((double)total / size);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (total != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(UiText.Get("La verificación SHA-256 del modelo falló. Se descartó la descarga."));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
