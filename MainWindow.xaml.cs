using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using S3VideoManager.ViewModels;
using S3VideoManager.Views;

namespace S3VideoManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        Loaded += MainWindow_Loaded;
        Unloaded += MainWindow_Unloaded;
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
    }
}
