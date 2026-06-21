using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using sk0ya.Loomo.Core.IO;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>保存済みワークフローの一覧表示用サマリ。</summary>
public sealed record WorkflowSummary(string Id, string Name, DateTime UpdatedAt);

/// <summary>
/// 名前付きワークフロー定義を <c>%APPDATA%/Loomo/workflows/{id}.json</c> に永続化する（Singleton 想定）。
/// 保存/削除のたびに <see cref="Changed"/> を発火し、一覧UIが追従する。UI 非依存。
/// <see cref="ConversationStore"/> と同じ作法。
/// </summary>
public sealed class WorkflowStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dir;

    /// <summary>ワークフローの追加・更新・削除が起きたとき発火。</summary>
    public event Action? Changed;

    public WorkflowStore() : this(DefaultDir()) { }

    public WorkflowStore(string dir) => _dir = dir;

    public static string DefaultDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "workflows");

    /// <summary>更新日時の新しい順にワークフロー一覧を返す。</summary>
    public IReadOnlyList<WorkflowSummary> List()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<WorkflowSummary>();

        var list = new List<WorkflowSummary>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<WorkflowDto>(File.ReadAllText(file), JsonOpts);
                if (dto is { Id: not null })
                    list.Add(new WorkflowSummary(dto.Id, dto.Name ?? "(無題)", dto.UpdatedAt));
            }
            catch
            {
                // 壊れたファイルは一覧から除外
            }
        }
        return list.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    /// <summary>ワークフローを読み込む。無ければ null。</summary>
    public Workflow? Load(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        WorkflowDto? dto;
        try { dto = JsonSerializer.Deserialize<WorkflowDto>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
        return dto?.ToWorkflow(id);
    }

    /// <summary>ワークフローを保存（Id 指定で上書き、null で新規採番）し、IDを返す。</summary>
    public string Save(Workflow workflow)
    {
        Directory.CreateDirectory(_dir);
        var id = workflow.Id ?? Guid.NewGuid().ToString("N");
        var path = PathFor(id);

        var createdAt = DateTime.Now;
        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<WorkflowDto>(File.ReadAllText(path), JsonOpts);
                if (existing is not null) createdAt = existing.CreatedAt;
            }
            catch { /* 既存が壊れていれば作成日時は今 */ }
        }

        var dto = WorkflowDto.From(id, createdAt, DateTime.Now, workflow);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
        workflow.Id = id;
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
        var safe = SafeFileName.Sanitize(id);
        if (safe.Length == 0) safe = Guid.NewGuid().ToString("N");
        return Path.Combine(_dir, safe + ".json");
    }

    // ===== 永続化DTO =====

    private sealed class WorkflowDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<StepDto> Steps { get; set; } = new();

        public static WorkflowDto From(string id, DateTime createdAt, DateTime updatedAt, Workflow w)
        {
            var dto = new WorkflowDto
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(w.Name) ? "(無題)" : w.Name,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
            };
            foreach (var s in w.Steps)
                dto.Steps.Add(new StepDto { Title = s.Title, Prompt = s.Prompt });
            return dto;
        }

        public Workflow ToWorkflow(string id)
        {
            var w = new Workflow { Id = id, Name = Name ?? "(無題)" };
            foreach (var s in Steps)
                w.Steps.Add(new WorkflowStep
                {
                    Title = s.Title ?? "",
                    Prompt = s.Prompt ?? "",
                });
            return w;
        }
    }

    private sealed class StepDto
    {
        public string? Title { get; set; }
        public string? Prompt { get; set; }
    }
}
