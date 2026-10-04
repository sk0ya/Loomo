using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Settings;
using sk0ya.Loomo.Services.Lsp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// エクスプローラでの移動・改名に合わせた参照の更新（<c>workspace/willRenameFiles</c>／<c>didRenameFiles</c>）。
/// fileOperations の filters 判定、送り先の選び方（マルチルート・ワークスペース外）、確認と設定、
/// そして移動の全経路が通る <see cref="FolderTreeCommandHandler"/> での前後の呼び出し順を確かめる。
/// </summary>
public sealed class LspFileRenameTests : IDisposable
{
    private readonly string _root;
    private readonly string _other;
    private readonly FakeWorkspaceService _workspace = new();
    private readonly List<FakeLspClient> _created = [];
    private readonly LspWorkspaceService _lsp;

    public LspFileRenameTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "loomo-lsp-rename-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "app");
        _other = Path.Combine(baseDir, "lib");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_other);
        _workspace.OpenFolder(_root);

        var servers = new LspServerTable(Path.Combine(baseDir, "lsp-servers.json"));
        servers.Set(".ts", new LspServerDef("fake-ts-server", [], "typescript"));
        servers.Set(".cs", new LspServerDef("fake-cs-server", [], "csharp"));
        _lsp = new LspWorkspaceService(_workspace, servers, (def, root) =>
        {
            var client = new FakeLspClient(def.Executable, root);
            lock (_created) _created.Add(client);
            return client;
        });
    }

    public void Dispose()
    {
        _lsp.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    private string Write(string folder, string relative, string text = "export const a = 1;")
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>そのフォルダーの .ts を開いてサーバーを起動し、TypeScript と同じ宣言を持たせる。</summary>
    private async Task<(FakeLspClient Client, ILspDocument Doc)> StartTsServerAsync(string folder)
    {
        var doc = _lsp.OpenDocument(Write(folder, "main.ts", "import { a } from './a';"), "import { a } from './a';")!;
        await Task.Delay(100);
        FakeLspClient client;
        lock (_created) client = _created.Last(c => c.Root.Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase));
        client.DeclareTypeScriptFileOperations();
        return (client, doc);
    }

    private static LspWorkspaceEdit ImportEdit(string mainPath, string newText) => new(
        new Dictionary<string, IReadOnlyList<LspTextEdit>>
        {
            [LspUri.FromPath(mainPath)] =
                [new LspTextEdit(new LspRange(new LspPosition(0, 19), new LspPosition(0, 24)), newText)],
        },
        null,
        []);

    // ── filters ──────────────────────────────────────────────────────────

    [Fact]
    public void Filters_match_typescript_files_and_folders_only()
    {
        var client = new FakeLspClient("x", "c:\\w");
        client.DeclareTypeScriptFileOperations();
        var filters = LspFileOperationFilters.Parse(client.ServerCapabilities, "willRename")!;

        Assert.True(filters.Matches(LspUri.FromPath(@"C:\w\src\a.ts"), isDirectory: false));
        Assert.True(filters.Matches(LspUri.FromPath(@"C:\w\src\Comp.TSX"), isDirectory: false));
        Assert.True(filters.Matches(LspUri.FromPath(@"C:\w\src\部品"), isDirectory: true));
        Assert.False(filters.Matches(LspUri.FromPath(@"C:\w\README.md"), isDirectory: false));
    }

    [Fact]
    public void Filters_are_null_when_the_server_does_not_declare_the_operation()
    {
        using var caps = JsonDocument.Parse("""{"workspace":{"workspaceFolders":{"supported":true}}}""");

        Assert.Null(LspFileOperationFilters.Parse(caps.RootElement.Clone(), "willRename"));
        Assert.Null(LspFileOperationFilters.Parse(null, "willRename"));
    }

    [Theory]
    [InlineData("**/*.cs", "/c:/w/src/A.cs", true)]
    [InlineData("**/*.cs", "/c:/w/src/A.csx", false)]
    [InlineData("src/**", "/c:/w/src/deep/a.ts", true)]
    [InlineData("*.ts", "/c:/w/src/a.ts", true)]
    [InlineData("**/[!.]*.ts", "/c:/w/.hidden.ts", false)]
    [InlineData("**/?.ts", "/c:/w/a.ts", true)]
    public void Glob_follows_lsp_glob_pattern(string glob, string path, bool expected)
        => Assert.Equal(expected, LspFileOperationFilters.TryCompileGlob(glob)!.IsMatch(path));

    [Fact]
    public void Scheme_and_kind_are_respected()
    {
        using var caps = JsonDocument.Parse("""
            {"workspace":{"fileOperations":{"willRename":{"filters":[
              {"scheme":"untitled","pattern":{"glob":"**/*.ts"}},
              {"pattern":{"glob":"**/*.cs","matches":"file"}}]}}}}
            """);
        var filters = LspFileOperationFilters.Parse(caps.RootElement.Clone(), "willRename")!;

        Assert.False(filters.Matches(LspUri.FromPath(@"C:\w\a.ts"), false));
        Assert.True(filters.Matches(LspUri.FromPath(@"C:\w\A.cs"), false));
        Assert.False(filters.Matches(LspUri.FromPath(@"C:\w\A.cs"), true));
    }

    // ── 送り先の選び方 ────────────────────────────────────────────────────

    [Fact]
    public async Task WillRename_asks_the_declaring_server_and_returns_its_edit()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        var main = Path.Combine(_root, "main.ts");
        client.WillRenameProvider = _ => ImportEdit(main, "./lib/a");
        var oldPath = Write(_root, "a.ts");
        var newPath = Path.Combine(_root, "lib", "a.ts");

        var result = await _lsp.WillRenameFilesAsync([new LspFileRename(oldPath, newPath, false)]);

        Assert.Equal(1, result.ServersAsked);
        Assert.Null(result.Error);
        var (uri, edits) = Assert.Single(result.Edit!.Changes);
        Assert.True(LspUri.MatchesPath(uri, main));
        Assert.Equal("./lib/a", Assert.Single(edits).NewText);
        var request = Assert.Single(client.WillRenameRequests);
        Assert.Equal((LspUri.FromPath(oldPath), LspUri.FromPath(newPath)), Assert.Single(request));
    }

    [Fact]
    public async Task WillRename_is_not_sent_for_files_the_server_did_not_ask_about()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        var md = Write(_root, "README.md", "# x");

        var result = await _lsp.WillRenameFilesAsync(
            [new LspFileRename(md, Path.Combine(_root, "docs", "README.md"), false)]);

        Assert.Equal(0, result.ServersAsked);
        Assert.Empty(client.WillRenameRequests);
    }

    [Fact]
    public async Task WillRename_is_not_sent_to_a_server_without_the_capability()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        client.ServerCapabilities = null;

        var result = await _lsp.WillRenameFilesAsync(
            [new LspFileRename(Write(_root, "a.ts"), Path.Combine(_root, "b.ts"), false)]);

        Assert.Equal(0, result.ServersAsked);
        Assert.Empty(client.WillRenameRequests);
    }

    [Fact]
    public async Task Moving_out_of_the_workspace_is_not_announced()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        var outside = Path.Combine(Path.GetTempPath(), "loomo-outside-" + Guid.NewGuid().ToString("N"), "a.ts");

        var result = await _lsp.WillRenameFilesAsync([new LspFileRename(Write(_root, "a.ts"), outside, false)]);
        _lsp.DidRenameFiles([new LspFileRename(Path.Combine(_root, "a.ts"), outside, false)]);

        Assert.Equal(0, result.ServersAsked);
        Assert.Empty(client.WillRenameRequests);
        Assert.Empty(client.DidRenameNotifications);
    }

    [Fact]
    public async Task Multi_root_sends_only_to_the_server_of_the_owning_folder()
    {
        _workspace.AddFolder(_other);
        var (primary, primaryDoc) = await StartTsServerAsync(_root);
        var (added, addedDoc) = await StartTsServerAsync(_other);
        using var _ = primaryDoc;
        using var __ = addedDoc;
        Assert.NotSame(primary, added);

        var source = Write(_other, "util.ts");
        await _lsp.WillRenameFilesAsync([new LspFileRename(source, Path.Combine(_other, "src", "util.ts"), false)]);

        Assert.Empty(primary.WillRenameRequests);
        Assert.Single(added.WillRenameRequests);
    }

    [Fact]
    public async Task DidRename_notifies_the_declaring_server()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        var folder = Path.Combine(_root, "src");

        _lsp.DidRenameFiles([new LspFileRename(folder, Path.Combine(_root, "lib"), true)]);

        var notification = Assert.Single(client.DidRenameNotifications);
        Assert.Equal(LspUri.FromPath(folder), Assert.Single(notification).OldUri);
    }

    // ── 参加者（確認・設定・適用） ─────────────────────────────────────────

    private (LspFileMoveParticipant Participant, List<LspWorkspaceEdit> Applied, List<FileMoveReferencePrompt> Prompts,
        List<string> Errors) CreateParticipant(LspSettings settings, FileMoveReferenceAnswer answer)
    {
        var applied = new List<LspWorkspaceEdit>();
        var prompts = new List<FileMoveReferencePrompt>();
        var errors = new List<string>();
        var participant = new LspFileMoveParticipant(_lsp, settings, errors.Add);
        participant.Attach(
            edit => { applied.Add(edit); return null; },
            prompt => { prompts.Add(prompt); return answer; });
        return (participant, applied, prompts, errors);
    }

    [Fact]
    public async Task Prompt_mode_asks_with_the_file_count_and_applies_on_yes()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        client.WillRenameProvider = _ => ImportEdit(Path.Combine(_root, "main.ts"), "./b");
        var settings = new LspSettings();
        var (participant, applied, prompts, errors) = CreateParticipant(settings, FileMoveReferenceAnswer.Update);
        var source = Write(_root, "a.ts");

        participant.BeforeMove(source, Path.Combine(_root, "b.ts"), false);

        var prompt = Assert.Single(prompts);
        Assert.Equal(1, prompt.FileCount);
        Assert.Equal("a.ts", prompt.Name);
        Assert.Single(applied);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Prompt_mode_skips_the_edit_on_no()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        client.WillRenameProvider = _ => ImportEdit(Path.Combine(_root, "main.ts"), "./b");
        var (participant, applied, prompts, _) = CreateParticipant(new LspSettings(), FileMoveReferenceAnswer.Skip);

        participant.BeforeMove(Write(_root, "a.ts"), Path.Combine(_root, "b.ts"), false);

        Assert.Single(prompts);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task Choosing_always_is_remembered_and_stops_asking()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        client.WillRenameProvider = _ => ImportEdit(Path.Combine(_root, "main.ts"), "./b");
        var settings = new LspSettings();
        var (participant, applied, prompts, _) = CreateParticipant(settings, FileMoveReferenceAnswer.Always);

        participant.BeforeMove(Write(_root, "a.ts"), Path.Combine(_root, "b.ts"), false);
        participant.BeforeMove(Write(_root, "c.ts"), Path.Combine(_root, "d.ts"), false);

        Assert.Equal(FileMoveReferenceUpdate.Always, settings.UpdateReferencesOnFileMove);
        Assert.Single(prompts);
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public async Task Never_mode_does_not_even_ask_the_server()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        var settings = new LspSettings { UpdateReferencesOnFileMove = FileMoveReferenceUpdate.Never };
        var (participant, applied, prompts, _) = CreateParticipant(settings, FileMoveReferenceAnswer.Update);

        participant.BeforeMove(Write(_root, "a.ts"), Path.Combine(_root, "b.ts"), false);

        Assert.Empty(client.WillRenameRequests);
        Assert.Empty(prompts);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task No_edit_means_no_prompt()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        client.WillRenameProvider = _ => null;
        var (participant, applied, prompts, _) = CreateParticipant(new LspSettings(), FileMoveReferenceAnswer.Update);

        participant.BeforeMove(Write(_root, "a.ts"), Path.Combine(_root, "b.ts"), false);

        Assert.Single(client.WillRenameRequests);
        Assert.Empty(prompts);
        Assert.Empty(applied);
    }

    // ── 移動の全経路が通るハンドラー ──────────────────────────────────────

    private sealed class RecordingParticipant : IFileMoveParticipant
    {
        public List<string> Calls { get; } = [];
        public void BeforeMove(string source, string destination, bool isDirectory)
            => Calls.Add($"before:{File.Exists(source) || Directory.Exists(source)}:{Path.GetFileName(destination)}");
        public void AfterMove(string source, string destination, bool isDirectory)
            => Calls.Add($"after:{File.Exists(destination) || Directory.Exists(destination)}:{Path.GetFileName(destination)}");
        public void MoveFailed(string source, string destination, bool isDirectory)
            => Calls.Add($"failed:{File.Exists(source) || Directory.Exists(source)}:{Path.GetFileName(destination)}");
    }

    /// <summary>移動が失敗したら after ではなく failed——前もって当てた参照の更新を戻す機会を渡す
    /// （渡さないと import だけが存在しない新しい名前を指したまま残る）。</summary>
    [Fact]
    public void Handler_reports_a_failed_rename_instead_of_after()
    {
        var participant = new RecordingParticipant();
        var handler = new FolderTreeCommandHandler(_workspace, new FileOperationHistory(), participant);
        var source = Write(_root, "a.ts");

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<InvalidOperationException>(() => handler.Rename(source, "b.ts", isDirectory: false));

        Assert.Equal(["before:True:b.ts", "failed:True:b.ts"], participant.Calls);
    }

    [Fact]
    public void Handler_reports_a_failed_paste_move()
    {
        var participant = new RecordingParticipant();
        var handler = new FolderTreeCommandHandler(_workspace, new FileOperationHistory(), participant);
        var target = Path.Combine(_root, "lib");
        Directory.CreateDirectory(target);
        var source = Write(_root, "moved.ts");

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<Exception>(() => handler.PasteWithConflict(target, source, move: true, resolver: null));

        Assert.Equal(["before:True:moved.ts", "failed:True:moved.ts"], participant.Calls);
    }

    /// <summary>改名の Undo／Redo もファイルが動く——逆向き（b→a）の移動として前後を知らせる。
    /// 通さないと、Undo でファイルだけ a へ戻り、import は b を指したまま残っていた。</summary>
    [Fact]
    public void Undo_and_redo_of_a_rename_are_announced_as_moves()
    {
        var participant = new RecordingParticipant();
        var history = new FileOperationHistory(participant);
        var handler = new FolderTreeCommandHandler(_workspace, history, participant);
        handler.Rename(Write(_root, "a.ts"), "b.ts", isDirectory: false);
        participant.Calls.Clear();

        history.Undo();
        Assert.Equal(["before:True:a.ts", "after:True:a.ts"], participant.Calls);

        participant.Calls.Clear();
        history.Redo();
        Assert.Equal(["before:True:b.ts", "after:True:b.ts"], participant.Calls);
    }

    /// <summary>コピーの Undo は参照先を動かさないので何も知らせない。</summary>
    [Fact]
    public void Undo_of_a_copy_is_not_a_move()
    {
        var participant = new RecordingParticipant();
        var history = new FileOperationHistory(participant);
        var handler = new FolderTreeCommandHandler(_workspace, history, participant);
        var target = Path.Combine(_root, "lib");
        Directory.CreateDirectory(target);
        handler.PasteWithConflict(target, Write(_root, "copy.ts"), move: false, resolver: null);

        history.Undo();

        Assert.Empty(participant.Calls);
    }

    /// <summary>当てた編集は、移動が失敗したときだけ取り消す。成功した移動・当てなかった編集は戻さない。</summary>
    [Fact]
    public async Task Failed_move_reverts_the_applied_reference_update()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        client.WillRenameProvider = _ => ImportEdit(Path.Combine(_root, "main.ts"), "./b");
        var reverts = 0;
        var errors = new List<string>();
        var participant = new LspFileMoveParticipant(_lsp,
            new LspSettings { UpdateReferencesOnFileMove = FileMoveReferenceUpdate.Always }, errors.Add);
        participant.Attach(_ => null, revert: () => { reverts++; return null; });
        var source = Write(_root, "a.ts");
        var destination = Path.Combine(_root, "b.ts");

        participant.BeforeMove(source, destination, false);
        participant.AfterMove(source, destination, false);
        participant.MoveFailed(source, destination, false);   // 成功のあとに来ても戻さない
        Assert.Equal(0, reverts);

        participant.BeforeMove(source, destination, false);
        participant.MoveFailed(source, destination, false);
        Assert.Equal(1, reverts);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Failed_move_without_an_applied_edit_reverts_nothing()
    {
        var (client, doc) = await StartTsServerAsync(_root);
        using var _ = doc;
        client.WillRenameProvider = _ => ImportEdit(Path.Combine(_root, "main.ts"), "./b");
        var reverts = 0;
        var participant = new LspFileMoveParticipant(_lsp, new LspSettings(), _ => { });
        participant.Attach(_ => null, _ => FileMoveReferenceAnswer.Skip, () => { reverts++; return null; });

        participant.BeforeMove(Write(_root, "a.ts"), Path.Combine(_root, "b.ts"), false);
        participant.MoveFailed(Path.Combine(_root, "a.ts"), Path.Combine(_root, "b.ts"), false);

        Assert.Equal(0, reverts);
    }

    [Fact]
    public void Handler_announces_rename_before_and_after_the_move()
    {
        var participant = new RecordingParticipant();
        var handler = new FolderTreeCommandHandler(_workspace, new FileOperationHistory(), participant);

        handler.Rename(Write(_root, "a.ts"), "b.ts", isDirectory: false);

        Assert.Equal(["before:True:b.ts", "after:True:b.ts"], participant.Calls);
    }

    [Fact]
    public void Handler_announces_paste_moves_but_not_copies()
    {
        var participant = new RecordingParticipant();
        var handler = new FolderTreeCommandHandler(_workspace, new FileOperationHistory(), participant);
        var target = Path.Combine(_root, "lib");
        Directory.CreateDirectory(target);

        handler.PasteWithConflict(target, Write(_root, "copy.ts"), move: false, resolver: null);
        Assert.Empty(participant.Calls);

        handler.PasteWithConflict(target, Write(_root, "moved.ts"), move: true, resolver: null);
        Assert.Equal(["before:True:moved.ts", "after:True:moved.ts"], participant.Calls);
    }

    [Fact]
    public void Edits_counted_per_file()
    {
        var edit = new LspWorkspaceEdit(
            new Dictionary<string, IReadOnlyList<LspTextEdit>>
            {
                ["file:///c:/w/a.ts"] = [new LspTextEdit(new LspRange(new(0, 0), new(0, 0)), "x")],
                ["file:///c:/w/b.ts"] = [new LspTextEdit(new LspRange(new(0, 0), new(0, 0)), "y")],
                ["file:///c:/w/empty.ts"] = [],
            },
            null,
            [new LspFileOperation(LspFileOperationKind.Create, "file:///c:/w/a.ts")]);

        Assert.Equal(2, LspFileMoveParticipant.CountFiles(edit));
    }
}
