using System.Text.Json;

namespace QnocrPoc.Shadow;

/// <summary>Rolls active JSONL files while retaining every archived audit segment.</summary>
public static class RunLogWriter
{
    public const long MaxActiveBytes = 8L * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Append<T>(string directory, string fileName, T value)
    {
        var line = JsonSerializer.Serialize(value) + Environment.NewLine;
        lock (Gate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path) && new FileInfo(path).Length + System.Text.Encoding.UTF8.GetByteCount(line) > MaxActiveBytes)
            {
                var archive = Path.Combine(directory, "log-archive");
                Directory.CreateDirectory(archive);
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var destination = Path.Combine(archive,
                    stem + "." + DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fffffff") + "." + Guid.NewGuid().ToString("N") + ".jsonl");
                File.Move(path, destination);
            }
            File.AppendAllText(path, line);
        }
    }

    public static long SizeBytes(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
        : 0;
}
