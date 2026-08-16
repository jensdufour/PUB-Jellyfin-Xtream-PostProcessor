using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.XtreamPostProcessor.State;

/// <summary>
/// Reads enrichment state produced by the legacy pipeline.
/// </summary>
public sealed class EnrichmentStateReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal async Task<EnrichmentState> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new EnrichmentState();
        }

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<EnrichmentState>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false) ?? new EnrichmentState();
        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported enrichment state schema {state.SchemaVersion}");
        }

        state.Items = new Dictionary<string, EnrichmentStateItem>(state.Items, StringComparer.OrdinalIgnoreCase);
        return state;
    }

    internal async Task WriteAsync(
        string path,
        EnrichmentState state,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"State path has no directory: {path}");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, true);
    }
}
