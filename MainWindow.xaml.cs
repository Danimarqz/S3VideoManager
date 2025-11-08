using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using S3VideoManager.ViewModels;
using S3VideoManager.Views;

namespace S3VideoManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private ActivityLogWindow? _activityWindow;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        Loaded += MainWindow_Loaded;
        Unloaded += MainWindow_Unloaded;
        SetActivityButtonState(false);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(async () => await _viewModel.LoadSubjectsAsync(),
            DispatcherPriority.Background);
    }

    private async void NewSubjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsWorking)
        {
            return;
        }

        var dialog = new TextPromptWindow("Nueva materia", "Escribe el nombre de la materia")
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            await _viewModel.CreateSubjectAsync(dialog.InputText);
        }
    }

    private async void UploadClassButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSubject is null)
        {
            MessageBox.Show(this, "Selecciona una materia primero.", "Subir clase",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_viewModel.IsWorking)
        {
            return;
        }

        var fileDialog = new OpenFileDialog
        {
            Title = "Selecciona el video",
            Filter = "Videos|*.mp4;*.mov;*.mkv;*.avi;*.mpg;*.mpeg;*.wmv|Todos los archivos|*.*",
            CheckFileExists = true
        };

        if (fileDialog.ShowDialog() != true)
        {
            return;
        }

        var nameDialog = new TextPromptWindow("Nueva clase", "¿Cómo quieres llamar a la clase?", "Clase 1")
        {
            Owner = this
        };

        if (nameDialog.ShowDialog() != true)
        {
            return;
        }

        var className = nameDialog.InputText;
        var existing = _viewModel.Classes.FirstOrDefault(c =>
            c.Name.Equals(className, StringComparison.OrdinalIgnoreCase));

        var overwrite = false;
        if (existing is not null)
        {
            var confirm = MessageBox.Show(this,
                $"La clase '{className}' ya existe en '{_viewModel.SelectedSubject?.Name}'. ¿Quieres sobreescribirla?",
                "Confirmar sobreescritura",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            overwrite = true;
        }

        await _viewModel.UploadClassAsync(fileDialog.FileName, className, overwrite);
    }

    private async void DeleteClassButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedClass is null)
        {
            MessageBox.Show(this, "Selecciona la clase que quieres eliminar.", "Eliminar clase",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_viewModel.IsWorking)
        {
            return;
        }

        var confirm = MessageBox.Show(this,
            $"¿Seguro que quieres eliminar '{_viewModel.SelectedClass.Name}'?",
            "Eliminar clase",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await _viewModel.DeleteSelectedClassAsync();
    }

    private void OpenActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activityWindow is { IsVisible: true })
        {
            _activityWindow.Close();
            return;
        }

        _activityWindow = new ActivityLogWindow(_viewModel);
        _activityWindow.Closed += ActivityWindowOnClosed;
        PositionActivityWindow(_activityWindow);
        _activityWindow.Show();
        SetActivityButtonState(true);
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsWorking))
        {
            Mouse.OverrideCursor = _viewModel.IsWorking ? Cursors.Wait : null;
        }
    }

    private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        Mouse.OverrideCursor = null;
        if (_activityWindow is not null)
        {
            _activityWindow.Closed -= ActivityWindowOnClosed;
            _activityWindow.Close();
            _activityWindow = null;
        }

        SetActivityButtonState(false);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // ignored
            }
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void PositionActivityWindow(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        var hostWidth = ActualWidth > 0 ? ActualWidth : Width;
        var hostHeight = ActualHeight > 0 ? ActualHeight : Height;
        var hostLeft = double.IsNaN(Left) ? SystemParameters.WorkArea.Left : Left;
        var hostTop = double.IsNaN(Top) ? SystemParameters.WorkArea.Top : Top;

        var windowWidth = window.Width;
        if (double.IsNaN(windowWidth) || windowWidth <= 0)
        {
            windowWidth = window.ActualWidth > 0 ? window.ActualWidth : window.MinWidth;
        }

        if (double.IsNaN(windowWidth) || windowWidth <= 0)
        {
            windowWidth = 600;
        }

        var windowHeight = window.Height;
        if (double.IsNaN(windowHeight) || windowHeight <= 0)
        {
            windowHeight = window.ActualHeight > 0 ? window.ActualHeight : window.MinHeight;
        }

        if (double.IsNaN(windowHeight) || windowHeight <= 0)
        {
            windowHeight = 500;
        }

        var targetLeft = hostLeft + (hostWidth - windowWidth) / 2;
        var targetTop = hostTop + (hostHeight - windowHeight) / 2;

        var leftBound = SystemParameters.WorkArea.Left;
        var topBound = SystemParameters.WorkArea.Top;
        var rightBound = SystemParameters.WorkArea.Right - windowWidth;
        var bottomBound = SystemParameters.WorkArea.Bottom - windowHeight;

        window.Left = Math.Max(Math.Min(targetLeft, rightBound), leftBound);
        window.Top = Math.Max(Math.Min(targetTop, bottomBound), topBound);
    }

    private void SetActivityButtonState(bool isActive)
    {
        if (ActivityButton is null)
        {
            return;
        }

        var brushKey = isActive ? "AccentBrushLight" : "AccentBrush";
        if (TryFindResource(brushKey) is Brush brush)
        {
            ActivityButton.Background = brush;
        }
    }

    private void ActivityWindowOnClosed(object? sender, EventArgs e)
    {
        if (_activityWindow is not null)
        {
            _activityWindow.Closed -= ActivityWindowOnClosed;
            _activityWindow = null;
        }

        SetActivityButtonState(false);
    }
}
