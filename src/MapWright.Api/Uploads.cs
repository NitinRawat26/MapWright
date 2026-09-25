using MapWright.Output.Readers;
using MapWright.Store;

namespace MapWright.Api;

internal static class Uploads
{
    public static async Task<List<InputFile>> Read(IFormFileCollection files, IReadOnlyCollection<string> extensions, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            throw new StoreException(StoreError.Invalid, "Upload at least one file in the 'files' form field.");
        }

        var inputs = new List<InputFile>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.FileName);
            if (!extensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            {
                throw new StoreException(StoreError.Invalid, $"'{name}' is not a supported file; use {string.Join(", ", extensions)}.");
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);
            inputs.Add(new(name, buffer.ToArray()));
        }

        return inputs;
    }

    public static string Text(InputFile input)
    {
        using var reader = new StreamReader(new MemoryStream(input.Content), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static DateTimeOffset Now(MapWrightDatabase database)
    {
        var now = database.Time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
    }
}
