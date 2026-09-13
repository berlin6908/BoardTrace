using System.Net.Http;
using System.Text.Json;
using System.Windows;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;

namespace BoardTrace.Station;

public partial class LoginWindow : Window
{
    private readonly HttpClient client;
    private readonly CancellationTokenSource cancellation = new();
    private bool closed;

    public LoginWindow(HttpClient client, string stationId)
    {
        InitializeComponent();
        this.client = client;
        StationLabel.Text = stationId + " · 工程回放登录";
        Loaded += (_, _) => UserNameInput.Focus();
    }

    public CurrentUser? AuthenticatedUser { get; private set; }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (!LoginButton.IsEnabled) return;
        if (string.IsNullOrWhiteSpace(UserNameInput.Text) || PasswordInput.Password.Length == 0)
        {
            Feedback.Text = "请输入人员账号和密码。";
            return;
        }
        var request = new LoginRequest(UserNameInput.Text.Trim(), PasswordInput.Password);
        PasswordInput.Clear();
        LoginButton.IsEnabled = false;
        Feedback.Text = "正在验证人员身份…";
        try
        {
            var user = await StationAuthentication.LoginOperatorAsync(client, request, cancellation.Token);
            if (!closed)
            {
                AuthenticatedUser = user;
                DialogResult = true;
            }
        }
        catch (UnauthorizedAccessException error) { Feedback.Text = error.Message; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            if (!closed) Feedback.Text = "无法完成中央登录，请检查服务连接后重试。";
        }
        finally
        {
            PasswordInput.Clear();
            LoginButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        cancellation.Cancel();
        PasswordInput.Clear();
        base.OnClosed(e);
    }
}
