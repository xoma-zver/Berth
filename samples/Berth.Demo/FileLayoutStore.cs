using System;
using System.Diagnostics;
using System.IO;

namespace Berth.Demo;

/// <summary>
/// File-backed layout store of the desktop host. Writes are atomic — the document goes to a
/// temporary file first and replaces the target with a rename — so a crash mid-write never
/// leaves a torn file behind (a torn file would still be handled gracefully by
/// <see cref="LayoutFormatException"/>, but the layout would be lost). IO errors are demoted
/// to «no stored layout» / «not saved this time» with a trace: persistence must never take
/// the application down.
/// </summary>
public sealed class FileLayoutStore : ILayoutStore
{
    private readonly string _path;

    /// <summary>Creates a store over the given file path.</summary>
    public FileLayoutStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>The default per-user location: ApplicationData/Berth.Demo/layout.json.</summary>
    public static FileLayoutStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Berth.Demo",
        "layout.json"));

    /// <inheritdoc/>
    public string? Load()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Berth.Demo: could not read the layout file '{0}': {1}", _path, exception.Message);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Save(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Berth.Demo: could not write the layout file '{0}': {1}", _path, exception.Message);
        }
    }
}
