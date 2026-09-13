using System.Windows;
using System.IO;

namespace BoardTrace.Station;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = StationOptions.Parse(e.Args);
            var viewModel = new StationViewModel(options);
            var window = new MainWindow { DataContext = viewModel };
            window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width - 32);
            window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height - 32);
            MainWindow = window;
            window.Show();
            await viewModel.InitializeAsync();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "BoardTrace 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

public sealed record StationOptions(string StationId, string DataRoot, string ManifestPath, string DatabasePath)
{
    public static StationOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--station" or "--data-root" or "--manifest" or "--database"))
                throw new ArgumentException("支持的参数：--station、--data-root、--manifest、--database，各需一个值。");
            values.Add(args[index], args[index + 1]);
        }
        var station = values.GetValueOrDefault("--station", "STATION-01");
        return new StationOptions(station,
            Path.GetFullPath(values.GetValueOrDefault("--data-root", "data")),
            Path.GetFullPath(values.GetValueOrDefault("--manifest", "training/manifests/inputs/validation.jsonl")),
            Path.GetFullPath(values.GetValueOrDefault("--database", $"data/stations/{station}.db")));
    }
}
