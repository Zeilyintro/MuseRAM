using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MuseRAM.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\MuseRAM.9F099702-2E27-47BE-93A8-B538DC2E57F4";
    private const string SingleInstanceActivationName = @"Local\MuseRAM.Activate.9F099702-2E27-47BE-93A8-B538DC2E57F4";
    private SingleInstanceGuard? _singleInstance;
    private SingleInstanceActivation? _singleInstanceActivation;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDataPaths.MigrateLegacyAuxiliaryFiles();
        if (UpdateCompletionService.IsRequested(e.Args))
        {
            try
            {
                if (!await UpdateCompletionService.TryCompleteAsync(e.Args))
                    throw new InvalidOperationException("更新启动参数无效。");
                Shutdown();
                return;
            }
            catch (Exception exception)
            {
                var diagnosticEnabled = new LocalSettingsStore().Load().DiagnosticDataCollectionEnabled;
                new DiagnosticLog(isEnabled: () => diagnosticEnabled)
                    .Error("Update replacement failed.", exception);
                StartupThemedDialog.Show(
                    "MuseRAM 更新失败",
                    exception.Message,
                    "确定",
                    SystemThemeService.IsLightTheme(),
                    error: true);
                Shutdown(1);
                return;
            }
        }

        _singleInstance = new SingleInstanceGuard(SingleInstanceMutexName);
        using var currentProcess = Process.GetCurrentProcess();
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? currentProcess.ProcessName;
        var currentProcessStartedAtUtc = currentProcess.StartTime.ToUniversalTime();
        if (!_singleInstance.IsPrimary || SingleInstanceGuard.HasOlderProcess(
                processName,
                Environment.ProcessId,
                currentProcessStartedAtUtc))
        {
            var activationSignaled = SingleInstanceActivation.TrySignal(SingleInstanceActivationName);
            var settings = new LocalSettingsStore().Load();
            if (settings.DiagnosticDataCollectionEnabled)
            {
                new DiagnosticLog().Info(
                    $"[DEBUG-MYDOCK] duplicate-instance-startup; ActivationSignaled={activationSignaled}");
            }
            if (activationSignaled)
            {
                Shutdown();
                return;
            }
            var language = UiLanguageCatalog.FromCode(settings.LanguageCode);
            var text = UiTextCatalog.For(language);
            StartupThemedDialog.Show(
                text["AlreadyRunningTitle"],
                text["AlreadyRunningMessage"],
                text["DialogOk"],
                SystemThemeService.IsLightTheme());
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _singleInstanceActivation = new SingleInstanceActivation(
            SingleInstanceActivationName,
            () => Dispatcher.BeginInvoke(mainWindow.RestoreFromExternalActivation));
        if (mainWindow.StartsHidden)
        {
            await mainWindow.InitializeAsync();
        }
        else
        {
            mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceActivation?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
