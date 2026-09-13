using System.Windows;
using System.IO;
using BoardTrace.Station.Core;

namespace BoardTrace.Station;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var options = StationOptions.Parse(e.Args);
            var operatorClient = StationAuthentication.CreateClient(options.ServerUrl);
            var login = new LoginWindow(operatorClient, options.StationId);
            if (login.ShowDialog() != true)
            {
                operatorClient.Dispose();
                Shutdown();
                return;
            }
            var viewModel = new StationViewModel(options, login.AuthenticatedUser!, operatorClient);
            var window = new MainWindow { DataContext = viewModel };
            window.Closed += (_, _) => Shutdown();
            viewModel.LoginRequested += (_, _) =>
            {
                window.Hide();
                var nextClient = StationAuthentication.CreateClient(options.ServerUrl);
                var nextLogin = new LoginWindow(nextClient, options.StationId);
                if (nextLogin.ShowDialog() == true)
                {
                    viewModel.SignIn(nextLogin.AuthenticatedUser!, nextClient);
                    window.Show();
                }
                else
                {
                    nextClient.Dispose();
                    window.Close();
                }
            };
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

public sealed record StationOptions(string StationId, string DataRoot, string ManifestPath, string DatabasePath, Uri ServerUrl, string CredentialsPath)
{
    public static StationOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--station" or "--data-root" or "--manifest" or "--database" or "--server" or "--credentials"))
                throw new ArgumentException("支持的参数：--station、--data-root、--manifest、--database、--server、--credentials，各需一个值。");
            values.Add(args[index], args[index + 1]);
        }
        var station = values.GetValueOrDefault("--station", "STATION-01");
        var server = new Uri(values.GetValueOrDefault("--server", "http://127.0.0.1:5180").TrimEnd('/') + "/", UriKind.Absolute);
        if (server.Scheme is not ("http" or "https")) throw new ArgumentException("中央服务地址必须为 HTTP 或 HTTPS 地址。");
        return new StationOptions(station,
            Path.GetFullPath(values.GetValueOrDefault("--data-root", "data")),
            Path.GetFullPath(values.GetValueOrDefault("--manifest", "training/manifests/inputs/validation.jsonl")),
            Path.GetFullPath(values.GetValueOrDefault("--database", $"data/stations/{station}.db")), server,
            Path.GetFullPath(values.GetValueOrDefault("--credentials", $".local/stations/{station}.json")));
    }
}
