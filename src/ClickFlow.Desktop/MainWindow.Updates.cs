using System.Diagnostics;
using System.Windows;
using ClickFlow.Core.Updates;

namespace ClickFlow.Desktop;

public partial class MainWindow
{
    private readonly GitHubUpdateService _updates = new();
    private CancellationTokenSource? _updateCancellation;
    private Task? _updateTask;
    private UpdateRelease? _availableUpdate;
    private string? _downloadedUpdate;
    private bool _updateBusy;
    private static Version CurrentVersion
    {
        get
        {
            var version = typeof(App).Assembly.GetName().Version!;
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    private void InitializeUpdateUi()
    {
        SidebarVersion.Text = $"NATIVE WINDOWS  /  v{CurrentVersion}";
        CurrentVersionText.Text = $"当前版本  {CurrentVersion} · Windows x64";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit() || _updateBusy || _closing) return;
        await RunUpdateOperationAsync(async token =>
        {
            _availableUpdate = null;
            _downloadedUpdate = null;
            UpdateNotes.Text = "";
            UpdateStatus.Text = "正在检查 GitHub 上的最新正式版本…";
            _availableUpdate = await _updates.CheckAsync(CurrentVersion, token);
            if (_availableUpdate is null)
                UpdateStatus.Text = $"当前已是最新正式版 · {CurrentVersion}";
            else
            {
                UpdateStatus.Text = $"发现新版本 {_availableUpdate.Version} · 下载约 {_availableUpdate.SizeBytes / 1048576d:F1} MB";
                UpdateNotes.Text = string.IsNullOrWhiteSpace(_availableUpdate.Notes)
                    ? "此版本未提供更新说明。" : _availableUpdate.Notes;
            }
            Log(UpdateStatus.Text);
        });
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit() || _updateBusy || _closing || _availableUpdate is null) return;
        var release = _availableUpdate;
        await RunUpdateOperationAsync(async token =>
        {
            UpdateStatus.Text = "正在获取并校验新版软件…";
            UpdateProgress.Value = 0;
            UpdateProgress.Visibility = Visibility.Visible;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (_closing || !_updateBusy || token.IsCancellationRequested) return;
                UpdateProgress.Value = p.Percentage;
                UpdateStatus.Text = $"正在下载 {release.Version} · {p.Percentage:F0}% · {p.BytesReceived / 1048576d:F1} / {p.TotalBytes / 1048576d:F1} MB";
            });
            var directory = Path.Combine(App.DataDirectory, "Updates", release.Version.ToString());
            _downloadedUpdate = await _updates.DownloadAsync(release, directory, progress, token);
            UpdateStatus.Text = $"{release.Version} 下载完成，SHA-256 校验通过。";
            UpdateNotes.Text = "点击「打开下载位置」，退出 ClickFlow 后，将下载的 EXE 改名为 ClickFlow.exe 并替换原文件。工作流配置会保留。\n\n" + _downloadedUpdate;
            Log(UpdateStatus.Text + " 文件：" + _downloadedUpdate);
        });
    }

    private async Task RunUpdateOperationAsync(Func<CancellationToken, Task> operation)
    {
        _updateBusy = true;
        using var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        UpdateUpdateButtons();
        UpdateButtons();
        try
        {
            _updateTask = operation(cancellation.Token);
            await _updateTask;
        }
        catch (OperationCanceledException)
        {
            if (!_closing) UpdateStatus.Text = cancellation.IsCancellationRequested ? "已取消。可重新检查或下载。" : "连接超时，请检查网络后重试。";
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                UpdateStatus.Text = "更新未完成：" + ex.Message;
                Log(UpdateStatus.Text);
            }
        }
        finally
        {
            _updateTask = null;
            _updateCancellation = null;
            _updateBusy = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateUpdateButtons();
            UpdateButtons();
        }
    }

    private void UpdateUpdateButtons()
    {
        CheckUpdateButton.IsEnabled = !_updateBusy;
        DownloadUpdateButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadUpdateButton.IsEnabled = !_updateBusy && _downloadedUpdate is null;
        CancelUpdateButton.Visibility = _updateBusy ? Visibility.Visible : Visibility.Collapsed;
        OpenUpdateFolderButton.Visibility = _downloadedUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        ReleasePageButton.IsEnabled = !_updateBusy;
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();
    private void ReleasePage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit() || _updateBusy) return;
        OpenUpdateLocation(_availableUpdate?.ReleasePage.AbsoluteUri ?? GitHubUpdateService.ReleasesUrl);
    }
    private void OpenUpdateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit() || _updateBusy || _downloadedUpdate is null) return;
        OpenUpdateLocation(Path.GetDirectoryName(_downloadedUpdate)!);
    }
    private void OpenUpdateLocation(string location)
    {
        try { Process.Start(new ProcessStartInfo(location) { UseShellExecute = true }); }
        catch (Exception ex) { UpdateStatus.Text = "无法打开：" + ex.Message; }
    }
}
