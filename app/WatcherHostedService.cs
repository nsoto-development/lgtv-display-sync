using Microsoft.Extensions.Hosting;

namespace LgtvDisplaySync.App;

// Runs the Win32 display watcher + GetMessage pump under the generic host / Windows service lifetime.
// HWND is created and pumped on this worker thread; SCM stop posts WM_QUIT to unblock GetMessage.
internal sealed class WatcherHostedService : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(
            () => Program.RunWatcherUntilQuit(consoleCancel: false, stoppingToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
}
