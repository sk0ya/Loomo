namespace sk0ya.Loomo.App.ViewModels;

/// <summary>launchSettings.json が指定したブラウザ起動先をdotnet起動VMへ渡すための、C#側専用の窓口。</summary>
internal interface ILaunchBrowserTarget
{
    string LaunchBrowserUrl { get; set; }
}
