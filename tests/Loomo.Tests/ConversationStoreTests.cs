using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Tests;

public class ConversationStoreTests
{
    private static ConversationStore NewStore()
        => new(Path.Combine(Path.GetTempPath(), "loomo-test-sessions-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Save_then_load_round_trips_messages_and_tool_calls()
    {
        var store = NewStore();
        var conv = new Conversation();
        conv.AddUser("ビルドして");
        var assistant = new ChatMessage { Role = ChatRole.Assistant, Text = "了解しました" };
        assistant.ToolUses.Add(new ToolUse("t1", "run_powershell", "{\"command\":\"dotnet build\"}", "{\"function\":{\"name\":\"run_powershell\"}}"));
        conv.Messages.Add(assistant);
        var toolMsg = new ChatMessage { Role = ChatRole.Tool };
        toolMsg.ToolResults.Add(new ToolResultMessage("t1", "成功", IsError: false));
        conv.Messages.Add(toolMsg);

        var id = store.Save(null, conv);
        var loaded = store.Load(id);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded!.Id);
        Assert.Equal("ビルドして", loaded.Title);           // 最初のユーザー発言がタイトル
        Assert.Equal(3, loaded.Conversation.Messages.Count);
        var a = loaded.Conversation.Messages[1];
        Assert.Equal("了解しました", a.Text);
        Assert.Single(a.ToolUses);
        Assert.Equal("run_powershell", a.ToolUses[0].Name);
        Assert.Equal("{\"function\":{\"name\":\"run_powershell\"}}", a.ToolUses[0].RawJson);
        Assert.Equal("成功", loaded.Conversation.Messages[2].ToolResults[0].Content);
    }

    [Fact]
    public void Save_then_load_round_trips_progress_log()
    {
        var store = NewStore();
        var conv = new Conversation();
        var user = conv.AddUser("ビルドして");
        user.ProgressLog = "[0 ms] ⚙️ 実行構成: モデル phi-4-mini\n[16.6 秒] 📊 AI内訳#1: トークン 入力347/出力30";
        conv.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = "完了" });

        var id = store.Save(null, conv);
        var loaded = store.Load(id);

        Assert.NotNull(loaded);
        Assert.Equal(user.ProgressLog, loaded!.Conversation.Messages[0].ProgressLog);
        Assert.Null(loaded.Conversation.Messages[1].ProgressLog);   // assistant には付かない
    }

    [Fact]
    public void Save_with_same_id_updates_in_place()
    {
        var store = NewStore();
        var conv = new Conversation();
        conv.AddUser("最初");

        var id = store.Save(null, conv);
        conv.AddUser("追記");
        var id2 = store.Save(id, conv);

        Assert.Equal(id, id2);
        Assert.Single(store.List());
    }

    [Fact]
    public void Delete_removes_session_and_raises_changed()
    {
        var store = NewStore();
        var raised = 0;
        store.Changed += () => raised++;

        var id = store.Save(null, new Conversation());   // raised -> 1
        Assert.Single(store.List());

        store.Delete(id);                                 // raised -> 2
        Assert.Empty(store.List());
        Assert.Equal(2, raised);
    }
}
