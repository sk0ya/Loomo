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

        /// <summary>AIウォームアップの有効/無効。null=旧設定（未指定）→ 既定（有効）を維持。</summary>
        public bool? WarmupEnabled { get; set; }

        public PersistedProvider Local { get; set; } = new();
        public PersistedSafety Safety { get; set; } = new();
        public PersistedObservability? Observability { get; set; }
        public PersistedVim? Vim { get; set; }
        public PersistedAppearance? Appearance { get; set; }
        public PersistedKeybindings? Keybindings { get; set; }

        public static PersistedSettings From(AiSettings s) => new()
        {
            Theme = s.Theme,
            AccentColor = s.AccentColor,
            WarmupEnabled = s.WarmupEnabled,
            Local = PersistedProvider.From(s.Local),
            Safety = PersistedSafety.From(s.Safety),
            Observability = PersistedObservability.From(s.Observability),
            Vim = PersistedVim.From(s.Vim),
            Appearance = PersistedAppearance.From(s.Appearance),
            Keybindings = PersistedKeybindings.From(s.Keybindings),
        };

        public void ApplyTo(AiSettings s)
        {
            s.Provider = AiProvider.Local;
            s.Theme = Theme;
            s.AccentColor = string.IsNullOrWhiteSpace(AccentColor) ? null : AccentColor;
            if (WarmupEnabled is { } warm) s.WarmupEnabled = warm; // 旧設定（null）は既定（有効）を維持
            Local.ApplyTo(s.Local);
            Safety.ApplyTo(s.Safety);
            Observability?.ApplyTo(s.Observability); // 旧設定（null）は in-memory 既定を維持
            Vim?.ApplyTo(s.Vim);
            Appearance?.ApplyTo(s.Appearance); // 旧設定（null）は in-memory 既定を維持
            Keybindings?.ApplyTo(s.Keybindings); // 旧設定（null）は既定割り当て（上書き無し）を維持
        }
    }

    // ===== キーボードショートカットの上書き。平文で保持（秘匿情報ではない）。 =====

    private sealed class PersistedKeybindings
    {
        /// <summary>コマンド Id → ジェスチャ表記。既定と異なるものだけ。</summary>
        public Dictionary<string, string> Overrides { get; set; } = new();

        public static PersistedKeybindings From(KeybindingSettings k) => new()
        {
            Overrides = new Dictionary<string, string>(k.Overrides),
        };

        // 既存インスタンスを書き換える（DI シングルトンの参照を保つため置き換えない）。
        public void ApplyTo(KeybindingSettings k)
        {
            k.Overrides.Clear();
            if (Overrides is null) return;
            foreach (var (id, gesture) in Overrides)
                k.Overrides[id] = gesture;
        }
    }

    // ===== 外観（エディタ/プレビュー/ターミナルの配色・フォント）。平文で保持。 =====

    private sealed class PersistedAppearance
    {
        public string? EditorTheme { get; set; }
        public string? EditorFontFamily { get; set; }
        public double EditorFontSize { get; set; }
        public string? MarkdownPreviewTheme { get; set; }
        public string? TerminalTheme { get; set; }
        public string? TerminalFontFamily { get; set; }
        public double TerminalFontSize { get; set; }

        public static PersistedAppearance From(AppearanceSettings a) => new()
        {
            EditorTheme = a.EditorTheme,
            EditorFontFamily = a.EditorFontFamily,
            EditorFontSize = a.EditorFontSize,
            MarkdownPreviewTheme = a.MarkdownPreviewTheme,
            TerminalTheme = a.TerminalTheme,
            TerminalFontFamily = a.TerminalFontFamily,
            TerminalFontSize = a.TerminalFontSize,
        };

        public void ApplyTo(AppearanceSettings a)
        {
            if (!string.IsNullOrWhiteSpace(EditorTheme)) a.EditorTheme = EditorTheme;
            a.EditorFontFamily = string.IsNullOrWhiteSpace(EditorFontFamily) ? null : EditorFontFamily;
            if (EditorFontSize > 0) a.EditorFontSize = EditorFontSize;
            if (!string.IsNullOrWhiteSpace(MarkdownPreviewTheme)) a.MarkdownPreviewTheme = MarkdownPreviewTheme;
            if (!string.IsNullOrWhiteSpace(TerminalTheme)) a.TerminalTheme = TerminalTheme;
            a.TerminalFontFamily = string.IsNullOrWhiteSpace(TerminalFontFamily) ? null : TerminalFontFamily;
            if (TerminalFontSize > 0) a.TerminalFontSize = TerminalFontSize;
        }
    }

    // ===== Vim エディタ設定。平文で保持（秘匿情報ではない）。 =====

    private sealed class PersistedVim
    {
        public bool Enabled { get; set; } = false;

        public static PersistedVim From(VimSettings v) => new()
        {
            Enabled = v.Enabled,
        };

        public void ApplyTo(VimSettings v)
        {
            v.Enabled = Enabled;
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

        /// <summary>ONNX モデルフォルダの絶対パス。</summary>
        public string? ModelPath { get; set; }
        public string? ApiKeyEnc { get; set; }

        /// <summary>旧設定からの読み込み互換用。現在は固定ローカルURLを使うため保存しない。</summary>
        [JsonPropertyName("baseUrl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyBaseUrl { get; set; }
        public int MaxTokens { get; set; } = 4096;

        /// <summary>thinking を有効にするか。null=未指定（既定維持）。</summary>
        public bool? Thinking { get; set; }

        /// <summary>旧形式（none/low/medium/high）。読み込み時の移行用にのみ保持し、書き出さない。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ThinkingEffort { get; set; }

        // null = 旧設定/未指定 → 既定値を維持。0 = トリム無効（明示）。n>0 = その上限。
        // 非nullable + 既定値だと「未指定」と「0=無効」と「明示値」を区別できないため int? にする。
        public int? MaxContextTokens { get; set; }

        /// <summary>num_ctx の上書き。null/0 = プロファイル既定を使う。</summary>
        public int? NumCtx { get; set; }

        /// <summary>num_gpu の上書き。null = 未指定（既定 -1 維持＝送らない）。0 = GPU オフロード無効（CPU 実行）。</summary>
        public int? NumGpu { get; set; }

        public static PersistedProvider From(ProviderConfig c) => new()
        {
            Model = c.Model,
            ModelPath = c.ModelPath,
            ApiKeyEnc = Protect(c.ApiKey),
            MaxTokens = c.MaxTokens,
            Thinking = c.Thinking,
            MaxContextTokens = c.MaxContextTokens,
            NumCtx = c.NumCtx,
            NumGpu = c.NumGpu,
        };

        public void ApplyTo(ProviderConfig c)
        {
            if (!string.IsNullOrEmpty(Model))
                c.Model = IsLegacyDefaultModel(Model) ? AiSettings.DefaultLocalModel : Model;
            if (!string.IsNullOrEmpty(ModelPath))
                c.ModelPath = ModelPath;
            c.ApiKey = Unprotect(ApiKeyEnc);
            if (MaxTokens > 0) c.MaxTokens = MaxTokens;
            // 新形式（bool）を優先。無ければ旧 ThinkingEffort（none 以外を有効）から移行する。
            if (Thinking is { } think) c.Thinking = think;
            else if (!string.IsNullOrWhiteSpace(ThinkingEffort))
                c.Thinking = !string.Equals(ThinkingEffort.Trim(), "none", StringComparison.OrdinalIgnoreCase);
            // 値があれば適用（0=無効も尊重）。未指定(null)なら in-memory 既定を保つ。
            if (MaxContextTokens is { } mct && mct >= 0) c.MaxContextTokens = mct;
            if (NumCtx is { } nc && nc >= 0) c.NumCtx = nc;
            // num_gpu は 0（CPU 強制）も負値（送らない）も有効値なので、ファイルに在れば素直に適用する。
            if (NumGpu is { } ng) c.NumGpu = ng;
        }

        private static bool IsLegacyDefaultModel(string model)
        {
            var id = model.Trim();
            return string.Equals(id, "llama3.1", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(id, "llama3.1:latest", StringComparison.OrdinalIgnoreCase);
        }
    }
}
