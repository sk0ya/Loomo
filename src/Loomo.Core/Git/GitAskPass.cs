using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Core.Git;

/// <summary>
/// git・ssh が資格情報を求めたときの問い合わせを、部屋の中で受けるための約束（§24.101）。
/// <para>git は <c>GIT_ASKPASS</c>、ssh は <c>SSH_ASKPASS</c>（＋<c>SSH_ASKPASS_REQUIRE=force</c>）が指すプログラムを
/// 「問いの文面」を引数にして起こし、その標準出力を答えとして読む。Loomo はそこに小さな exe
/// （<c>sk0ya.Loomo.AskPass.exe</c>）を指し、exe は名前付きパイプで Loomo へ問いを渡して答えを待つ。
/// やり取りは 1 行の JSON を 1 往復だけ。答えはどこにも保存しない（覚えさせるのは GCM・ssh-agent の役目）。</para>
/// </summary>
public static class GitAskPass
{
    /// <summary>exe が問いを送る先（Loomo のパイプ名）。</summary>
    public const string PipeVariable = "LOOMO_ASKPASS_PIPE";

    /// <summary>どの git の操作の問いか（入力欄に出す。例 <c>git push</c>）。</summary>
    public const string ContextVariable = "LOOMO_ASKPASS_CONTEXT";

    /// <summary>ssh が問いの種類を伝える変数（OpenSSH 8.4+）。<c>confirm</c>＝はい／いいえ、<c>none</c>＝知らせるだけ。</summary>
    public const string SshPromptVariable = "SSH_ASKPASS_PROMPT";

    public const string ExecutableName = "sk0ya.Loomo.AskPass.exe";

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>exe が送る問い。</summary>
    /// <param name="Prompt">git・ssh が渡した文面そのまま（例 <c>Password for 'https://user@host': </c>）。</param>
    /// <param name="SshPrompt"><see cref="SshPromptVariable"/> の値（無ければ null）。</param>
    /// <param name="Context">どの操作の問いか（<see cref="ContextVariable"/>）。</param>
    public sealed record Request(string Prompt, string? SshPrompt = null, string? Context = null);

    /// <summary>Loomo の答え。<paramref name="Answered"/> が false なら取り消し（exe は失敗で終わる）。</summary>
    public sealed record Reply(bool Answered, string? Answer = null);

    /// <summary>問いの種類で入力欄の形を決める。純関数。</summary>
    public static GitCredentialPromptKind Classify(Request request)
    {
        // ssh が種類を言うなら、それに従う（FIDO 鍵の「触れてください」は none、ssh-agent の確認は confirm）。
        if (string.Equals(request.SshPrompt, "none", StringComparison.OrdinalIgnoreCase))
            return GitCredentialPromptKind.Notice;
        if (string.Equals(request.SshPrompt, "confirm", StringComparison.OrdinalIgnoreCase))
            return GitCredentialPromptKind.Confirm;

        var prompt = request.Prompt ?? "";
        // 初めての接続先のホスト鍵（Are you sure you want to continue connecting (yes/no/[fingerprint])?）。
        if (prompt.Contains("(yes/no", StringComparison.OrdinalIgnoreCase))
            return GitCredentialPromptKind.YesNo;
        if (prompt.Contains("password", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("PIN", StringComparison.Ordinal)
            || prompt.Contains("token", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("パスワード", StringComparison.Ordinal)
            || prompt.Contains("パスフレーズ", StringComparison.Ordinal))
            return GitCredentialPromptKind.Secret;
        // ユーザー名など。伏せずに見せる（打ち間違いに気づけるように）。
        return GitCredentialPromptKind.Text;
    }

    /// <summary>
    /// 問いを Loomo へ送り、答えを待つ（exe の側）。Loomo が居ない・途中で切れた・取り消されたら
    /// <c>Answered=false</c>。待つ時間に上限は設けない——git の側に時間制限があり、切れたら exe ごと止められる。
    /// </summary>
    public static async Task<Reply> AskAsync(string pipeName, Request request, CancellationToken cancellationToken = default)
    {
        try
        {
            // 非同期ハンドル必須（同期だと読み待ちが書きを塞ぐ。memory: named-pipe-sync-deadlock）。
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
            await WriteLineAsync(pipe, JsonSerializer.Serialize(request, Json), cancellationToken).ConfigureAwait(false);
            var line = await ReadLineAsync(pipe, cancellationToken).ConfigureAwait(false);
            return line is null
                ? new Reply(false)
                : JsonSerializer.Deserialize<Reply>(line, Json) ?? new Reply(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or UnauthorizedAccessException)
        {
            return new Reply(false);
        }
    }

    /// <summary>1 行書く（UTF-8・BOM なし・LF）。</summary>
    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var bytes = new UTF8Encoding(false).GetBytes(line + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>1 行読む。行の前に相手が切れたら null。StreamReader を使わないのは、読み過ぎて
    /// 次の読み（切断の検知）に渡すべきバイトを抱え込まないため。</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return null;
            if (one[0] == (byte)'\n')
                return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            buffer.WriteByte(one[0]);
        }
    }
}

/// <summary>問いの種類（入力欄の形）。</summary>
public enum GitCredentialPromptKind
{
    /// <summary>そのまま見せて打つ（ユーザー名など）。</summary>
    Text,
    /// <summary>伏せて打つ（パスワード・パスフレーズ・トークン・PIN）。</summary>
    Secret,
    /// <summary>はい／いいえを文字で答える（ホスト鍵の確認。答えは <c>yes</c>／<c>no</c>）。</summary>
    YesNo,
    /// <summary>はい／いいえを終了コードで答える（<c>SSH_ASKPASS_PROMPT=confirm</c>）。</summary>
    Confirm,
    /// <summary>知らせるだけ（<c>SSH_ASKPASS_PROMPT=none</c>。FIDO 鍵に触れる合図）。</summary>
    Notice,
}

/// <summary>資格情報の問いを人に見せて答えを得る（App が実装する）。</summary>
public interface IGitCredentialPrompter
{
    /// <summary>答え。取り消されたら null。<see cref="GitCredentialPromptKind.Confirm"/> は「はい」で空文字、
    /// <see cref="GitCredentialPromptKind.Notice"/> は知らせたら空文字を返す。<paramref name="cancellationToken"/> は
    /// 問うた側（git）が居なくなったときに倒れる——そのときは入力欄を閉じる。</summary>
    Task<string?> AskAsync(GitAskPass.Request request, GitCredentialPromptKind kind, CancellationToken cancellationToken);
}
