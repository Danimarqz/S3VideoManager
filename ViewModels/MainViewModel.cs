using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S3VideoManager.Models;
using S3VideoManager.Services;

namespace S3VideoManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly S3Service _s3Service;
    private readonly FfmpegService _ffmpegService;
    private string? _pendingSubjectName;

    public ObservableCollection<SubjectModel> Subjects { get; } = new();
    public ObservableCollection<ClassModel> Classes { get; } = new();
    public ObservableCollection<string> ActivityLog { get; } = new();

    [ObservableProperty]
    private SubjectModel? _selectedSubject;

    [ObservableProperty]
    private ClassModel? _selectedClass;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _statusMessage = "Listo";

    [ObservableProperty]
    private double _operationProgress;

    public IAsyncRelayCommand RefreshSubjectsCommand { get; }
    public IAsyncRelayCommand RefreshClassesCommand { get; }
    public IAsyncRelayCommand DeleteClassCommand { get; }

    public bool IsWorking => IsBusy || IsRefreshing;

    public MainViewModel(S3Service s3Service, FfmpegService ffmpegService)
    {
        _s3Service = s3Service ?? throw new ArgumentNullException(nameof(s3Service));
        _ffmpegService = ffmpegService ?? throw new ArgumentNullException(nameof(ffmpegService));

        RefreshSubjectsCommand = new AsyncRelayCommand(LoadSubjectsAsync, () => !IsBusy && !IsRefreshing);
        RefreshClassesCommand = new AsyncRelayCommand(LoadClassesAsync, () => !IsBusy && !IsRefreshing && SelectedSubject is not null);
        DeleteClassCommand = new AsyncRelayCommand(DeleteSelectedClassAsync, () => !IsBusy && SelectedClass is not null);
    }

    public async Task LoadSubjectsAsync()
    {
        if (IsBusy || IsRefreshing)
        {
            return;
        }

        try
        {
            IsRefreshing = true;
            StatusMessage = "Cargando materias...";
            Subjects.Clear();
            Classes.Clear();
            SelectedSubject = null;
            var subjects = await Task.Run(async () => await _s3Service.GetSubjectsAsync()).ConfigureAwait(true);
            foreach (var subjectName in subjects)
            {
                Subjects.Add(new SubjectModel(subjectName));
            }

            StatusMessage = $"Se cargaron {Subjects.Count} materias.";
            AddLog("Materias actualizadas.");

            if (Subjects.Count > 0)
            {
                SelectedSubject = Subjects[0];
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al cargar materias: {ex.Message}";
            AddLog($"Error materias: {ex}");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task LoadClassesAsync()
    {
        if (SelectedSubject is null)
        {
            Classes.Clear();
            return;
        }

        if (IsBusy || IsRefreshing)
        {
            _pendingSubjectName = SelectedSubject.Name;
            return;
        }

        var currentSubject = SelectedSubject;
        try
        {
            IsRefreshing = true;
            StatusMessage = $"Cargando clases de {currentSubject.Name}...";
            Classes.Clear();

            var classNames = await Task.Run(async () => await _s3Service.GetClassesAsync(currentSubject.Name)).ConfigureAwait(true);
            foreach (var className in classNames)
            {
                var prefix = $"{currentSubject.Name}/{className}".Replace("//", "/");
                Classes.Add(new ClassModel(className, $"{prefix}/"));
            }

            SelectedClass = Classes.FirstOrDefault();
            StatusMessage = $"Clases actualizadas para {currentSubject.Name}.";
            AddLog($"Clases de {currentSubject.Name} actualizadas ({Classes.Count}).");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al cargar clases: {ex.Message}";
            AddLog($"Error clases: {ex}");
        }
        finally
        {
            IsRefreshing = false;
            if (_pendingSubjectName is not null)
            {
                var pending = _pendingSubjectName;
                _pendingSubjectName = null;
                if (SelectedSubject?.Name == pending)
                {
                    _ = LoadClassesAsync();
                }
            }
        }
    }

    public async Task<bool> CreateSubjectAsync(string subjectName)
    {
        if (string.IsNullOrWhiteSpace(subjectName))
        {
            StatusMessage = "El nombre de la materia no puede estar vacío.";
            return false;
        }

        subjectName = subjectName.Trim();
        if (subjectName.Contains('/') || subjectName.Contains('\\'))
        {
            StatusMessage = "El nombre de la materia no puede contener barras.";
            return false;
        }

        if (Subjects.Any(s => string.Equals(s.Name, subjectName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "Ya existe una materia con ese nombre.";
            return false;
        }

        if (IsBusy)
        {
            StatusMessage = "Espera a que termine la operación actual.";
            return false;
        }

        try
        {
            IsBusy = true;
            StatusMessage = $"Creando materia '{subjectName}'...";
            await _s3Service.CreateSubjectAsync(subjectName).ConfigureAwait(true);
            InsertSubjectSorted(new SubjectModel(subjectName));
            SelectedSubject = Subjects.FirstOrDefault(s => s.Name.Equals(subjectName, StringComparison.OrdinalIgnoreCase));
            StatusMessage = $"Materia '{subjectName}' creada.";
            AddLog($"Materia '{subjectName}' creada.");
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al crear materia: {ex.Message}";
            AddLog($"Error creando materia: {ex}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> UploadClassAsync(string videoFilePath, string className, bool overwriteExisting = false)
    {
        if (SelectedSubject is null)
        {
            StatusMessage = "Selecciona una materia primero.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(videoFilePath) || !File.Exists(videoFilePath))
        {
            StatusMessage = "El archivo de video no existe.";
            return false;
        }

        className = SanitizeName(className);
        if (string.IsNullOrWhiteSpace(className))
        {
            StatusMessage = "El nombre de la clase no puede estar vacío.";
            return false;
        }

        var existingClass = Classes.FirstOrDefault(c => c.Name.Equals(className, StringComparison.OrdinalIgnoreCase));
        var shouldOverwrite = existingClass is not null;

        if (shouldOverwrite && !overwriteExisting)
        {
            StatusMessage = "Ya existe una clase con ese nombre en la materia seleccionada.";
            return false;
        }

        if (IsBusy)
        {
            StatusMessage = "Ya hay una operación en curso.";
            return false;
        }

        string? workDirectory = null;
        try
        {
            IsBusy = true;
            OperationProgress = 0;
            StatusMessage = $"Transcodificando '{className}'...";

            var logProgress = new Progress<string>(AddLog);
            var encodeProgress = new Progress<double>(value => OperationProgress = Math.Clamp(value * 0.5, 0, 0.5));
            workDirectory = await _ffmpegService.TranscodeToHlsAsync(videoFilePath, null, logProgress, encodeProgress)
                .ConfigureAwait(true);

            if (shouldOverwrite)
            {
                StatusMessage = $"Eliminando versión anterior de '{className}'...";
                await _s3Service.DeleteClassAsync(SelectedSubject.Name, className).ConfigureAwait(true);
                AddLog($"Clase '{className}' eliminada antes de la nueva subida.");
            }

            StatusMessage = $"Subiendo '{className}' a S3...";
            var uploadProgress = new Progress<double>(value => OperationProgress = 0.5 + Math.Clamp(value, 0, 1) * 0.5);
            await _s3Service.UploadClassAsync(SelectedSubject.Name, className, workDirectory, uploadProgress)
                .ConfigureAwait(true);

            StatusMessage = $"Clase '{className}' subida correctamente.";
            AddLog($"Clase '{className}' subida a {SelectedSubject.Name}.");

            await LoadClassesAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al subir la clase: {ex.Message}";
            AddLog($"Error subida: {ex}");
            return false;
        }
        finally
        {
            OperationProgress = 0;
            IsBusy = false;

            if (!string.IsNullOrWhiteSpace(workDirectory) && Directory.Exists(workDirectory))
            {
                try
                {
                    Directory.Delete(workDirectory, true);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    public async Task<bool> DeleteSelectedClassAsync()
    {
        if (SelectedSubject is null || SelectedClass is null)
        {
            StatusMessage = "Selecciona una clase para eliminar.";
            return false;
        }

        if (IsBusy)
        {
            StatusMessage = "Espera a que termine la operación actual.";
            return false;
        }

        try
        {
            IsBusy = true;
            StatusMessage = $"Eliminando '{SelectedClass.Name}'...";
            await _s3Service.DeleteClassAsync(SelectedSubject.Name, SelectedClass.Name).ConfigureAwait(true);
            AddLog($"Clase '{SelectedClass.Name}' eliminada de {SelectedSubject.Name}.");
            await LoadClassesAsync().ConfigureAwait(true);
            StatusMessage = "Clase eliminada.";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al eliminar: {ex.Message}";
            AddLog($"Error al eliminar: {ex}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InsertSubjectSorted(SubjectModel model)
    {
        var index = 0;
        while (index < Subjects.Count &&
               string.Compare(Subjects[index].Name, model.Name, StringComparison.OrdinalIgnoreCase) < 0)
        {
            index++;
        }

        Subjects.Insert(index, model);
    }

    private static string SanitizeName(string value)
    {
        var cleaned = value.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalidChar, '-');
        }

        cleaned = cleaned.Replace('/', '-').Replace('\\', '-');
        return cleaned;
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        ActivityLog.Insert(0, $"[{timestamp}] {message}");
        while (ActivityLog.Count > 50)
        {
            ActivityLog.RemoveAt(ActivityLog.Count - 1);
        }
    }

    partial void OnSelectedSubjectChanged(SubjectModel? value)
    {
        RefreshClassesCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            Classes.Clear();
            return;
        }

        if (IsBusy || IsRefreshing)
        {
            _pendingSubjectName = value.Name;
            return;
        }

        _ = LoadClassesAsync();
    }

    partial void OnSelectedClassChanged(ClassModel? value)
    {
        DeleteClassCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorking));
        RefreshSubjectsCommand.NotifyCanExecuteChanged();
        RefreshClassesCommand.NotifyCanExecuteChanged();
        DeleteClassCommand.NotifyCanExecuteChanged();

        if (!value && _pendingSubjectName is not null && SelectedSubject?.Name == _pendingSubjectName)
        {
            var pending = _pendingSubjectName;
            _pendingSubjectName = null;
            if (!string.IsNullOrEmpty(pending))
            {
                _ = LoadClassesAsync();
            }
        }
    }

    partial void OnIsRefreshingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorking));
        RefreshSubjectsCommand.NotifyCanExecuteChanged();
        RefreshClassesCommand.NotifyCanExecuteChanged();

        if (!value && _pendingSubjectName is not null && SelectedSubject?.Name == _pendingSubjectName)
        {
            var pending = _pendingSubjectName;
            _pendingSubjectName = null;
            if (!string.IsNullOrEmpty(pending))
            {
                _ = LoadClassesAsync();
            }
        }
    }
}
