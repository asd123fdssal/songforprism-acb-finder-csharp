using System.IO;
using AcbFinder.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcbFinder.App;

public partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 50;
    private readonly List<string> _logLines = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecryptCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractAcbCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractAwbCommand))]
    [NotifyCanExecuteChangedFor(nameof(CategorizeWavCommand))]
    private bool isBusy;

    [ObservableProperty] private string gameFolder = "";
    [ObservableProperty] private string logText = "";
    [ObservableProperty] private int progressValue;
    [ObservableProperty] private int progressMax = 1;

    public MainViewModel()
    {
        var defaultGameFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "BNE", "imasscprism", "D");
        if (Directory.Exists(defaultGameFolder))
        {
            GameFolder = defaultGameFolder;
            AppendLog($"Game folder set automatically: {defaultGameFolder}");
        }
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select Game Folder" };
        if (dialog.ShowDialog() == true)
            GameFolder = dialog.FolderName;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task DecryptAsync()
    {
        if (string.IsNullOrWhiteSpace(GameFolder) || !Directory.Exists(GameFolder))
        {
            AppendLog("The game folder is not set or does not exist.");
            return Task.CompletedTask;
        }

        var originDir = DecryptService.GetDefaultOriginDir();
        return RunServiceAsync("Decrypt",
            (progress, log, ct) => DecryptService.RunAsync(GameFolder, originDir, progress, log, ct));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ExtractAcbAsync()
    {
        var originDir = DecryptService.GetDefaultOriginDir();
        return RunServiceAsync("Extract ACB",
            (progress, log, ct) => AcbExtractService.RunAsync(originDir, progress, log, ct));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ExtractAwbAsync()
    {
        var originDir = DecryptService.GetDefaultOriginDir();
        return RunServiceAsync("Extract AWB",
            (progress, log, ct) => AwbExtractService.RunAsync(originDir, progress, log, ct));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task CategorizeWavAsync()
    {
        var originDir = DecryptService.GetDefaultOriginDir();
        return RunServiceAsync("Categorize WAV",
            (progress, log, ct) => CategorizeService.RunAsync(originDir, progress, log, ct));
    }

    private async Task RunServiceAsync(string name, Func<IProgress<(int done, int total)>, Action<string>, CancellationToken, Task> runService)
    {
        IsBusy = true;
        try
        {
            AppendLog($"{name} started.");

            // Progress<T> captures the current SynchronizationContext at construction time
            // (we're still on the UI thread here), so both callbacks marshal back safely
            // even though the service itself runs on a background thread.
            IProgress<(int done, int total)> progress =
                new Progress<(int done, int total)>(p => { ProgressValue = p.done; ProgressMax = p.total; });
            IProgress<string> logProgress = new Progress<string>(AppendLog);

            // Root-cause fix for dispatcher flooding: a service may report thousands of times
            // (e.g. one per parallel-processed file). Progress<T>.Report unconditionally posts
            // to the UI thread, so without this a big batch queues thousands of dispatcher
            // callbacks and the window appears frozen. Collapsing to one report per whole
            // percentage point (plus the final report) caps that at ~101 posts, regardless of
            // which service is calling — services stay unaware of this.
            var throttledProgress = new ThrottledProgress(progress);

            await Task.Run(() => runService(throttledProgress, line => logProgress.Report(line), CancellationToken.None));

            AppendLog($"{name} completed.");
        }
        catch (Exception ex)
        {
            AppendLog($"{name} error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AppendLog(string line)
    {
        _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        if (_logLines.Count > MaxLogLines)
            _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
        LogText = string.Join(Environment.NewLine, _logLines);
    }

    /// <summary>
    /// Forwards to <paramref name="inner"/> only when the integer percentage changes (or on
    /// the final done==total report), so a service calling Report thousands of times from
    /// parallel workers only triggers a handful of UI-thread dispatcher posts.
    /// </summary>
    private sealed class ThrottledProgress(IProgress<(int done, int total)> inner) : IProgress<(int done, int total)>
    {
        private int _lastPercent = -1;

        public void Report((int done, int total) value)
        {
            var percent = value.total == 0 ? 100 : value.done * 100 / value.total;
            if (value.done == value.total || Interlocked.Exchange(ref _lastPercent, percent) != percent)
                inner.Report(value);
        }
    }
}
