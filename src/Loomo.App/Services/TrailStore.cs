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
    int Column,
    DisplayMode DisplayMode,
    PaneKind? StagePane,
    string? PaneLayout);

/// <summary>軌跡（操作ログ）の SQLite 永続化。ワークスペース（workspace 列＝WorkspaceSnapshot.Id、
/// 未オープンのスクラッチは空文字）×1日ごと（ローカル日付の day 列）に記録し、過去の日付の軌跡も
/// 遡って読める。上限なし。既定の保存先は %APPDATA%/Loomo/trail.db。
/// すべて UI スレッドからの小さな読み書き想定（WAL・接続は開きっぱなし）。失敗時は
/// 呼び出し側（TrailViewModel）が握りつぶしてメモリ内動作へ縮退する。</summary>
public sealed class TrailStore : IDisposable
{
    // 開発中の初回正式スキーマ。user_version=0 の試作DBはデータごと作り直す。
    private const int SchemaVersion = 1;
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
                using (var journal = _connection.CreateCommand())
                {
                    journal.CommandText = "PRAGMA journal_mode=WAL;";
                    journal.ExecuteNonQuery();
                }
                using (var version = _connection.CreateCommand())
                {
                    version.CommandText = "PRAGMA user_version;";
                    if (Convert.ToInt32(version.ExecuteScalar()) != SchemaVersion)
                    {
                        using var reset = _connection.CreateCommand();
                        reset.CommandText = "DROP TABLE IF EXISTS trail_entries; DROP TABLE IF EXISTS trail_layouts;";
                        reset.ExecuteNonQuery();
                    }
                }
                using var schema = _connection.CreateCommand();
                schema.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS trail_layouts (
                        id       INTEGER PRIMARY KEY AUTOINCREMENT,
                        snapshot TEXT NOT NULL UNIQUE
                    );
                    CREATE TABLE IF NOT EXISTS trail_entries (
                        id        INTEGER PRIMARY KEY AUTOINCREMENT,
                        workspace TEXT    NOT NULL DEFAULT '',
                        day       TEXT    NOT NULL,
                        timestamp TEXT    NOT NULL,
                        kind      INTEGER NOT NULL,
                        target    TEXT    NOT NULL,
                        label     TEXT    NOT NULL,
                        line      INTEGER NOT NULL DEFAULT -1,
                        col       INTEGER NOT NULL DEFAULT -1,
                        display_mode INTEGER NOT NULL,
                        stage_pane   INTEGER NULL,
                        layout_id    INTEGER NULL REFERENCES trail_layouts(id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_trail_ws_day ON trail_entries(workspace, day, id);
                    PRAGMA user_version = {SchemaVersion};
                    """;
                schema.ExecuteNonQuery();
            }
            return _connection;
        }
    }

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    public long Append(string workspace, DateTime timestamp, int kind, string target, string label, int line, int column,
        DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null, string? paneLayout = null)
    {
        lock (_gate)
        {
            var layoutId = GetOrCreateLayoutId(paneLayout);
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO trail_entries(workspace, day, timestamp, kind, target, label, line, col,
                                          display_mode, stage_pane, layout_id)
                VALUES ($ws, $day, $ts, $kind, $target, $label, $line, $col, $mode, $stagePane, $layoutId);
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
            cmd.Parameters.AddWithValue("$mode", (int)displayMode);
            cmd.Parameters.AddWithValue("$stagePane", stagePane is { } pane ? (int)pane : DBNull.Value);
            cmd.Parameters.AddWithValue("$layoutId", layoutId is { } id ? id : DBNull.Value);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>直前と同一地点の再通過（デデュープ）で、既存行の時刻・ラベル・位置を上書きする。</summary>
    public void Update(long id, DateTime timestamp, string label, int line, int column, string? paneLayout)
    {
        lock (_gate)
        {
            var layoutId = GetOrCreateLayoutId(paneLayout);
            long? previousLayoutId;
            using (var previous = Connection.CreateCommand())
            {
                previous.CommandText = "SELECT layout_id FROM trail_entries WHERE id = $id;";
                previous.Parameters.AddWithValue("$id", id);
                var value = previous.ExecuteScalar();
                previousLayoutId = value is null or DBNull ? null : Convert.ToInt64(value);
            }
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                UPDATE trail_entries
                SET timestamp = $ts, label = $label, line = $line, col = $col, layout_id = $layoutId
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$ts", timestamp.ToString(TimestampFormat));
            cmd.Parameters.AddWithValue("$label", label);
            cmd.Parameters.AddWithValue("$line", line);
            cmd.Parameters.AddWithValue("$col", column);
            cmd.Parameters.AddWithValue("$layoutId", layoutId is { } layout ? layout : DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();

            // 同一点のデデュープ更新で参照先が変わった場合、誰からも参照されない旧配置を回収する。
            if (previousLayoutId is { } oldId && oldId != layoutId)
            {
                using var cleanup = Connection.CreateCommand();
                cleanup.CommandText = """
                    DELETE FROM trail_layouts
                    WHERE id = $id AND NOT EXISTS (
                        SELECT 1 FROM trail_entries WHERE layout_id = $id
                    );
                    """;
                cleanup.Parameters.AddWithValue("$id", oldId);
                cleanup.ExecuteNonQuery();
            }
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
                SELECT e.id, e.timestamp, e.kind, e.target, e.label, e.line, e.col,
                       e.display_mode, e.stage_pane, l.snapshot
                FROM trail_entries e
                LEFT JOIN trail_layouts l ON l.id = e.layout_id
                WHERE e.workspace = $ws AND e.day = $day ORDER BY e.id;
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
                    reader.GetInt32(6),
                    (DisplayMode)reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : (PaneKind)reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
            return list;
        }
    }

    /// <summary>同じ配置JSONは1行だけ保持し、各軌跡行からID参照する。</summary>
    private long? GetOrCreateLayoutId(string? paneLayout)
    {
        if (string.IsNullOrWhiteSpace(paneLayout))
            return null;
        using (var insert = Connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO trail_layouts(snapshot) VALUES ($snapshot);";
            insert.Parameters.AddWithValue("$snapshot", paneLayout);
            insert.ExecuteNonQuery();
        }
        using var select = Connection.CreateCommand();
        select.CommandText = "SELECT id FROM trail_layouts WHERE snapshot = $snapshot;";
        select.Parameters.AddWithValue("$snapshot", paneLayout);
        return (long)select.ExecuteScalar()!;
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
