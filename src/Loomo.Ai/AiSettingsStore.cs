using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.Ai;

/// <summary>
/// <see cref="AiSettings"/> を <c>%APPDATA%/Loomo/settings.json</c> に永続化する。
/// APIキーは平文保存せず、DPAPI(<see cref="DataProtectionScope.CurrentUser"/>)で暗号化して書き出す。
/// DPAPI は Windows 専用（本アプリは WPF / Windows 専用なので問題なし）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public AiSettingsStore() : this(DefaultPath()) { }

    public AiSettingsStore(string filePath) => _filePath = filePath;

    public string FilePath => _filePath;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "settings.json");

    /// <summary>保存済み設定を読み込み <paramref name="settings"/> に反映する。
    /// ファイルが無い・壊れている場合は既定値のままにして何もしない。</summary>
    public void Load(AiSettings settings)
    {
        if (!File.Exists(_filePath)) return;
        PersistedSettings? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(_filePath), JsonOpts);
        }
        catch
        {
            return; // 破損時は既定のまま起動する
        }
        dto?.ApplyTo(settings);
    }

    /// <summary>現在の設定をファイルへ保存する（APIキーは暗号化）。</summary>
    public void Save(AiSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var dto = PersistedSettings.From(settings);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(dto, JsonOpts));
    }

    // ===== DPAPI helpers =====

    internal static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    internal static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(cipher), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null; // 別ユーザー/別マシン/破損では復号できない
        }
    }

    // ===== 永続化用DTO（APIキーは暗号化文字列で保持） =====

    private sealed class PersistedSettings
    {
        public AppTheme Theme { get; set; }
        public string? AccentColor { get; set; }
        public string? SystemPrompt { get; set; }
        public PersistedProvider Local { get; set; } = new();
        public PersistedSafety Safety { get; set; } = new();
        public PersistedObservability? Observability { get; set; }

        public static PersistedSettings From(AiSettings s) => new()
        {
            Theme = s.Theme,
            AccentColor = s.AccentColor,
            SystemPrompt = s.SystemPrompt,
            Local = PersistedProvider.From(s.Local),
            Safety = PersistedSafety.From(s.Safety),
            Observability = PersistedObservability.From(s.Observability),
        };

        public void ApplyTo(AiSettings s)
        {
            s.Provider = AiProvider.Local;
            s.Theme = Theme;
            s.AccentColor = string.IsNullOrWhiteSpace(AccentColor) ? null : AccentColor;
            if (!string.IsNullOrWhiteSpace(SystemPrompt)) s.SystemPrompt = SystemPrompt;
            Local.ApplyTo(s.Local);
            Safety.ApplyTo(s.Safety);
            Observability?.ApplyTo(s.Observability); // 旧設定（null）は in-memory 既定を維持
        }
    }

    // ===== 観測性（§20）。平文で保持（秘匿情報ではない）。 =====

    private sealed class PersistedObservability
    {
        public bool EnableTracing { get; set; } = true;
        public int MaxSessions { get; set; } = 200;

        public static PersistedObservability From(ObservabilitySettings o) => new()
        {
            EnableTracing = o.EnableTracing,
            MaxSessions = o.MaxSessions,
        };

        public void ApplyTo(ObservabilitySettings o)
        {
            o.EnableTracing = EnableTracing;
            o.MaxSessions = MaxSessions;
        }
    }

    // ===== 安全設計（§10）。平文で保持（秘匿情報ではない）。 =====

    private sealed class PersistedSafety
    {
        public bool AutoApprove { get; set; }
        public bool RestrictToWorkspaceRoot { get; set; } = true;
        public List<string>? BlockedCommandPatterns { get; set; }

        public static PersistedSafety From(SafetySettings s) => new()
        {
            AutoApprove = s.AutoApprove,
            RestrictToWorkspaceRoot = s.RestrictToWorkspaceRoot,
            BlockedCommandPatterns = s.BlockedCommandPatterns.ToList(),
        };

        // 既存インスタンスを書き換える（DI シングルトンの参照を保つため置き換えない）
        public void ApplyTo(SafetySettings s)
        {
            s.AutoApprove = AutoApprove;
            s.RestrictToWorkspaceRoot = RestrictToWorkspaceRoot;
            if (BlockedCommandPatterns is not null)
            {
                s.BlockedCommandPatterns.Clear();
                s.BlockedCommandPatterns.AddRange(BlockedCommandPatterns);
            }
        }
    }

    private sealed class PersistedProvider
    {
        public string? Model { get; set; }
        public string? ApiKeyEnc { get; set; }
        public string? BaseUrl { get; set; }
        public int MaxTokens { get; set; } = 4096;
        public string? ThinkingEffort { get; set; }

        // null = 旧設定/未指定 → 既定値を維持。0 = トリム無効（明示）。n>0 = その上限。
        // 非nullable + 既定値だと「未指定」と「0=無効」と「明示値」を区別できないため int? にする。
        public int? MaxContextTokens { get; set; }

        public static PersistedProvider From(ProviderConfig c) => new()
        {
            Model = c.Model,
            ApiKeyEnc = Protect(c.ApiKey),
            BaseUrl = c.BaseUrl,
            MaxTokens = c.MaxTokens,
            ThinkingEffort = c.ThinkingEffort,
            MaxContextTokens = c.MaxContextTokens,
        };

        public void ApplyTo(ProviderConfig c)
        {
            if (!string.IsNullOrEmpty(Model)) c.Model = Model;
            c.ApiKey = Unprotect(ApiKeyEnc);
            if (BaseUrl is not null) c.BaseUrl = BaseUrl;
            if (MaxTokens > 0) c.MaxTokens = MaxTokens;
            if (!string.IsNullOrWhiteSpace(ThinkingEffort)) c.ThinkingEffort = ThinkingEffort;
            // 値があれば適用（0=無効も尊重）。未指定(null)なら in-memory 既定を保つ。
            if (MaxContextTokens is { } mct && mct >= 0) c.MaxContextTokens = mct;
        }
    }
}
