using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sk0ya.Loomo.Core.Pty;

/// <summary>
/// 端末の常駐ホスト（<c>sk0ya.Loomo.Pty.Host</c>）と本体のあいだの約束（設計 §34.5）。
///
/// <para>接続1本＝セッション1つ。最初の1フレームが <see cref="FrameType.Request"/>（JSON）で、
/// ホストは <see cref="FrameType.Response"/> か <see cref="FrameType.Error"/> で答える。開いた接続では
/// 以後、出力・入力・リサイズ・終了をフレームで流す。</para>
///
/// <para>フレームは <c>[種別 1byte][長さ 4byte LE][本体]</c>。出力は UTF-16LE のまま送る——ConPTY 側は
/// 文字列で渡してくるので、UTF-8 へ直すとサロゲートの切れ目で壊しうる。</para>
/// </summary>
public static class PtyProtocol
{
    /// <summary>互換の無い変更をしたら上げる。パイプ名に入るので、版の違うホストとは最初から出会わない。</summary>
    public const int Version = 2;

    /// <summary>1フレームの上限。スナップショット（スクロールバック全体）が一番大きい。</summary>
    public const int MaxFrameBytes = 256 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public enum FrameType : byte
    {
        Request = 1,
        Response = 2,
        Error = 3,
        /// <summary>ホスト→本体：端末の出力（UTF-16LE）。接続直後の1つ目はスナップショット。</summary>
        Output = 10,
        /// <summary>ホスト→本体：シェルが終わった（int32 LE の終了コード）。</summary>
        Exited = 11,
        /// <summary>本体→ホスト：入力（生バイト）。</summary>
        Input = 20,
        /// <summary>本体→ホスト：大きさ（int16 LE 桁, int16 LE 行）。</summary>
        Resize = 21,
        /// <summary>本体→ホスト：このセッションを殺す。</summary>
        Kill = 22,
    }

    public enum Operation
    {
        /// <summary>あれば繋ぐ、無ければ作って繋ぐ。</summary>
        Open,
        Kill,
        KillWorkspace,
        List,
    }

    /// <param name="Op">要求の種類。</param>
    /// <param name="SessionId">セッション（＝端末タブ）の ID。</param>
    /// <param name="WorkspaceId">持ち主のワークスペース。ワークスペースの削除で一緒に殺すために持つ。</param>
    /// <param name="CommandLine">シェル統合を差し込み済みの起動コマンド行。</param>
    /// <param name="WorkingDirectory">起動時の作業フォルダー。</param>
    /// <param name="Columns">桁数。</param>
    /// <param name="Rows">行数。</param>
    /// <param name="ScrollbackLimit">ホストの画面モデルが持つスクロールバックの行数。</param>
    /// <param name="Environment">起動時に足す環境変数。</param>
    public sealed record Request(
        Operation Op,
        Guid SessionId = default,
        Guid? WorkspaceId = null,
        string? CommandLine = null,
        string? WorkingDirectory = null,
        short Columns = 0,
        short Rows = 0,
        int ScrollbackLimit = 0,
        Dictionary<string, string?>? Environment = null);

    /// <param name="Created">新しく作ったなら true、生きていたものに繋いだなら false。</param>
    /// <param name="Sessions">一覧の要求への答え。</param>
    /// <param name="InUse">開こうとしたシェルには別の接続が繋がっている（先勝ち。後から来た方には渡さない）。</param>
    public sealed record Response(bool Created = false, List<SessionInfo>? Sessions = null, bool InUse = false);

    public sealed record SessionInfo(
        Guid SessionId,
        Guid? WorkspaceId,
        string? Title,
        string? WorkingDirectory,
        DateTime CreatedUtc,
        bool Attached);

    /// <summary>
    /// 同じユーザー・同じログオンセッションのホストにだけ出会う名前。ACL は
    /// <c>PipeOptions.CurrentUserOnly</c> が絞るが、他ユーザーと名前がぶつかって先取りされないよう
    /// ユーザー名も混ぜる。
    /// </summary>
    public static string PipeName()
    {
        int sessionId;
        using (var process = Process.GetCurrentProcess())
            sessionId = process.SessionId;
        byte[] user = SHA256.HashData(Encoding.UTF8.GetBytes(System.Environment.UserDomainName + "\\" + System.Environment.UserName));
        return $"loomo-pty-v{Version}-{sessionId}-{Convert.ToHexString(user, 0, 6).ToLowerInvariant()}";
    }

    public static void WriteFrame(Stream stream, FrameType type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    public static void WriteJson<T>(Stream stream, FrameType type, T value) =>
        WriteFrame(stream, type, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    /// <summary>1フレーム読む。相手が閉じたら null。</summary>
    public static (FrameType Type, byte[] Payload)? ReadFrame(Stream stream)
    {
        Span<byte> header = stackalloc byte[5];
        if (!ReadExactly(stream, header))
            return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header[1..]);
        if (length < 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"フレームの長さが不正です: {length}");
        var payload = new byte[length];
        if (!ReadExactly(stream, payload))
            return null;
        return ((FrameType)header[0], payload);
    }

    public static T ReadJson<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException("空の JSON です");

    public static byte[] EncodeText(string text) => Encoding.Unicode.GetBytes(text);
    public static string DecodeText(byte[] payload) => Encoding.Unicode.GetString(payload);

    public static byte[] EncodeResize(short columns, short rows)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(payload, columns);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), rows);
        return payload;
    }

    public static (short Columns, short Rows) DecodeResize(byte[] payload) =>
        (BinaryPrimitives.ReadInt16LittleEndian(payload), BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(2)));

    public static byte[] EncodeExitCode(int exitCode)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, exitCode);
        return payload;
    }

    public static int DecodeExitCode(byte[] payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : -1;

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer[read..]);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }
}
