using System.Net.Http;
using System.Text.Json;
using System.Windows;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;

namespace BoardTrace.Station;

public partial class LoginWindow : Window
{
    private readonly StationOptions options;
    private readonly CancellationTokenSource cancellation = new();
    private bool closed;
    private bool busy;
    private ResumeCandidate? candidate;

    public LoginWindow(StationPersonnelSession personnel, StationOptions options)
    {
        InitializeComponent();
        this.options = options;
        PersonnelSession = personnel;
        StationLabel.Text = options.StationId + " · 操作员登录";
        Loaded += async (_, _) => { UserNameInput.Focus(); await LoadResumeCandidateAsync(); };
    }

    public CurrentUser? AuthenticatedUser { get; private set; }
    public StationPersonnelSession? PersonnelSession { get; private set; }
    public bool ResumeBatch { get; private set; }

    private void SetBusy(bool value)
    {
        busy = value;
        LoginButton.IsEnabled = !value;
        ResumeButton.IsEnabled = !value && candidate is not null;
        RecoveryButton.IsEnabled = !value;
    }

    private async Task LoadResumeCandidateAsync()
    {
        try
        {
            candidate = await Task.Run(() => OfflineBatchResume.Read(options));
            if (!closed) ResumeNotice.Text = candidate is null ? "当前没有可恢复的本班批次。"
                : $"{candidate.Envelope.Operator.DisplayName} · {candidate.Batch.Batch.BatchNumber}\n原授权至 {candidate.Envelope.Session.ExpiresAt.LocalDateTime:MM-dd HH:mm}，仅继续此批次。";
        }
        catch (Exception error)
        {
            candidate = null;
            if (!closed) ResumeNotice.Text = "本班恢复不可用：" + error.Message;
        }
        if (!closed) ResumeButton.IsEnabled = !busy && candidate is not null;
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (string.IsNullOrWhiteSpace(UserNameInput.Text) || PasswordInput.Password.Length == 0)
        {
            Feedback.Text = "请输入人员账号和密码。";
            return;
        }
        var request = new LoginRequest(UserNameInput.Text.Trim(), PasswordInput.Password);
        PasswordInput.Clear();
        SetBusy(true);
        Feedback.Text = "正在验证人员身份…";
        try
        {
            var user = await StationAuthentication.LoginOperatorAsync(PersonnelSession!.Client, request, cancellation.Token);
            if (!closed)
            {
                AuthenticatedUser = user;
                ResumeBatch = false;
                DialogResult = true;
            }
        }
        catch (UnauthorizedAccessException error) { if (!closed) Feedback.Text = error.Message; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            if (!closed) Feedback.Text = "无法完成中央登录，请检查服务连接后重试。";
        }
        finally
        {
            PasswordInput.Clear();
            if (!closed) SetBusy(false);
        }
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        StationPersonnelSession? restored = null;
        try
        {
            var current = await Task.Run(() => OfflineBatchResume.Read(options))
                ?? throw new InvalidOperationException("原批次恢复包已失效，请在线登录。");
            // A missing fixed recipe prevents new production, never substitutes a detector.
            await Task.Run(() => new LocalRecipeStore(options.DatabasePath).Load(current.Batch.Batch.RecipeVersionId));
            restored = OfflineBatchResume.RestorePersonnel(options, current);
            Feedback.Text = "正在核对原人员会话；中央断线时沿原授权继续。";
            try
            {
                var user = await StationAuthentication.CurrentOperatorAsync(restored.Client, cancellation.Token);
                if (user.Id != current.Envelope.Operator.Id)
                    throw new UnauthorizedAccessException("中央返回的人员与原批次操作员不同。");
            }
            catch (Exception error) when (!closed && !cancellation.IsCancellationRequested && OfflineBatchResume.IsUnavailable(error)) { }
            if (closed) return;
            if (current.Envelope.Session.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("原人员授权已到期，请在线登录。");
            PersonnelSession?.Dispose();
            PersonnelSession = restored;
            restored = null;
            AuthenticatedUser = current.Envelope.Operator;
            ResumeBatch = true;
            DialogResult = true;
        }
        catch (UnauthorizedAccessException error)
        {
            candidate = null;
            try
            {
                new LocalInspectionStore(options.DatabasePath).ClearExecutionSession();
                Feedback.Text = error.Message + " 原本班恢复授权已清除。";
            }
            catch (Exception storageError) { Feedback.Text = "本地授权清除未完成，请检查存储后在线重新登录。" + storageError.Message; }
        }
        catch (Exception error)
        {
            if (!closed) Feedback.Text = "不能恢复本班：" + error.Message;
        }
        finally
        {
            restored?.Dispose();
            if (!closed) SetBusy(false);
        }
    }

    private void RecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        PersonnelSession?.Dispose();
        PersonnelSession = null;
        AuthenticatedUser = null;
        ResumeBatch = false;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        cancellation.Cancel();
        PasswordInput.Clear();
        base.OnClosed(e);
    }
}
