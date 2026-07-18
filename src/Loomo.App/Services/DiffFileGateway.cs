using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Diff の復元・競合解決で必要なファイルアクセスを ViewModel から隔離する。
/// </summary>
public sealed class DiffFileGateway
{
    public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(path, content, cancellationToken);

    public void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
