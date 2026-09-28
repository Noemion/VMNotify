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
    private string AppArchitecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    private void InitializeAbout(string build)
    {
        appVersion = build.Split('+')[0];
        AboutVersion.Text = $"版本 {appVersion} · {AppArchitecture}";
        AboutBuild.Text = "构建 " + (build.Contains('+') ? build.Split('+')[1] : appVersion);
        updates = new Updates(updateClient, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMNotify", "Updates"));
        if (!previewMode) RefreshDownloads();
    }
    private void RefreshDownloads()
    {
        try
        {
            var items = updates.Downloads();
            DownloadedPackages.ItemsSource = items;
            NoDownloads.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { UpdateStatus.Text = "无法读取下载记录：" + ex.Message; }
    }
    private void AboutClicked(object sender, RoutedEventArgs e) => SelectPage(3);
    private void RepositoryClicked(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Updates.RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception ex) { UpdateStatus.Text = "无法打开仓库：" + ex.Message; }
    }
    private async Task UpdateOperation(Func<Task> action)
    {
        if (updateWorking) return;
        updateWorking = true;
        CheckUpdateButton.IsEnabled = DownloadUpdateButton.IsEnabled = DownloadedPackages.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { UpdateStatus.Text = "请求超时或已取消，请重试。"; }
        catch (Exception ex) { UpdateStatus.Text = ex.Message; }
        finally
        {
            updateWorking = false;
            CheckUpdateButton.IsEnabled = DownloadedPackages.IsEnabled = true;
            DownloadUpdateButton.IsEnabled = availableUpdate != null;
            UpdateProgress.Visibility = Visibility.Collapsed;
            RefreshDownloads();
        }
    }
    private async void CheckUpdateClicked(object sender, RoutedEventArgs e) => await UpdateOperation(async () =>
    {
        availableUpdate = null;
        UpdateStatus.Text = "正在检查 GitHub 发布版本…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        availableUpdate = await updates.Check(appVersion, AppArchitecture, timeout.Token);
        UpdateStatus.Text = availableUpdate == null ? $"当前已是最新版本（{appVersion}）。" : $"发现新版本 {availableUpdate.Version}，可下载 {AppArchitecture} 安装包。";
    });
    private async void DownloadUpdateClicked(object sender, RoutedEventArgs e) => await UpdateOperation(async () =>
    {
        if (availableUpdate == null) return;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        var release = availableUpdate;
        var progress = new Progress<int>(value => { UpdateProgress.Value = value; UpdateStatus.Text = $"正在下载 {release.Version}：{value}%"; });
        await updates.Download(release, progress, updateLifetime.Token);
        UpdateStatus.Text = $"版本 {release.Version} 已下载并通过校验，可点击下方“安装”。";
    });
    private async void InstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DownloadedUpdate item }) return;
        await UpdateOperation(async () =>
        {
            if (Updates.ParseVersion(item.Version) <= Updates.ParseVersion(appVersion))
                throw new InvalidOperationException("此安装包不高于当前版本，可删除旧包或前往 GitHub 查看。");
            string path = await updates.Verify(item, updateLifetime.Token);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            await Stop();
            quitting = true;
            Close();
        });
    }
    private async void DeleteUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DownloadedUpdate item }) return;
        await UpdateOperation(() =>
        {
            updates.Delete(item);
            UpdateStatus.Text = "已删除下载的安装包。";
            return Task.CompletedTask;
        });
    }
}
