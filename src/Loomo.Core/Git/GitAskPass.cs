using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    /// <summary>git の起動ごとの印。Loomo が「人が答えていた時間」を git の時間制限から外すのに使う。</summary>
    public const string InvocationVariable = "LOOMO_ASKPASS_INVOCATION";

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
    /// <param name="Invocation">どの git の起動の問いか（<see cref="InvocationVariable"/>）。</param>
    public sealed record Request(string Prompt, string? SshPrompt = null, string? Context = null, string? Invocation = null);

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

        // 初めての接続先のホスト鍵（Are you sure you want to continue connecting (yes/no/[fingerprint])?）。
        // 文面全体で見る——前置きの「can't be established」のアポストロフィで引用符の数え方が狂うので。
        if ((request.Prompt ?? "").Contains("(yes/no", StringComparison.OrdinalIgnoreCase))
            return GitCredentialPromptKind.YesNo;
        var prompt = OutsideQuotes(request.Prompt ?? "");
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

    /// <summary>引用符の中（git の URL・ssh の鍵のパス）を除いた文面。<c>Username for 'https://token.example.com'</c> の
    /// ホスト名に釣られてユーザー名を伏せないように、問いの種類は外側の語だけで決める。</summary>
    private static string OutsideQuotes(string prompt)
    {
        var text = new StringBuilder(prompt.Length);
        var quoted = false;
        foreach (var c in prompt)
        {
            if (c == '\'') { quoted = !quoted; continue; }
            if (!quoted) text.Append(c);
        }
        return text.ToString();
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

    /// <summary>1 行の上限。問いも答えも数百バイトなので、これを超えるものはやり取りの相手ではない。</summary>
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>1 行読む。行の前に相手が切れたら・上限を超えたら null。StreamReader を使わないのは、読み過ぎて
    /// 次の読み（切断の検知）に渡すべきバイトを抱え込まないため（1 バイトずつだが、1 往復は数百バイト）。</summary>
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
            if (buffer.Length >= MaxLineBytes)
                return null;
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
    /// <see cref="GitCredentialPromptKind.Notice"/> は知らせたら空文字を返す。<paramref name="remember"/> が null でなければ
    /// 入力欄に「次から聞かれないようにする」を出し、選ばれたかを答えに添える。<paramref name="cancellationToken"/> は
    /// 問うた側（git）が居なくなったときに倒れる——そのときは入力欄を閉じる。</summary>
    Task<GitCredentialAnswer?> AskAsync(GitAskPass.Request request, GitCredentialPromptKind kind,
        GitCredentialRemember? remember, CancellationToken cancellationToken);

    /// <summary>覚えさせた結果（できた・できなかった理由）を人に知らせる。どのスレッドからも呼ばれる。</summary>
    void Report(string message, bool success);
}

/// <summary>入力欄の答え。<paramref name="Remember"/> は「次から聞かれないようにする」が選ばれたか。</summary>
public sealed record GitCredentialAnswer(string Text, bool Remember = false);

/// <summary>
/// 「次から聞かれないようにする」の中身（§24.101）。Loomo は答えを持たず、Windows の正規の置き場へ渡す——
/// HTTPS は Git Credential Manager、SSH の鍵の合言葉は ssh-agent。問いの文面から読み取れたときだけ出す。
/// </summary>
public abstract record GitCredentialRemember
{
    private GitCredentialRemember() { }

    /// <summary>HTTPS の接続先（<paramref name="Origin"/> は <c>https://host[:port]</c>）。ユーザー名は git が問いに入れていればそれ。</summary>
    public sealed record Https(string Origin, string? UserName) : GitCredentialRemember;

    /// <summary>SSH の鍵（Windows のパス）。</summary>
    public sealed record SshKey(string KeyPath) : GitCredentialRemember;

    private static readonly Regex PasswordFor = new(
        @"^\s*Password for '(?<scheme>https?)://(?:(?<user>[^@'/]+)@)?(?<host>[^/']+)", RegexOptions.CultureInvariant);

    private static readonly Regex PassphraseForKey = new(
        @"passphrase for key '(?<path>[^']+)'", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>問いから覚えさせ方を読む。読めなければ null（チェックを出さない）。純関数。</summary>
    public static GitCredentialRemember? From(GitAskPass.Request request, GitCredentialPromptKind kind)
    {
        if (kind != GitCredentialPromptKind.Secret || string.IsNullOrEmpty(request.Prompt))
            return null;
        if (PasswordFor.Match(request.Prompt) is { Success: true } https)
        {
            var user = https.Groups["user"].Success ? Uri.UnescapeDataString(https.Groups["user"].Value) : null;
            return new Https($"{https.Groups["scheme"].Value}://{https.Groups["host"].Value}", user);
        }
        if (PassphraseForKey.Match(request.Prompt) is { Success: true } key && ToWindowsPath(key.Groups["path"].Value) is { } path)
            return new SshKey(path);
        return null;
    }

    /// <summary>ssh が書いた鍵のパスを Windows のパスへ。Git 同梱の ssh は <c>/c/Users/me/.ssh/id</c>、
    /// Windows の OpenSSH は <c>C:\Users\me/.ssh/id</c> と書く。読めなければ null。</summary>
    public static string? ToWindowsPath(string path)
    {
        var p = path.Trim();
        if (p.Length >= 3 && p[0] == '/' && char.IsLetter(p[1]) && p[2] == '/')
            p = $"{char.ToUpperInvariant(p[1])}:{p[2..]}";
        if (p.Length < 3 || !char.IsLetter(p[0]) || p[1] != ':')
            return null;
        return p.Replace('/', '\\');
    }
}
