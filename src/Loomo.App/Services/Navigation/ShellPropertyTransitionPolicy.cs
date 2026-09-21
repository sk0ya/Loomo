using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct ShellPropertyEffects(
    bool ApplySidebarVisibility = false,
    bool ApplySettingsWindowState = false,
    bool ActivateSettingsWindow = false,
    bool RecordPanelTrail = false,
    bool FocusSidebar = false);

/// <summary>ShellViewModel の状態変更から、画面側で行う遷移効果を決める。</summary>
internal static class ShellPropertyTransitionPolicy
{
    internal static ShellPropertyEffects Resolve(ShellViewModel viewModel, string? propertyName)
        => propertyName switch
        {
            nameof(ShellViewModel.IsSidebarVisible) => new(
                ApplySidebarVisibility: true,
                RecordPanelTrail: viewModel.IsSidebarVisible,
                FocusSidebar: viewModel.IsSidebarVisible),
            nameof(ShellViewModel.IsSettingsOverlayOpen) => new(ApplySettingsWindowState: true),
            nameof(ShellViewModel.SettingsCategory) when viewModel.IsSettingsOverlayOpen =>
                new(ActivateSettingsWindow: true),
            nameof(ShellViewModel.ActivePanel) when !viewModel.IsPanelChangeAutomatic => new(
                RecordPanelTrail: true,
                FocusSidebar: true),
            _ => default,
        };
}
