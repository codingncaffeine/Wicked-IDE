using System.Text;

namespace Wicked.Core.Engines;

/// <summary>
/// A size-capped log file for a child process's output: when it passes <c>maxBytes</c>, <c>name.log</c> becomes <c>name.1.log</c>
/// (and so on, <c>keep</c> files). Appends are serialised and flushed per line, because the value of this file is the last
/// lines before a crash. Used from the output-pump threads, never from a caller's thread.
/// </summary>
internal sealed class RotatingLog : IDisposable
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private long _size;

    public RotatingLog(string path, long maxBytes = 4 * 1024 * 1024, int keep = 3)
    {
        _path = path;
        _maxBytes = maxBytes;
        _keep = keep;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    public void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                _writer ??= Open();
                _writer.WriteLine(line);
                _writer.Flush();
                _size += line.Length + 1;
                if (_size > _maxBytes) Rotate();
            }
            catch (IOException ex)
            {
                Log.Warn($"Could not write {_path}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter Open()
    {
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _size = stream.Length;
        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        var stem = Path.ChangeExtension(_path, null);
        var extension = Path.GetExtension(_path);
        for (var i = _keep - 1; i >= 1; i--)
        {
            var from = i == 1 ? _path : $"{stem}.{i - 1}{extension}";
            if (File.Exists(from)) File.Move(from, $"{stem}.{i}{extension}", overwrite: true);
        }

        _size = 0;
    }
}
