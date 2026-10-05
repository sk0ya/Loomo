using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    string? PaneLayout,
    string? Note = null);

/// <summary>しおり（メモを付けた地点）の1件。一覧から日をまたいでその地点へ戻るための最小限（§27.13）。</summary>
public sealed record TrailNoteRecord(long Id, DateOnly Day, DateTime Timestamp, int Kind, string Target, string Label,
    string Note);

/// <summary>可視ターミナルで実行し終えたコマンドの1回分（§24.23 この日のまとめ）。出力は持たない。</summary>
public sealed record CommandRunRecord(DateTime Timestamp, string Command, int? ExitCode);

/// <summary>
/// まだ書かれていないかもしれない軌跡1行への参照。<see cref="TrailStore.AppendDeferred"/> は即座にこれを返し、
/// 書き出しスレッドが INSERT を終えた時点で id が入る。後続の更新（デデュープ・離脱位置・配置）も同じ
/// 待ち行列を通るので、<b>id が決まる前に更新が走ることはない</b>（1本のスレッドで積んだ順に実行するため）。
/// </summary>
public sealed class TrailRowRef
{
    /// <summary>まだ書かれていない。</summary>
    public const long Pending = -2;
    /// <summary>書き込みに失敗した（＝メモリ内だけのエントリ）。</summary>
    public const long Lost = -1;

    private long _id = Pending;

    private TrailRowRef(long id) => _id = id;
    internal TrailRowRef() { }

    /// <summary>読み込み済みの行（id が判っているもの）への参照。</summary>
    public static TrailRowRef Resolved(long id) => new(id);

    public long Id => Volatile.Read(ref _id);
    public bool IsPersisted => Id >= 0;
    internal void Resolve(long id) => Volatile.Write(ref _id, id);
}

/// <summary>軌跡（操作ログ）の SQLite 永続化。ワークスペース（workspace 列＝WorkspaceSnapshot.Id、
/// 未オープンのスクラッチは空文字）×1日ごと（ローカル日付の day 列）に記録し、過去の日付の軌跡も
/// 遡って読める。上限なし。既定の保存先は %APPDATA%/Loomo/trail.db。
/// すべて UI スレッドからの小さな読み書き想定（WAL・接続は開きっぱなし）。失敗時は
/// 呼び出し側（TrailViewModel）が握りつぶしてメモリ内動作へ縮退する。</summary>
public sealed class TrailStore : IDisposable
{
    // スキーマ更新時も軌跡は破棄せず、下の EnsureSchema で加算的に移行する。
    private const int SchemaVersion = 1;
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private readonly object _gate = new();
    /// <summary>書き込みを UI スレッドから外すための待ち行列（1本のスレッドで積んだ順に実行）。
    /// 実測で INSERT 0.8ms／UPDATE 0.7ms——ペインを切り替えるたび、編集が落ち着くたびに
    /// UI スレッドで払っていた。§31.15</summary>
    private readonly DeferredWriteQueue _writes = new("LoomoTrailSave");

    public TrailStore()
        : this(DefaultPath())
    {
    }

    private static string DefaultPath()
        => Environment.GetEnvironmentVariable("LOOMO_TRAIL_DB") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Loomo", "trail.db");

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
                EnsureSchema(_connection);
            }
            return _connection;
        }
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            var current = Convert.ToInt32(version.ExecuteScalar());
            if (current > SchemaVersion)
                throw new InvalidOperationException(
                    $"trail.db のスキーマ {current} は、このバージョン（{SchemaVersion}）より新しいため開けません。");
        }

        using (var schema = connection.CreateCommand())
        {
            // user_version=0 の旧DBにもまず不足列を既定値付きで追加する。既存行は
            // レイアウト表示・舞台指定なし・配置なしとして読み込めるため、履歴を失わない。
            schema.CommandText = """
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
                    display_mode INTEGER NOT NULL DEFAULT 1,
                    stage_pane   INTEGER NULL,
                    layout_id    INTEGER NULL REFERENCES trail_layouts(id)
                );
                """;
            schema.ExecuteNonQuery();
        }

        AddColumnIfMissing(connection, "workspace", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "display_mode", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(connection, "stage_pane", "INTEGER NULL");
        AddColumnIfMissing(connection, "layout_id", "INTEGER NULL REFERENCES trail_layouts(id)");
        // しおり（§27.13）。user_version は上げない——列を足すだけなら古い本体は読まないだけで困らないが、
        // 上げると古い本体が「新しすぎるスキーマ」として trail.db ごと開けなくなる。
        AddColumnIfMissing(connection, "note", "TEXT NULL");

        // コマンドの実行記録（§24.23）。「最近のコマンド」は同じ行を1件にまとめて最新50件しか持たないので、
        // その日に何をいつ打って成否がどうだったかはここにしか残らない。出力は入れない（§24.21 と同じ理由）。
        // テーブルを足すだけなので user_version は上げない（しおりと同じ）。
        using (var commands = connection.CreateCommand())
        {
            commands.CommandText = """
                CREATE TABLE IF NOT EXISTS command_runs (
                    id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    workspace TEXT    NOT NULL,
                    day       TEXT    NOT NULL,
                    timestamp TEXT    NOT NULL,
                    command   TEXT    NOT NULL,
                    exit_code INTEGER NULL
                );
                CREATE INDEX IF NOT EXISTS idx_command_runs_ws_day ON command_runs(workspace, day, id);
                """;
            commands.ExecuteNonQuery();
        }

        using (var finish = connection.CreateCommand())
        {
            finish.CommandText = $"""
                CREATE INDEX IF NOT EXISTS idx_trail_ws_day ON trail_entries(workspace, day, id);
                PRAGMA user_version = {SchemaVersion};
                """;
            finish.ExecuteNonQuery();
        }
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string name, string definition)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('trail_entries') WHERE name = $name;";
        probe.Parameters.AddWithValue("$name", name);
        if (Convert.ToInt64(probe.ExecuteScalar()) != 0)
            return;

        // name/definition は上の固定呼び出しだけから渡す（SQLite は列定義をパラメーター化できない）。
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE trail_entries ADD COLUMN {name} {definition};";
        alter.ExecuteNonQuery();
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

    /// <summary>行を1つ足す。ディスクを叩くのは書き出しスレッドで、呼び出し側は参照だけ受け取って進む。</summary>
    public TrailRowRef AppendDeferred(string workspace, DateTime timestamp, int kind, string target, string label,
        int line, int column, DisplayMode displayMode = DisplayMode.Layout, PaneKind? stagePane = null,
        string? paneLayout = null)
    {
        var row = new TrailRowRef();
        _writes.Enqueue(() =>
        {
            try
            {
                row.Resolve(Append(workspace, timestamp, kind, target, label, line, column,
                    displayMode, stagePane, paneLayout));
            }
            catch { row.Resolve(TrailRowRef.Lost); }
        });
        return row;
    }

    /// <summary><see cref="Update(long, DateTime, string, int, int, string?)"/> の遅延版。</summary>
    public void UpdateDeferred(TrailRowRef row, DateTime timestamp, string label, int line, int column,
        string? paneLayout)
        => EnqueueRowUpdate(row, id => Update(id, timestamp, label, line, column, paneLayout));

    /// <summary><see cref="UpdatePosition"/> の遅延版。</summary>
    public void UpdatePositionDeferred(TrailRowRef row, int line, int column)
        => EnqueueRowUpdate(row, id => UpdatePosition(id, line, column));

    /// <summary><see cref="UpdatePaneLayout"/> の遅延版。</summary>
    public void UpdatePaneLayoutDeferred(TrailRowRef row, string? paneLayout)
        => EnqueueRowUpdate(row, id => UpdatePaneLayout(id, paneLayout));

    private void EnqueueRowUpdate(TrailRowRef row, Action<long> update)
        => _writes.Enqueue(() =>
        {
            // ここに来た時点で INSERT は済んでいる（同じ待ち行列を順に実行するため）。
            // それでも負なら書き込みに失敗した行なので、黙って捨てる。
            var id = row.Id;
            if (id < 0) return;
            try { update(id); }
            catch { }
        });

    /// <summary>積んである書き込みが終わるまで待つ。読み取りは自分で通す。</summary>
    public void Flush() => _writes.Flush();

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
            CleanupLayoutIfUnreferenced(previousLayoutId, layoutId);
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

    /// <summary>最新地点の表示配置だけを更新する。時刻・対象・カーソル位置は変えない。</summary>
    public void UpdatePaneLayout(long id, string? paneLayout)
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

            using (var update = Connection.CreateCommand())
            {
                update.CommandText = "UPDATE trail_entries SET layout_id = $layoutId WHERE id = $id;";
                update.Parameters.AddWithValue("$layoutId", layoutId is { } layout ? layout : DBNull.Value);
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
            }

            CleanupLayoutIfUnreferenced(previousLayoutId, layoutId);
        }
    }

    /// <summary>指定ワークスペース×指定日（ローカル日付）の軌跡を古い順に読む。</summary>
    public IReadOnlyList<TrailRecord> LoadDay(string workspace, DateOnly day)
    {
        _writes.Flush();   // 積んである書き込みより前を読まない
        lock (_gate)
        {
            var list = new List<TrailRecord>();
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                SELECT e.id, e.timestamp, e.kind, e.target, e.label, e.line, e.col,
                       e.display_mode, e.stage_pane, l.snapshot, e.note
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
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
            return list;
        }
    }

    /// <summary>しおりのメモを付け替える（null・空白なら外す）。デデュープの更新はこの列に触れないので、
    /// 同じ地点を通り直してもしおりは残る。</summary>
    public void SetNote(long id, string? note)
    {
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "UPDATE trail_entries SET note = $note WHERE id = $id;";
            cmd.Parameters.AddWithValue("$note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : note);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary><see cref="SetNote"/> の遅延版（まだ INSERT 待ちの行にも付けられる）。</summary>
    public void SetNoteDeferred(TrailRowRef row, string? note)
        => EnqueueRowUpdate(row, id => SetNote(id, note));

    /// <summary>そのワークスペースのしおり（新しい順）。日をまたいだ一覧に使う。</summary>
    public IReadOnlyList<TrailNoteRecord> ListNotes(string workspace)
    {
        _writes.Flush();   // 積んである書き込みより前を読まない
        lock (_gate)
        {
            var list = new List<TrailNoteRecord>();
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, day, timestamp, kind, target, label, note
                FROM trail_entries
                WHERE workspace = $ws AND note IS NOT NULL
                ORDER BY timestamp DESC, id DESC;
                """;
            cmd.Parameters.AddWithValue("$ws", workspace);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new TrailNoteRecord(
                    reader.GetInt64(0),
                    DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd"),
                    DateTime.ParseExact(reader.GetString(2), TimestampFormat, null),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
            return list;
        }
    }

    /// <summary>コマンドの実行を1回分残す（<paramref name="finished"/> はローカル時刻。day はそこから決める）。</summary>
    public void AppendCommandRun(string workspace, DateTime finished, string command, int? exitCode)
    {
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO command_runs(workspace, day, timestamp, command, exit_code)
                VALUES ($ws, $day, $ts, $command, $exit);
                """;
            cmd.Parameters.AddWithValue("$ws", workspace);
            cmd.Parameters.AddWithValue("$day", finished.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$ts", finished.ToString(TimestampFormat));
            cmd.Parameters.AddWithValue("$command", command);
            cmd.Parameters.AddWithValue("$exit", exitCode is { } code ? code : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary><see cref="AppendCommandRun"/> の遅延版（書き出しスレッドで書く。失敗は捨てる）。</summary>
    public void AppendCommandRunDeferred(string workspace, DateTime finished, string command, int? exitCode)
        => _writes.Enqueue(() =>
        {
            try { AppendCommandRun(workspace, finished, command, exitCode); }
            catch { }
        });

    /// <summary>指定ワークスペース×指定日のコマンド実行を古い順に読む。</summary>
    public IReadOnlyList<CommandRunRecord> LoadCommandRuns(string workspace, DateOnly day)
    {
        _writes.Flush();   // 積んである書き込みより前を読まない
        lock (_gate)
        {
            var list = new List<CommandRunRecord>();
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                SELECT timestamp, command, exit_code FROM command_runs
                WHERE workspace = $ws AND day = $day ORDER BY id;
                """;
            cmd.Parameters.AddWithValue("$ws", workspace);
            cmd.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new CommandRunRecord(
                    DateTime.ParseExact(reader.GetString(0), TimestampFormat, null),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2)));
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

    private void CleanupLayoutIfUnreferenced(long? previousLayoutId, long? currentLayoutId)
    {
        if (previousLayoutId is not { } oldId || oldId == currentLayoutId)
            return;
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

    /// <summary>そのワークスペースに記録が1件でもあるか（バーの表示判定）。</summary>
    public bool HasAny(string workspace)
    {
        _writes.Flush();   // 積んである書き込みより前を読まない
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
        _writes.Flush();   // 積んである書き込みより前を読まない
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
        _writes.Flush();
        _writes.Dispose();
        _connection?.Dispose();
        _connection = null;
    }
}
