using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>
/// ファイルの移動・改名を言語サーバーへ知らせる口（<c>workspace/willRenameFiles</c>・<c>didRenameFiles</c>）と、
/// 送ってよいかを決める <c>capabilities</c> の生 JSON。メンバーは Editor の <see cref="ILspClient"/> に
/// 足したもの（<c>ServerCapabilities</c> / <c>WillRenameFilesAsync</c> / <c>DidRenameFilesAsync</c>）と同じ形。
/// テストの偽クライアントはこれを直接実装する。
/// </summary>
public interface ILspFileRenameClient
{
    JsonElement? ServerCapabilities { get; }
    Task<LspWorkspaceEdit?> WillRenameFilesAsync(
        IReadOnlyList<(string OldUri, string NewUri)> files, CancellationToken ct = default);
    Task DidRenameFilesAsync(IReadOnlyList<(string OldUri, string NewUri)> files);
}

/// <summary>
/// <see cref="ILspClient"/> から <see cref="ILspFileRenameClient"/> を取り出す。テストの偽クライアントは
/// <see cref="ILspFileRenameClient"/> を直接実装しているのでそのまま使い、本物は <see cref="ILspClient"/> の
/// 同名メンバー（Editor 1.0.95 で追加）へ委ねる。
/// </summary>
public static class LspFileRenameClient
{
    public static ILspFileRenameClient For(ILspClient client) =>
        client as ILspFileRenameClient ?? new DirectClient(client);

    private sealed class DirectClient(ILspClient client) : ILspFileRenameClient
    {
        public JsonElement? ServerCapabilities => client.ServerCapabilities;

        public Task<LspWorkspaceEdit?> WillRenameFilesAsync(
            IReadOnlyList<(string OldUri, string NewUri)> files, CancellationToken ct = default)
            => client.WillRenameFilesAsync(files, ct);

        public Task DidRenameFilesAsync(IReadOnlyList<(string OldUri, string NewUri)> files)
            => client.DidRenameFilesAsync(files);
    }
}
