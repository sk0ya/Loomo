using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Serialization;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// テーブル形式（SQLite・Parquet）の読み取り専用プレビューで共有する体裁とテーブル HTML の組み立て。
/// Office 系（<see cref="OfficePreview"/>）と同じく本文を <see cref="MarkdownPage.BuildPage(string, string?, string)"/>
/// へ相乗りさせ（テーマ別 CSS・スクロールバーが得られる）、本文側だけに効く小さな <c>&lt;style&gt;</c> を差し込む。
/// エラーページは <see cref="OfficePreview.ErrorPage"/> をそのまま流用する。
/// </summary>
internal static class DatabasePreview
{
    /// <summary>本文の先頭へ入れる、DB プレビュー用の追加スタイル（body スコープ）。</summary>
    internal const string BodyStyle = """
        <style>
        .db-section { margin: 0 0 22px; }
        .db-empty { opacity: .6; font-size: .9em; margin: 4px 0 12px; }
        .db-note { opacity: .6; font-size: .85em; margin: 4px 0 12px; }
        .db-null { opacity: .45; font-style: italic; }
        .db-table-wrap { overflow-x: auto; }
        </style>
        """;

    /// <summary>1テーブル分（見出し＋列ヘッダ＋行）の HTML セクション。NULL 値（<c>null</c> セル）は淡色表記。</summary>
    internal static string RenderSection(
        string heading,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<string?>> rows,
        bool truncated,
        int shownRows)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"db-section\">");
        sb.Append("<h2>").Append(MarkdownRenderer.Encode(heading)).Append("</h2>");

        if (columns.Count == 0)
        {
            sb.Append("<p class=\"db-empty\">列がありません。</p></div>");
            return sb.ToString();
        }

        sb.Append("<div class=\"db-table-wrap\"><table><thead><tr>");
        foreach (var col in columns)
            sb.Append("<th>").Append(MarkdownRenderer.Encode(col)).Append("</th>");
        sb.Append("</tr></thead><tbody>");

        if (rows.Count == 0)
        {
            sb.Append("<tr><td colspan=\"").Append(columns.Count).Append("\" class=\"db-empty\">行がありません。</td></tr>");
        }
        else
        {
            foreach (var row in rows)
            {
                sb.Append("<tr>");
                for (var c = 0; c < columns.Count; c++)
                {
                    var value = c < row.Count ? row[c] : null;
                    if (value is null)
                        sb.Append("<td><span class=\"db-null\">NULL</span></td>");
                    else
                        sb.Append("<td>").Append(MarkdownRenderer.Encode(value)).Append("</td>");
                }
                sb.Append("</tr>");
            }
        }

        sb.Append("</tbody></table></div>");
        if (truncated)
            sb.Append("<p class=\"db-note\">先頭 ").Append(shownRows).Append(" 行を表示しています。</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>1セルあたりの表示上限。ブラウザを固めないよう長大な値は切り詰める。</summary>
    internal const int MaxCellChars = 500;

    /// <summary>DB/Parquet の値を表示用文字列へ。<c>null</c>/<c>DBNull</c> は <c>null</c> を返す（＝NULL 表記へ）。</summary>
    internal static string? Stringify(object? value)
    {
        if (value is null || value is DBNull)
            return null;

        var s = value switch
        {
            byte[] bytes => $"<{bytes.Length} バイト>",
            // 数値・日時は文化非依存で安定した文字列にする。
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        if (s.Length > MaxCellChars)
            s = s[..MaxCellChars] + "…";
        return s;
    }
}

/// <summary>SQLite の1テーブル分のプレビューデータ（表示用の文字列セル。<c>null</c> セルは DB の NULL）。</summary>
public sealed record SqlitePreviewTable(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated,
    int ShownRows);

/// <summary>
/// SQLite ファイル（.db/.sqlite/.sqlite3）を <see cref="Microsoft.Data.Sqlite"/> で<b>読み取り専用</b>に開き、
/// ユーザーテーブルごとに先頭数行を「表示用の文字列セル」へ落とす純ロジック。UI からは
/// <see cref="SqliteEditorSupport"/> がこれを使って HTML テーブルを描く。<b>接続プールを無効にして</b>開くので、
/// <c>using</c> の破棄でファイルハンドルが確実に解放される（掴んだままにならない）。
/// </summary>
public static class SqlitePreviewReader
{
    // プレビュー上限。極端に大きい DB でも取り込みを抑える。
    private const int MaxTables = 20;
    private const int MaxRowsPerTable = 200;
    private const int MaxColumns = 100;

    public static IReadOnlyList<SqlitePreviewTable> Read(string filePath)
    {
        // 読み取り専用＋プール無効。プールを有効のままだと Dispose 後もプールが接続（＝ファイルハンドル）を
        // 保持し、ファイルを削除・上書きできなくなる。プレビューでは共有しないので Pooling=false にする。
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var tableNames = ReadTableNames(connection);
        var tables = new List<SqlitePreviewTable>(Math.Min(tableNames.Count, MaxTables));
        foreach (var name in tableNames.Take(MaxTables))
            tables.Add(ReadTable(connection, name));
        return tables;
    }

    private static List<string> ReadTableNames(SqliteConnection connection)
    {
        var names = new List<string>();
        using var command = connection.CreateCommand();
        // sqlite_ 接頭辞の内部テーブルは除外し、名前順で安定させる。
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static SqlitePreviewTable ReadTable(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        // テーブル名は識別子として二重引用符でクォート（内部の " は "" へエスケープ）。上限+1 行取り、
        // 余分に読めたら「切り詰めた」と判定する。
        var quoted = "\"" + tableName.Replace("\"", "\"\"") + "\"";
        command.CommandText = $"SELECT * FROM {quoted} LIMIT {MaxRowsPerTable + 1}";

        using var reader = command.ExecuteReader();

        var columnCount = Math.Min(reader.FieldCount, MaxColumns);
        var columns = new List<string>(columnCount);
        for (var c = 0; c < columnCount; c++)
            columns.Add(reader.GetName(c));

        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        while (reader.Read())
        {
            if (rows.Count >= MaxRowsPerTable)
            {
                truncated = true; // 上限+1 行目が読めた＝まだ続きがある。
                break;
            }

            var cells = new string?[columnCount];
            for (var c = 0; c < columnCount; c++)
                cells[c] = reader.IsDBNull(c) ? null : DatabasePreview.Stringify(reader.GetValue(c));
            rows.Add(cells);
        }

        return new SqlitePreviewTable(tableName, columns, rows, truncated, rows.Count);
    }
}

/// <summary>Parquet ファイルの先頭行プレビューデータ（表示用の文字列セル。<c>null</c> セルは NULL）。</summary>
public sealed record ParquetPreview(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated,
    int ShownRows);

/// <summary>
/// Parquet ファイル（.parquet）を <see cref="Parquet.Net"/> で読み、スキーマの列と先頭数行を
/// 「表示用の文字列セル」へ落とす純ロジック。UI からは <see cref="ParquetEditorSupport"/> が使う。
/// Parquet.Net 6 系は低レベル API 中心だが、<see cref="ParquetSerializer.DeserializeUntypedAsync"/> が
/// 行を <c>Dictionary&lt;列名,値&gt;</c> の並びで返すので、それを列順（スキーマ順）に整列して取り込む。
/// 行グループ単位で読み、上限に達したら打ち切る（巨大ファイルの全読み込みを避ける）。ファイルは
/// <see cref="File.OpenRead(string)"/> の読み取り専用ストリームで開き、破棄で解放する。
/// </summary>
public static class ParquetPreviewReader
{
    private const int MaxRows = 200;
    private const int MaxColumns = 100;

    public static ParquetPreview Read(string filePath)
    {
        // スキーマ（列順）と行グループ数を取る。ParquetReader は IAsyncDisposable のみなので、
        // 同期境界では DisposeAsync を待って破棄する。ストリームは自前で using して確実に閉じる。
        int rowGroupCount;
        List<string> columns;
        using (var stream = File.OpenRead(filePath))
        {
            var reader = ParquetReader.CreateAsync(stream, leaveStreamOpen: true).GetAwaiter().GetResult();
            try
            {
                rowGroupCount = reader.RowGroupCount;
                columns = reader.Schema.DataFields.Select(f => f.Name).Take(MaxColumns).ToList();
            }
            finally
            {
                reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;

        for (var g = 0; g < rowGroupCount && !truncated; g++)
        {
            // DeserializeUntypedAsync は内部で開いた ParquetReader と共に渡したストリームを閉じ得るので、
            // 行グループごとに読み取り専用ストリームを開き直す（掴みっぱなし・二重破棄を避ける）。
            using var stream = File.OpenRead(filePath);
            var result = ParquetSerializer
                .DeserializeUntypedAsync(stream, rowGroupIndex: g)
                .GetAwaiter().GetResult();

            foreach (var record in result.Data)
            {
                if (rows.Count >= MaxRows)
                {
                    truncated = true;
                    break;
                }

                var cells = new string?[columns.Count];
                for (var c = 0; c < columns.Count; c++)
                    cells[c] = record.TryGetValue(columns[c], out var value)
                        ? DatabasePreview.Stringify(value)
                        : null;
                rows.Add(cells);
            }
        }

        return new ParquetPreview(columns, rows, truncated, rows.Count);
    }
}

/// <summary>
/// SQLite データベース（.db/.sqlite/.sqlite3）の読み取り専用プレビュー。<see cref="SqlitePreviewReader"/> で
/// ユーザーテーブルごとに先頭行を読み、テーブル単位の見出し＋HTML テーブルにして EditorSupport ペインの
/// WebView2 へ表示する。エディタ本文は使わず（<see cref="UsesEditorText"/> = false）、ファイルパスから直接読む。
/// 表示専用で書き戻しはしない。
/// </summary>
public sealed class SqliteEditorSupport : IEditorSupportHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".db", ".sqlite", ".sqlite3"];

    public SqliteEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    // SQLite はバイナリ。エディタ本文は使わず、ファイルパスから直接読む。
    public bool UsesEditorText => false;

    public string DescribeTitle(string filePath) => $"SQLite: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
    {
        var theme = _settings.Appearance.MarkdownPreviewTheme;
        try
        {
            var tables = SqlitePreviewReader.Read(filePath);
            var body = new StringBuilder(DatabasePreview.BodyStyle);
            if (tables.Count == 0)
            {
                body.Append("<p class=\"db-empty\">テーブルがありません。</p>");
            }
            else
            {
                foreach (var table in tables)
                    body.Append(DatabasePreview.RenderSection(
                        table.Name, table.Columns, table.Rows, table.Truncated, table.ShownRows));
            }
            return MarkdownPage.BuildPage(body.ToString(), DescribeTitle(filePath), theme);
        }
        catch (Exception ex)
        {
            // .db は SQLite でないこともある（別形式・破損）。例外は握ってテーマ付きの案内ページを出す。
            return OfficePreview.ErrorPage(filePath, ex, theme);
        }
    }
}

/// <summary>
/// Parquet ファイル（.parquet）の読み取り専用プレビュー。<see cref="ParquetPreviewReader"/> で列と先頭行を
/// 読み、1枚の HTML テーブルにして EditorSupport ペインの WebView2 へ表示する。エディタ本文は使わず
/// （<see cref="UsesEditorText"/> = false）、ファイルパスから直接読む。表示専用で書き戻しはしない。
/// </summary>
public sealed class ParquetEditorSupport : IEditorSupportHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".parquet"];

    public ParquetEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    // Parquet はバイナリ。エディタ本文は使わず、ファイルパスから直接読む。
    public bool UsesEditorText => false;

    public string DescribeTitle(string filePath) => $"Parquet: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
    {
        var theme = _settings.Appearance.MarkdownPreviewTheme;
        try
        {
            var preview = ParquetPreviewReader.Read(filePath);
            var body = DatabasePreview.BodyStyle + DatabasePreview.RenderSection(
                Path.GetFileName(filePath), preview.Columns, preview.Rows, preview.Truncated, preview.ShownRows);
            return MarkdownPage.BuildPage(body, DescribeTitle(filePath), theme);
        }
        catch (Exception ex)
        {
            return OfficePreview.ErrorPage(filePath, ex, theme);
        }
    }
}
