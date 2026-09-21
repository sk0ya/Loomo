using System.Windows;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct WorkflowProgressLayout(
    GridLength ProgressAreaHeight,
    bool ShouldUpdateProgressAreaHeight,
    double ProgressAreaMinHeight,
    double ProgressLogMinHeight,
    GridLength ProgressLogHeight,
    GridLength FinalOutputHeight);

/// <summary>ワークフローの状態から進捗エリアのレイアウト値を決める。</summary>
internal static class WorkflowProgressLayoutPolicy
{
    public static WorkflowProgressLayout Resolve(
        bool hasFinalOutput,
        bool isProgressVisible,
        bool isWarmingUp,
        bool showProgressDetails,
        GridLength currentProgressAreaHeight)
    {
        // 表示中の Star はユーザーがドラッグした高さを保持し、非表示・ウォームアップへの切替時だけ更新する。
        var targetOuterHeight = !isProgressVisible
            ? new GridLength(0)
            : isWarmingUp && !hasFinalOutput
                ? GridLength.Auto
                : new GridLength(1.4, GridUnitType.Star);
        var shouldUpdateProgressAreaHeight = currentProgressAreaHeight.GridUnitType != targetOuterHeight.GridUnitType
            || (targetOuterHeight.GridUnitType != GridUnitType.Star
                && currentProgressAreaHeight.Value != targetOuterHeight.Value);

        return new WorkflowProgressLayout(
            targetOuterHeight,
            shouldUpdateProgressAreaHeight,
            isProgressVisible ? 96 : 0,
            showProgressDetails ? 72 : 38,
            showProgressDetails ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            hasFinalOutput
                ? new GridLength(showProgressDetails ? 0.7 : 1.0, GridUnitType.Star)
                : new GridLength(0));
    }
}
