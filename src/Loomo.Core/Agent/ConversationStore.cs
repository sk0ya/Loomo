using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>保存済みセッションの一覧表示用サマリ。</summary>
public sealed record SessionSummary(string Id, string Title, DateTime UpdatedAt);

/// <summary>復元したセッション（会話本体つき）。</summary>
public sealed record LoadedSession(string Id, string Title, Conversation Conversation);

/// <summary>
/// AI 会話セッションを <c>%APPDATA%/Loomo/sessions/{id}.json</c> に永続化する（Singleton 想定）。
/// 保存/削除のたびに <see cref="Changed"/> を発火し、一覧UIが追従する。UI 非依存。
/// </summary>
public sealed class ConversationStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dir;

    /// <summary>セッションの追加・更新・削除が起きたとき発火。</summary>
    public event Action? Changed;

    public ConversationStore() : this(DefaultDir()) { }

    public ConversationStore(string dir) => _dir = dir;

    public static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "sessions");

    /// <summary>更新日時の新しい順にセッション一覧を返す。</summary>
    public IReadOnlyList<SessionSummary> List()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<SessionSummary>();

        var list = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<SessionDto>(File.ReadAllText(file), JsonOpts);
                if (dto is { Id: not null })
                    list.Add(new SessionSummary(dto.Id, dto.Title ?? "(無題)", dto.UpdatedAt));
            }
            catch
            {
                // 壊れたファイルは一覧から除外
            }
        }
        return list.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    /// <summary>セッションを読み込み会話を復元する。無ければ null。</summary>
    public LoadedSession? Load(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        SessionDto? dto;
        try { dto = JsonSerializer.Deserialize<SessionDto>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
        if (dto is null) return null;
        return new LoadedSession(dto.Id ?? id, dto.Title ?? "(無題)", dto.ToConversation());
    }

    /// <summary>会話を保存（id 指定で上書き、null で新規作成）し、セッションIDを返す。
    /// タイトルは最初のユーザー発言から自動生成する。</summary>
    public string Save(string? id, Conversation conversation)
    {
        Directory.CreateDirectory(_dir);
        id ??= Guid.NewGuid().ToString("N");
        var path = PathFor(id);

        var createdAt = DateTime.Now;
        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<SessionDto>(File.ReadAllText(path), JsonOpts);
                if (existing is not null) createdAt = existing.CreatedAt;
            }
            catch { /* 既存が壊れていれば作成日時は今 */ }
        }

        var dto = SessionDto.From(id, DeriveTitle(conversation), createdAt, DateTime.Now, conversation);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
        Changed?.Invoke();
        return id;
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        Changed?.Invoke();
    }

    private string PathFor(string id)
    {
        // パストラバーサル防止：ファイル名に使えない文字を除去
        var safe = string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (safe.Length == 0) safe = Guid.NewGuid().ToString("N");
        return Path.Combine(_dir, safe + ".json");
    }

    private static string DeriveTitle(Conversation conversation)
    {
        var firstUser = conversation.Messages
            .FirstOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text));
        var text = firstUser?.Text?.Trim() ?? "";
        if (text.Length == 0) return "新しいセッション";
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 40 ? text : text[..40] + "…";
    }

    // ===== 永続化DTO =====

    private sealed class SessionDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<MessageDto> Messages { get; set; } = new();

        public static SessionDto From(string id, string title, DateTime createdAt, DateTime updatedAt, Conversation c)
        {
            var dto = new SessionDto { Id = id, Title = title, CreatedAt = createdAt, UpdatedAt = updatedAt };
            foreach (var m in c.Messages)
            {
                var md = new MessageDto { Role = m.Role, Text = m.Text };
                foreach (var u in m.ToolUses)
                    md.ToolUses.Add(new ToolUseDto { Id = u.Id, Name = u.Name, ArgumentsJson = u.ArgumentsJson });
                foreach (var r in m.ToolResults)
                    md.ToolResults.Add(new ToolResultDto { ToolUseId = r.ToolUseId, Content = r.Content, IsError = r.IsError });
                dto.Messages.Add(md);
            }
            return dto;
        }

        public Conversation ToConversation()
        {
            var c = new Conversation();
            foreach (var md in Messages)
            {
                var m = new ChatMessage { Role = md.Role, Text = md.Text };
                foreach (var u in md.ToolUses)
                    m.ToolUses.Add(new ToolUse(u.Id ?? "", u.Name ?? "", u.ArgumentsJson ?? "{}"));
                foreach (var r in md.ToolResults)
                    m.ToolResults.Add(new ToolResultMessage(r.ToolUseId ?? "", r.Content ?? "", r.IsError));
                c.Messages.Add(m);
            }
            return c;
        }
    }

    private sealed class MessageDto
    {
        public ChatRole Role { get; set; }
        public string? Text { get; set; }
        public List<ToolUseDto> ToolUses { get; set; } = new();
        public List<ToolResultDto> ToolResults { get; set; } = new();
    }

    private sealed class ToolUseDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? ArgumentsJson { get; set; }
    }

    private sealed class ToolResultDto
    {
        public string? ToolUseId { get; set; }
        public string? Content { get; set; }
        public bool IsError { get; set; }
    }
}
