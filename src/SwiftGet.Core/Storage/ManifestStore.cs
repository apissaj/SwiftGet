using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftGet.Core.Downloads;

namespace SwiftGet.Core.Storage;

/// <summary>
/// Baca/tulis <see cref="DownloadManifest"/> ke disk secara atomik.
/// <para>
/// Pola atomik: tulis ke file <c>.tmp</c> di folder yang sama, flush ke disk,
/// lalu <see cref="File.Move(string,string,bool)"/> menimpa manifest lama.
/// Ini mencegah manifest setengah jadi bila aplikasi mati saat menyimpan.
/// </para>
/// </summary>
public sealed class ManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ManifestStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        StateDirectory = stateDirectory;
        Directory.CreateDirectory(stateDirectory);
    }

    public string StateDirectory { get; }

    public string GetManifestPath(string id) =>
        Path.Combine(StateDirectory, id + ".swiftget");

    public string GetPartPath(string id) =>
        Path.Combine(StateDirectory, id + ".swiftpart");

    public async Task SaveAsync(DownloadManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        manifest.UpdatedUtc = DateTimeOffset.UtcNow;

        string finalPath = GetManifestPath(manifest.Id);
        string tempPath = finalPath + ".tmp";

        Directory.CreateDirectory(StateDirectory);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 4096, FileOptions.Asynchronous))
        {
            await fs.WriteAsync(payload, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tempPath, finalPath, overwrite: true);
    }

    public async Task<DownloadManifest?> LoadAsync(string id, CancellationToken ct = default)
    {
        string path = GetManifestPath(id);
        if (!File.Exists(path)) return null;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);

        try
        {
            var manifest = await JsonSerializer
                .DeserializeAsync<DownloadManifest>(fs, JsonOptions, ct)
                .ConfigureAwait(false);

            if (manifest is null) return null;
            if (manifest.Version != DownloadManifest.CurrentVersion) return null;

            return manifest;
        }
        catch (JsonException)
        {
            // Manifest rusak (mis. mati saat tulis sebelum fitur atomik ada).
            // Perlakuan aman: anggap tidak ada, biarkan unduhan dimulai ulang.
            return null;
        }
    }

    /// <summary>Muat semua manifest yang ada di folder state.</summary>
    public async Task<IReadOnlyList<DownloadManifest>> LoadAllAsync(CancellationToken ct = default)
    {
        var results = new List<DownloadManifest>();
        if (!Directory.Exists(StateDirectory)) return results;

        foreach (string file in Directory.EnumerateFiles(StateDirectory, "*.swiftget"))
        {
            ct.ThrowIfCancellationRequested();

            string id = Path.GetFileNameWithoutExtension(file);
            var manifest = await LoadAsync(id, ct).ConfigureAwait(false);
            if (manifest is not null) results.Add(manifest);
        }

        return results;
    }

    /// <summary>Hapus manifest dan file sementara untuk satu unduhan.</summary>
    public void Delete(string id)
    {
        string manifestPath = GetManifestPath(id);
        try
        {
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }

        PartFile.TryDelete(GetPartPath(id));
    }
}
