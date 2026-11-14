using System.Globalization;
using Microsoft.Data.Sqlite;
using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 結んだ出自の SQLite の置き場（§43.6 A2b）。<c>%APPDATA%/Loomo/provenance.db</c>——人のリポジトリには何も書かない
/// （git notes は頼まれずに ref を増やすので採らなかった）。頼みごと（本文・セッション・位置）を 1 行、コミット済みの行
/// （ファイル・短いハッシュ・そのコミットでの行番号）からそこへの参照を 1 行ずつ。
/// <para>読むのも書くのも blame を引くスレッド（背景）。接続は開きっぱなし（WAL）で、1 つの錠で順に通す。<see cref="Retention"/>
/// より前に覚えた行は開いたついでに刈る。壊れていても・開けなくても黙って空として振る舞う。</para>
/// </summary>
public sealed class ProvenanceStore : IClaudeProvenanceStore, IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);

    private readonly string _dbPath;
    private readonly Func<DateTime> _nowUtc;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private bool _broken;

    public ProvenanceStore(string dbPath, Func<DateTime>? nowUtc = null)
    {
        _dbPath = dbPath;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo", "provenance.db");

    public IReadOnlyDictionary<(string Hash, int OriginalLine), ClaudeWrite> Load(string filePath)
    {
        var result = new Dictionary<(string Hash, int OriginalLine), ClaudeWrite>();
        lock (_gate)
        {
            try
            {
                if (Open() is not { } connection)
                    return result;
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT l.hash, l.original_line, l.from_subagent, r.session_id, r.prompt_line, r.prompt, r.at, r.transcript_path
                    FROM commit_line l JOIN request r ON r.id = l.request_id
                    WHERE l.file = $file
                    """;
                command.Parameters.AddWithValue("$file", Key(filePath));
                using var reader = command.ExecuteReader();
                // 同じ頼みごとは同じインスタンスにする（「同じ頼みごとの行」をまとめて扱えるように）。
                var requests = new Dictionary<(string, int, bool), ClaudeWrite>();
                while (reader.Read())
                {
                    var fromSubagent = reader.GetInt64(2) != 0;
                    var session = reader.GetString(3);
                    var promptLine = reader.GetInt32(4);
                    if (!requests.TryGetValue((session, promptLine, fromSubagent), out var write))
                    {
                        DateTimeOffset? at = reader.IsDBNull(6) ? null
                            : DateTimeOffset.TryParse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
                        requests[(session, promptLine, fromSubagent)] = write = new ClaudeWrite(
                            session, reader.GetString(7), filePath, reader.GetString(5), promptLine, at, [], fromSubagent) { Stored = true };
                    }
                    result[(reader.GetString(0), reader.GetInt32(1))] = write;
                }
            }
            catch (SqliteException) { }
        }
        return result;
    }

    public void Save(string filePath, IReadOnlyDictionary<(string Hash, int OriginalLine), ClaudeWrite> lines)
    {
        if (lines.Count == 0)
            return;
        lock (_gate)
        {
            try
            {
                if (Open() is not { } connection)
                    return;
                using var transaction = connection.BeginTransaction();
                var ids = new Dictionary<(string, int), long>();
                var now = _nowUtc().ToString("o", CultureInfo.InvariantCulture);
                foreach (var (key, write) in lines)
                {
                    if (!ids.TryGetValue((write.SessionId, write.PromptLine), out var requestId))
                        ids[(write.SessionId, write.PromptLine)] = requestId = UpsertRequest(connection, transaction, write);
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO commit_line (file, hash, original_line, request_id, from_subagent, saved_at)
                        VALUES ($file, $hash, $line, $request, $subagent, $now)
                        ON CONFLICT(file, hash, original_line) DO UPDATE SET
                            request_id = excluded.request_id, from_subagent = excluded.from_subagent, saved_at = excluded.saved_at
                        """;
                    command.Parameters.AddWithValue("$file", Key(filePath));
                    command.Parameters.AddWithValue("$hash", key.Hash);
                    command.Parameters.AddWithValue("$line", key.OriginalLine);
                    command.Parameters.AddWithValue("$request", requestId);
                    command.Parameters.AddWithValue("$subagent", write.FromSubagent ? 1 : 0);
                    command.Parameters.AddWithValue("$now", now);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch (SqliteException) { }
        }
    }

    private static long UpsertRequest(SqliteConnection connection, SqliteTransaction transaction, ClaudeWrite write)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO request (session_id, prompt_line, prompt, at, transcript_path)
            VALUES ($session, $line, $prompt, $at, $path)
            ON CONFLICT(session_id, prompt_line) DO UPDATE SET prompt = excluded.prompt, transcript_path = excluded.transcript_path
            RETURNING id
            """;
        command.Parameters.AddWithValue("$session", write.SessionId);
        command.Parameters.AddWithValue("$line", write.PromptLine);
        command.Parameters.AddWithValue("$prompt", write.Prompt);
        command.Parameters.AddWithValue("$at", write.At is { } at ? at.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$path", write.TranscriptPath);
        return (long)command.ExecuteScalar()!;
    }

    private SqliteConnection? Open()
    {
        if (_connection is not null)
            return _connection;
        if (_broken)
            return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection,
                """
                CREATE TABLE IF NOT EXISTS request (
                    id INTEGER PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    prompt_line INTEGER NOT NULL,
                    prompt TEXT NOT NULL,
                    at TEXT,
                    transcript_path TEXT NOT NULL,
                    UNIQUE (session_id, prompt_line));
                CREATE TABLE IF NOT EXISTS commit_line (
                    file TEXT NOT NULL,
                    hash TEXT NOT NULL,
                    original_line INTEGER NOT NULL,
                    request_id INTEGER NOT NULL,
                    from_subagent INTEGER NOT NULL,
                    saved_at TEXT NOT NULL,
                    PRIMARY KEY (file, hash, original_line)) WITHOUT ROWID;
                """);
            // 古い行を刈る（ISO 8601 の UTC は文字列のまま比べられる）。参照の無くなった頼みごとも落とす。
            using (var prune = connection.CreateCommand())
            {
                prune.CommandText =
                    """
                    DELETE FROM commit_line WHERE saved_at < $before;
                    DELETE FROM request WHERE id NOT IN (SELECT DISTINCT request_id FROM commit_line);
                    """;
                prune.Parameters.AddWithValue("$before", (_nowUtc() - Retention).ToString("o", CultureInfo.InvariantCulture));
                prune.ExecuteNonQuery();
            }
            return _connection = connection;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _broken = true;
            return null;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>ファイルの鍵：正規化した絶対パス（Windows では大文字小文字を区別しない）。</summary>
    private static string Key(string filePath)
    {
        try { return Path.GetFullPath(filePath).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return filePath.ToUpperInvariant(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
            _broken = true;
        }
    }
}
