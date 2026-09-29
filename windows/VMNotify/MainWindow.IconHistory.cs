using System.IO;

namespace VMNotify;

public partial class MainWindow
{
    private readonly IconHistory iconHistory = new();
    private long savedIconRevision;
    private DateTime nextIconSave;
    private Task iconSaveTask = Task.CompletedTask;
    private string? activeIconScope;
    private string IconHistoryPath => Path.Combine(Path.GetDirectoryName(settingsFile)!, "tray-icon-history.json");

    private void CaptureIconHistory()
    {
        var scope = activeIconScope;
        if (scope == null) return;
        foreach (var app in receiver.AvailableApps)
            foreach (var icon in receiver.GetIcons(app.Id)) iconHistory.Record(scope, app.Id, icon);
        if (!previewMode && iconHistory.Revision != savedIconRevision && iconSaveTask.IsCompleted && DateTime.UtcNow >= nextIconSave)
            iconSaveTask = SaveIconHistory();
    }
    private async Task SaveIconHistory()
    {
        nextIconSave = DateTime.UtcNow.AddSeconds(3);
        var revision = iconHistory.Revision;
        var snapshot = iconHistory.Snapshot();
        try { await Task.Run(() => IconHistory.Save(IconHistoryPath, snapshot)); savedIconRevision = revision; }
        catch (Exception ex) { Diagnostics.Write("Icon history save failed: " + ex.Message); }
    }
    private RecordedIcon[] ReadIconHistory(string id, Func<IconObservation[]> readIcons)
    {
        var scope = IconHistory.Scope(saved);
        foreach (var icon in readIcons()) iconHistory.Record(scope, id, icon);
        return iconHistory.ForApp(scope, id);
    }
}
