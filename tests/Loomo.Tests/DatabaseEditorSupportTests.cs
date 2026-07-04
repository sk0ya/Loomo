using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Schema;
using Parquet.Serialization;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// SQLite・Parquet の読み取り専用プレビュー（<see cref="SqliteEditorSupport"/> / <see cref="ParquetEditorSupport"/>）の検証。
/// 実ファイル（.sqlite/.parquet）を一時生成し、HTML テーブルにテーブル名・列名・セル値・&lt;table&gt; が
/// 含まれること、壊れ/存在しないファイルで例外を投げずエラーページになること、拡張子解決・タイトル・
/// 本文非依存（<see cref="IEditorSupportProvider.UsesEditorText"/>=false）を確かめる。SQLite は接続がファイルを
/// 掴んだままにならないこと（テスト後に削除できること）も finally の削除で担保する。
/// </summary>
public class DatabaseEditorSupportTests
{
    [Fact]
    public void 拡張子解決_db_sqlite_sqlite3はSqlite提供者へ()
    {
        var registry = BuildRegistry();
        Assert.IsType<SqliteEditorSupport>(registry.Resolve("data.db"));
        Assert.IsType<SqliteEditorSupport>(registry.Resolve("data.sqlite"));
        Assert.IsType<SqliteEditorSupport>(registry.Resolve("data.sqlite3"));
    }

    [Fact]
    public void 拡張子解決_parquetはParquet提供者へ()
    {
        var registry = BuildRegistry();
        Assert.IsType<ParquetEditorSupport>(registry.Resolve("data.parquet"));
    }

    [Fact]
    public void DescribeTitle_種別と名前を出す()
    {
        Assert.Equal("SQLite: data.sqlite", new SqliteEditorSupport(new AiSettings()).DescribeTitle("data.sqlite"));
        Assert.Equal("Parquet: data.parquet", new ParquetEditorSupport(new AiSettings()).DescribeTitle("data.parquet"));
    }

    [Fact]
    public void UsesEditorTextはfalse_本文非依存()
    {
        Assert.False(new SqliteEditorSupport(new AiSettings()).UsesEditorText);
        Assert.False(new ParquetEditorSupport(new AiSettings()).UsesEditorText);
    }

    [Fact]
    public void Sqlite_テーブル名_列名_セル値をHTMLテーブルへ()
    {
        var path = CreateSqlite();
        try
        {
            var html = new SqliteEditorSupport(new AiSettings()).RenderHtml(path, text: "");

            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("<table>", html);
            Assert.Contains("会員", html);      // テーブル名（見出し）
            Assert.Contains("名前", html);      // 列名
            Assert.Contains("太郎", html);      // 文字セル
            Assert.Contains("30", html);        // 数値セル
            Assert.Contains("db-null", html);   // NULL セルの淡色表記
        }
        finally { File.Delete(path); }   // 接続がファイルを掴んだままなら削除に失敗する
    }

    [Fact]
    public void Sqlite_存在しないファイルはエラーページ_例外を投げない()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");   // 作らない
        var html = new SqliteEditorSupport(new AiSettings()).RenderHtml(path, text: "");
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("表示できませんでした", html);
    }

    [Fact]
    public void Sqlite_壊れたファイルはエラーページ_例外を投げない()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        File.WriteAllText(path, "これは SQLite ではありません");
        try
        {
            var html = new SqliteEditorSupport(new AiSettings()).RenderHtml(path, text: "");
            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("表示できませんでした", html);
        }
        finally { File.Delete(path); }   // 破損ファイルでもハンドルが残らず削除できること
    }

    [Fact]
    public void Parquet_列名とセル値をHTMLテーブルへ()
    {
        var path = CreateParquet();
        try
        {
            var html = new ParquetEditorSupport(new AiSettings()).RenderHtml(path, text: "");

            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("<table>", html);
            Assert.Contains("id", html);        // 列名
            Assert.Contains("name", html);      // 列名
            Assert.Contains("花子", html);      // セル値
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Parquet_壊れたファイルはエラーページ_例外を投げない()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".parquet");
        File.WriteAllText(path, "これは Parquet ではありません");
        try
        {
            var html = new ParquetEditorSupport(new AiSettings()).RenderHtml(path, text: "");
            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("表示できませんでした", html);
        }
        finally { File.Delete(path); }
    }

    private static EditorSupportRegistry BuildRegistry()
    {
        var settings = new AiSettings();
        return new EditorSupportRegistry(new IEditorSupportProvider[]
        {
            new SqliteEditorSupport(settings),
            new ParquetEditorSupport(settings),
        });
    }

    /// <summary>会員テーブル（NULL を含む）を持つ一時 SQLite ファイルを作る。プール無効で開き、削除できる状態にする。</summary>
    private static string CreateSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE 会員 (名前 TEXT, 年齢 INTEGER, メモ TEXT);" +
                "INSERT INTO 会員 VALUES ('太郎', 30, NULL);" +
                "INSERT INTO 会員 VALUES ('次郎', 25, '常連');";
            command.ExecuteNonQuery();
            SqliteConnection.ClearPool(connection);
        }
        return path;
    }

    /// <summary>id/name の2列・数行の一時 Parquet ファイルを作る。</summary>
    private static string CreateParquet()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".parquet");
        var records = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "太郎" },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = "花子" },
        };
        var schema = new ParquetSchema(
            new DataField<int>("id"),
            new DataField<string>("name"));

        using (var stream = File.Create(path))
            ParquetSerializer.SerializeUntypedAsync(records, schema, stream).GetAwaiter().GetResult();
        return path;
    }
}
