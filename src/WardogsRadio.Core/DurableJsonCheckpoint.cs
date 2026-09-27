using System.Text.Json;

namespace WardogsRadio.Core;

/// <summary>Small atomic checkpoint for an unfinished, user-reviewable operation.</summary>
public sealed class DurableJsonCheckpoint<T>(string path) where T : class
{
    public bool Exists => File.Exists(path);

    public void Save(T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(value));
        File.Move(pending, path, true);
    }

    public T Load() => JsonSerializer.Deserialize<T>(File.ReadAllText(path))
        ?? throw new InvalidDataException("The checkpoint is empty.");

    public void Clear()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
