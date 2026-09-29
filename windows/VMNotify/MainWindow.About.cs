using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;

namespace VMNotify;

public partial class MainWindow
{
    private readonly HttpClient updateClient = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly CancellationTokenSource updateLifetime = new();
    private Updates updates = null!;
    private ReleaseUpdate? availableUpdate;
    private string appVersion = "";
    private bool updateWorking;
    private Task startupUpdateCleanup = Task.CompletedTask;
    private string AppArchitecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    private void InitializeAbout(string build)
    {
        appVersion = build.Split('+')[0];
        AboutVersion.Text = $"版本 {appVersion} · {AppArchitecture}";
        AboutBuild.Text = "构建 " + (build.Contains('+') ? build.Split('+')[1] : appVersion);
        updates = new Updates(updateClient, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMNotify", "Updates"));
        if (!previewMode) startupUpdateCleanup = CleanupPreviousDownloads();
    }
    private async Task CleanupPreviousDownloads()
    {
        try
        {
            // The installer can still be exiting when it launches the updated app.
            for (int attempt = 0; attempt < 4; attempt++) {
                var result = await Task.Run(updates.DeleteAll, updateLifetime.Token);
                if (result.Failed == 0) return;
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2), updateLifetime.Token);
            }
            Diagnostics.Write("Update cleanup: some cached files remain in use; retry on next startup.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Write("Update cleanup failed: " + ex.Message); }
    }
    private void AboutClicked(object sender, RoutedEventArgs e) => SelectPage(3);
    private async Task PreviewUpdateCleanup(string directory)
    {
        var original = updates;
        var folder = Path.Combine(directory, "startup-cleanup-test");
        Directory.CreateDirectory(folder);
        var installer = Path.Combine(folder, "VMNotify-0.1.0-win-x64-setup.exe");
        await File.WriteAllTextAsync(installer, "test installer");
        await File.WriteAllTextAsync(installer + ".json", "{}");
        await File.WriteAllTextAsync(installer + ".partial", "incomplete");
        var preserved = Path.Combine(folder, "settings.json");
        await File.WriteAllTextAsync(preserved, "preserve");
        updates = new Updates(updateClient, folder);
        try {
            var locked = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.None);
            try {
                startupUpdateCleanup = CleanupPreviousDownloads();
                bool actionRan = false;
                var operation = UpdateOperation(() => {
                    if (File.Exists(installer) || File.Exists(installer + ".json") || File.Exists(installer + ".partial"))
                        throw new InvalidOperationException("Update started before previous installer cleanup completed");
                    actionRan = true;
                    return Task.CompletedTask;
                });
                await Task.Delay(300);
                if (actionRan) throw new InvalidOperationException("Cleanup must serialize with a new download");
                locked.Dispose();
                await operation;
                if (!actionRan || !File.Exists(preserved)) throw new InvalidOperationException("Startup cleanup/retry failed or removed unrelated file");
            } finally { locked.Dispose(); }
        } finally { updates = original; startupUpdateCleanup = Task.CompletedTask; }
        File.WriteAllText(Path.Combine(directory, "update-checks.txt"), "PASS: startup deletes cached installer, metadata and partial files; locked installer retried; settings preserved; update waits for cleanup.");
    }
    private void RepositoryClicked(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Updates.RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception ex) { UpdateStatus.Text = "无法打开仓库：" + ex.Message; }
    }
    private async Task UpdateOperation(Func<Task> action)
    {
        if (updateWorking) return;
        updateWorking = true;
        CheckUpdateButton.IsEnabled = DownloadUpdateButton.IsEnabled = false;
        try { await startupUpdateCleanup; updateLifetime.Token.ThrowIfCancellationRequested(); await action(); }
        catch (OperationCanceledException) { UpdateStatus.Text = "请求超时或已取消，请重试。"; }
        catch (Exception ex) { UpdateStatus.Text = ex.Message; }
        finally
        {
            updateWorking = false;
            CheckUpdateButton.IsEnabled = true;
            DownloadUpdateButton.IsEnabled = availableUpdate != null;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }
    private async void CheckUpdateClicked(object sender, RoutedEventArgs e) => await UpdateOperation(async () =>
    {
        availableUpdate = null;
        UpdateStatus.Text = "正在检查 GitHub 发布版本…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        availableUpdate = await updates.Check(appVersion, AppArchitecture, timeout.Token);
        UpdateStatus.Text = availableUpdate == null ? $"当前已是最新版本（{appVersion}）。" : $"发现新版本 {availableUpdate.Version}，点击“下载并升级”开始更新。";
    });
    private async void DownloadUpdateClicked(object sender, RoutedEventArgs e) => await UpdateOperation(async () =>
    {
        if (availableUpdate == null) return;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        var release = availableUpdate;
        var progress = new Progress<int>(value => { UpdateProgress.Value = value; UpdateStatus.Text = $"正在下载 {release.Version}：{value}%"; });
        var item = await updates.Download(release, progress, updateLifetime.Token);
        UpdateStatus.Text = "正在校验安装包…";
        string path = await updates.Verify(item, updateLifetime.Token);
        updateLifetime.Token.ThrowIfCancellationRequested();
        UpdateStatus.Text = "正在启动升级向导…";
        using var installer = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })
            ?? throw new IOException("无法启动升级向导，请重试。");
        quitting = true;
        await Stop();
        Close();
    });
}
