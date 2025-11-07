using System;
using System.Windows;
using S3VideoManager.Helpers;
using S3VideoManager.Services;
using S3VideoManager.ViewModels;

namespace S3VideoManager;

public partial class App : Application
{
    private S3Service? _s3Service;
    private FfmpegService? _ffmpegService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettingsLoader.GetAppSettings(AppContext.BaseDirectory);
        _ffmpegService = new FfmpegService(settings.Transcode);
        _s3Service = new S3Service(settings.Aws);

        var mainViewModel = new MainViewModel(_s3Service, _ffmpegService);
        var window = new MainWindow(mainViewModel);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        _s3Service?.Dispose();
        _ffmpegService = null;
    }
}
