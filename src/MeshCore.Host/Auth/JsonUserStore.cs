using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshCore.Host.Auth;

/// <summary>Where users and the protected JWT key live. Swappable (e.g. for SQLite) without touching callers.</summary>
public interface IUserStore
{
    /// <summary>The current contents (an immutable snapshot).</summary>
    Task<AuthDocument> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="change"/> to the latest contents and saves the result, as one step:
    /// concurrent updates are serialised, so checks made inside <paramref name="change"/> hold.
    /// </summary>
    Task<T> UpdateAsync<T>(Func<AuthDocument, (AuthDocument Updated, T Result)> change, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores <see cref="AuthDocument"/> as <c>{ "Auth": { … } }</c> in <c>appsettings.auth.json</c>.
/// Writes go to a temp file that then replaces the original, so a crash never leaves a half-written file.
/// </summary>
public sealed class JsonUserStore(string path) : IUserStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private AuthDocument? _cache;

    public string FilePath { get; } = Path.GetFullPath(path);

    public async Task<AuthDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _cache) is { } cached)
        {
            return cached;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _cache ??= await LoadAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<T> UpdateAsync<T>(Func<AuthDocument, (AuthDocument Updated, T Result)> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var current = _cache ??= await LoadAsync(cancellationToken);
            var (updated, result) = change(current);
            if (!ReferenceEquals(updated, current))
            {
                await SaveAsync(updated, cancellationToken);
                Volatile.Write(ref _cache, updated);
            }
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<AuthDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new AuthDocument();
        }

        await using var stream = File.OpenRead(FilePath);
        var file = await JsonSerializer.DeserializeAsync<AuthFile>(stream, Json, cancellationToken);
        return file?.Auth ?? new AuthDocument();
    }

    private async Task SaveAsync(AuthDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, new AuthFile(document), Json, cancellationToken);
        }
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Dispose() => _lock.Dispose();

    private sealed record AuthFile(AuthDocument Auth);
}
