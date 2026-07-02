using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace sk0ya.Loomo.App.Services;

/// <summary>軌跡の1レコード（SQLite の行）。Kind は <c>TrailEntryKind</c> の int 値。</summary>
public sealed record TrailRecord(
    long Id,
    DateTime Timestamp,
    int Kind,
    string Target,
    string Label,
    int Line,
    int Column);

/// <summary>軌跡（操作ログ）の SQLite 永続化。ワークスペース（workspace 列＝WorkspaceSnapshot.Id、
/// 未オープンのスクラッチは空文字）×1日ごと（ローカル日付の day 列）に記録し、過去の日付の軌跡も
/// 遡って読める。上限なし。既定の保存先は %APPDATA%/Loomo/trail.db。
/// すべて UI スレッドからの小さな読み書き想定（WAL・接続は開きっぱなし）。失敗時は
/// 呼び出し側（TrailViewModel）が握りつぶしてメモリ内動作へ縮退する。</summary>
public sealed class TrailStore : IDisposable
{
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private readonly object _gate = new();

    public TrailStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "trail.db"))
    {
    }

    public TrailStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    private SqliteConnection Connection
    {
        get
        {
            if (_connection is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
                _connection = new SqliteConnection($"Data Source={_dbPath}");
                _connection.Open();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS trail_entries (
                        id        INTEGER PRIMARY KEY AUTOINCREMENT,
                        workspace TEXT    NOT NULL DEFAULT '',
                        day       TEXT    NOT NULL,
                        timestamp TEXT    NOT NULL,
                        kind      INTEGER NOT NULL,
                        target    TEXT    NOT NULL,
                        label     TEXT    NOT NULL,
                        line      INTEGER NOT NULL DEFAULT -1,
                        col       INTEGER NOT NULL DEFAULT -1
                    );
                    """;
                cmd.ExecuteNonQuery();
                // workspace 列が無い旧テーブル（v2 初期）へのマイグレーション。旧行は '' のまま
                // （＝どのワークスペースにも属さない。スクラッチ表示でのみ見える）。
                using (var probe = _connection.CreateCommand())
                {
                    probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('trail_entries') WHERE name = 'workspace';";
                    if (Convert.ToInt64(probe.ExecuteScalar()) == 0)
                    {
                        using var alter = _connection.CreateCommand();
                        alter.CommandText = "ALTER TABLE trail_entries ADD COLUMN workspace TEXT NOT NULL DEFAULT '';";
                        alter.ExecuteNonQuery();
                    }
                }
                using (var index = _connection.CreateCommand())
                {
                    index.CommandText = "CREATE INDEX IF NOT EXISTS idx_trail_ws_day ON trail_entries(workspace, day, id);";
                    index.ExecuteNonQuery();
                }
            }
            return _connection;
        }
    }

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    public long Append(string workspace, DateTime timestamp, int kind, string target, string label, int line, int column)
    {
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO trail_entries(workspace, day, timestamp, kind, target, label, line, col)
                VALUES ($ws, $day, $ts, $kind, $target, $label, $line, $col);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ws", workspace);
            cmd.Parameters.AddWithValue("$day", timestamp.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$ts", timestamp.ToString(TimestampFormat));
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$target", target);
            cmd.Parameters.AddWithValue("$label", label);
            cmd.Parameters.AddWithValue("$line", line);
            cmd.Parameters.AddWithValue("$col", column);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>直前と同一地点の再通過（デデュープ）で、既存行の時刻・ラベル・位置を上書きする。</summary>
    public void Update(long id, DateTime timestamp, string label, int line, int column)
    {
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE trail_entries
                SET timestamp = $ts, label = $label, line = $line, col = $col
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$ts", timestamp.ToString(TimestampFormat));
            cmd.Parameters.AddWithValue("$label", label);
            cmd.Parameters.AddWithValue("$line", line);
            cmd.Parameters.AddWithValue("$col", column);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>離脱時カーソルの上書き（ラベル・時刻は変えない）。</summary>
    public void UpdatePosition(long id, int line, int column)
    {
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "UPDATE trail_entries SET line = $line, col = $col WHERE id = $id;";
            cmd.Parameters.AddWithValue("$line", line);
            cmd.Parameters.AddWithValue("$col", column);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>指定ワークスペース×指定日（ローカル日付）の軌跡を古い順に読む。</summary>
    public IReadOnlyList<TrailRecord> LoadDay(string workspace, DateOnly day)
    {
        lock (_gate)
        {
            var list = new List<TrailRecord>();
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, timestamp, kind, target, label, line, col
                FROM trail_entries WHERE workspace = $ws AND day = $day ORDER BY id;
                """;
            cmd.Parameters.AddWithValue("$ws", workspace);
            cmd.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TrailRecord(
                    reader.GetInt64(0),
                    DateTime.ParseExact(reader.GetString(1), TimestampFormat, null),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6)));
            }
            return list;
        }
    }

    /// <summary>そのワークスペースに記録が1件でもあるか（バーの表示判定）。</summary>
    public bool HasAny(string workspace)
    {
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM trail_entries WHERE workspace = $ws);";
            cmd.Parameters.AddWithValue("$ws", workspace);
            return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
        }
    }

    /// <summary>そのワークスペースで記録のある日付一覧（新しい順）。カレンダーの参考・テスト用。</summary>
    public IReadOnlyList<DateOnly> ListDays(string workspace)
    {
        lock (_gate)
        {
            var list = new List<DateOnly>();
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT day FROM trail_entries WHERE workspace = $ws ORDER BY day DESC;";
            cmd.Parameters.AddWithValue("$ws", workspace);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd"));
            return list;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
