using System.Text.Json;

namespace TimeLedger;

/// <summary>Loads and saves the <see cref="DataFile"/> as a single JSON document.
/// Saves are atomic: the new content is written to a temp file in the same
/// directory and then moved over the old file, so a crash mid-write can never
/// leave a half-written data file behind.</summary>
public sealed class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public JsonStore(string path) => _path = path;

    /// <summary>Default location: <c>~/.timeledger/data.json</c>, overridable
    /// with the TIMELEDGER_DATA environment variable (handy for tests).</summary>
    public static string DefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("TIMELEDGER_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".timeledger", "data.json");
    }

    public DataFile Load()
    {
        if (!File.Exists(_path)) return new DataFile();
        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<DataFile>(json, Options) ?? new DataFile();
    }

    public void Save(DataFile data)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
