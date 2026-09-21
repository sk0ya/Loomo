using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct ShellPropertyEffects(
    bool ApplySidebarVisibility = false,
    bool ApplySettingsWindowState = false,
    bool ActivateSettingsWindow = false,
    SidebarPanel? RecordPanelTrail = null,
    ActivityBarSlot? FocusSidebarSlot = null);

/// <summary>ShellViewModel の状態変更から、画面側で行う遷移効果を決める。
/// サイドバーは上段・中段の2区画あり、どちらが動いたかで軌跡へ書く面とフォーカス先が変わる。</summary>
internal static class ShellPropertyTransitionPolicy
{
    internal static ShellPropertyEffects Resolve(ShellViewModel viewModel, string? propertyName)
        => propertyName switch
        {
            nameof(ShellViewModel.IsSidebarVisible) => new(
                ApplySidebarVisibility: true,
                RecordPanelTrail: viewModel.IsSidebarVisible ? viewModel.ActivePanel : null,
                FocusSidebarSlot: viewModel.IsSidebarVisible ? ActivityBarSlot.Primary : null),
            nameof(ShellViewModel.IsSecondarySidebarVisible) => new(
                ApplySidebarVisibility: true,
                RecordPanelTrail: viewModel.IsSecondarySidebarVisible ? viewModel.SecondaryPanel : null,
                FocusSidebarSlot: viewModel.IsSecondarySidebarVisible ? ActivityBarSlot.Secondary : null),
            nameof(ShellViewModel.IsSettingsOverlayOpen) => new(ApplySettingsWindowState: true),
            nameof(ShellViewModel.SettingsCategory) when viewModel.IsSettingsOverlayOpen =>
                new(ActivateSettingsWindow: true),
            nameof(ShellViewModel.ActivePanel) when !viewModel.IsPanelChangeAutomatic => new(
                RecordPanelTrail: viewModel.ActivePanel,
                FocusSidebarSlot: ActivityBarSlot.Primary),
            nameof(ShellViewModel.SecondaryPanel) when !viewModel.IsPanelChangeAutomatic => new(
                RecordPanelTrail: viewModel.SecondaryPanel,
                FocusSidebarSlot: ActivityBarSlot.Secondary),
            _ => default,
        };
}
