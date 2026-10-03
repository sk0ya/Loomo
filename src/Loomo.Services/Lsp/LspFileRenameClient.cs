using System;
using System.Collections.Generic;
using System.Reflection;
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
/// <see cref="ILspClient"/> から <see cref="ILspFileRenameClient"/> を取り出す。
///
/// <para><b>なぜリフレクションか:</b> 3 つのメンバーは Editor 側（<c>feature/will-rename-files</c>）で
/// <see cref="ILspClient"/> へ既定実装つきで足したもので、いまピン留めしている公開パッケージにはまだ無い。
/// 直接呼ぶとビルドが通らないので、実行時に<b>あれば使い、無ければ null</b>（＝この機能は眠ったまま、
/// 移動そのものは従来どおり）にしている。引数・戻り値は旧版にも在る型（ValueTuple・JsonElement・
/// <see cref="LspWorkspaceEdit"/>）だけなので、名前で引けば型の食い違いは起きない。
/// パッケージを上げたら、このクラスは <see cref="ILspClient"/> の直接呼び出しに置き換えて消す。</para>
/// </summary>
public static class LspFileRenameClient
{
    private static readonly PropertyInfo? CapabilitiesProperty =
        typeof(ILspClient).GetProperty("ServerCapabilities", typeof(JsonElement?));

    private static readonly MethodInfo? WillRenameMethod = typeof(ILspClient).GetMethod(
        "WillRenameFilesAsync",
        [typeof(IReadOnlyList<(string, string)>), typeof(CancellationToken)]);

    private static readonly MethodInfo? DidRenameMethod = typeof(ILspClient).GetMethod(
        "DidRenameFilesAsync",
        [typeof(IReadOnlyList<(string, string)>)]);

    /// <summary>参照中の Editor がファイル操作の口を持っているか。</summary>
    public static bool IsSupportedByEditor =>
        CapabilitiesProperty is not null && WillRenameMethod is not null && DidRenameMethod is not null;

    public static ILspFileRenameClient? For(ILspClient client)
    {
        if (client is ILspFileRenameClient direct) return direct;
        return IsSupportedByEditor ? new ReflectedClient(client) : null;
    }

    private sealed class ReflectedClient(ILspClient client) : ILspFileRenameClient
    {
        public JsonElement? ServerCapabilities => (JsonElement?)CapabilitiesProperty!.GetValue(client);

        public Task<LspWorkspaceEdit?> WillRenameFilesAsync(
            IReadOnlyList<(string OldUri, string NewUri)> files, CancellationToken ct = default)
            => (Task<LspWorkspaceEdit?>)WillRenameMethod!.Invoke(client, [files, ct])!;

        public Task DidRenameFilesAsync(IReadOnlyList<(string OldUri, string NewUri)> files)
            => (Task)DidRenameMethod!.Invoke(client, [files])!;
    }
}
