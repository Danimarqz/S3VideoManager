using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
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
    private bool _forceRefreshPending;
    private readonly Dictionary<string, IReadOnlyList<string>> _classesCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingClassSelection;
    private CancellationTokenSource? _transferCts;

    public ObservableCollection<SubjectModel> Subjects { get; } = new();
    public ObservableCollection<ClassModel> Classes { get; } = new();
    public ObservableCollection<string> ActivityLog { get; } = new();
    public ICollectionView SubjectsView { get; }

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

    [ObservableProperty]
    private string _subjectFilterText = string.Empty;

    [ObservableProperty]
    private bool _transferInProgress;

    [ObservableProperty]
    private string? _activeTransferDescription;

    public IAsyncRelayCommand RefreshSubjectsCommand { get; }
    public IAsyncRelayCommand RefreshClassesCommand { get; }
    public IAsyncRelayCommand DeleteClassCommand { get; }

    public bool IsWorking => IsBusy || IsRefreshing;

    public MainViewModel(S3Service s3Service, FfmpegService ffmpegService)
    {
        _s3Service = s3Service ?? throw new ArgumentNullException(nameof(s3Service));
        _ffmpegService = ffmpegService ?? throw new ArgumentNullException(nameof(ffmpegService));

        SubjectsView = CollectionViewSource.GetDefaultView(Subjects);
        SubjectsView.Filter = FilterSubject;

        RefreshSubjectsCommand = new AsyncRelayCommand(LoadSubjectsAsync, () => !IsBusy && !IsRefreshing);
        RefreshClassesCommand = new AsyncRelayCommand(() => LoadClassesAsync(forceRefresh: true), () => !IsBusy && !IsRefreshing && SelectedSubject is not null);
        DeleteClassCommand = new AsyncRelayCommand(DeleteSelectedClassAsync, () => !IsBusy && !TransferInProgress && SelectedClass is { IsBusy: false });
    }

    public bool CanStartTransfer => !IsWorking && !TransferInProgress;

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
            _classesCache.Clear();
            _pendingSubjectName = null;
            _forceRefreshPending = false;
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

    public async Task LoadClassesAsync(bool forceRefresh = false, string? preferredClassToSelect = null)
    {
        var currentSubject = SelectedSubject;
        if (currentSubject is null)
        {
            Classes.Clear();
            return;
        }

        if (IsBusy || IsRefreshing)
        {
            _pendingSubjectName = currentSubject.Name;
            _forceRefreshPending |= forceRefresh;
            if (!string.IsNullOrWhiteSpace(preferredClassToSelect))
            {
                _pendingClassSelection = preferredClassToSelect;
            }
            return;
        }

        if (!forceRefresh && _classesCache.TryGetValue(currentSubject.Name, out var cachedClasses))
        {
            PopulateClasses(currentSubject.Name, cachedClasses);
            RestoreClassSelection(preferredClassToSelect);
            StatusMessage = $"Clases en cache para {currentSubject.Name}.";
            return;
        }

        try
        {
            IsRefreshing = true;
            StatusMessage = $"Cargando clases de {currentSubject.Name}...";
            var classNames = await Task.Run(async () => await _s3Service.GetClassesAsync(currentSubject.Name)).ConfigureAwait(true);
            var classList = classNames.ToList();
            _classesCache[currentSubject.Name] = classList;
            PopulateClasses(currentSubject.Name, classList);
            RestoreClassSelection(preferredClassToSelect);
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
            TryLoadPendingClasses();
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
        var subject = SelectedSubject;
        if (subject is null)
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

        if (!CanStartTransfer)
        {
            StatusMessage = "Espera a que termine la operación en curso.";
            return false;
        }

        string? workDirectory = null;
        var transferToken = BeginTransfer($"Subiendo '{className}'");
        try
        {
            OperationProgress = 0;
            StatusMessage = $"Transcodificando '{className}'...";

            var logProgress = new Progress<string>(AddLog);
            var encodeProgress = new Progress<double>(value => OperationProgress = Math.Clamp(value * 0.5, 0, 0.5));
            workDirectory = await _ffmpegService.TranscodeToHlsAsync(videoFilePath, null, logProgress, encodeProgress, transferToken)
                .ConfigureAwait(true);

            transferToken.ThrowIfCancellationRequested();

            if (shouldOverwrite)
            {
                StatusMessage = $"Eliminando versión anterior de '{className}'...";
                await _s3Service.DeleteClassAsync(subject.Name, className, transferToken).ConfigureAwait(true);
                AddLog($"Clase '{className}' eliminada antes de la nueva subida.");
            }

            StatusMessage = $"Subiendo '{className}' a S3...";
            var uploadProgress = new Progress<double>(value => OperationProgress = 0.5 + Math.Clamp(value, 0, 1) * 0.5);
            await _s3Service.UploadClassAsync(subject.Name, className, workDirectory, uploadProgress, transferToken)
                .ConfigureAwait(true);

            _classesCache.Remove(subject.Name);
            StatusMessage = $"Clase '{className}' subida correctamente.";
            AddLog($"Clase '{className}' subida a {subject.Name}.");

            var subjectStillSelected = SelectedSubject is not null &&
                                       string.Equals(SelectedSubject.Name, subject.Name, StringComparison.OrdinalIgnoreCase);
            if (subjectStillSelected)
            {
                await LoadClassesAsync(forceRefresh: true).ConfigureAwait(true);
            }
            else
            {
                _pendingSubjectName = subject.Name;
                _forceRefreshPending = true;
                _pendingClassSelection = className;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Transferencia cancelada.";
            AddLog("Transferencia cancelada por el usuario.");
            return false;
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
            EndTransfer();

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
        var subject = SelectedSubject;
        var classToDelete = SelectedClass;

        if (subject is null || classToDelete is null)
        {
            StatusMessage = "Selecciona una clase para eliminar.";
            return false;
        }

        if (IsBusy)
        {
            StatusMessage = "Espera a que termine la operación actual.";
            return false;
        }

        if (TransferInProgress)
        {
            StatusMessage = "Ya hay una transferencia en curso.";
            return false;
        }

        var cancellationToken = BeginTransfer($"Eliminando '{classToDelete.Name}'");
        try
        {
            classToDelete.IsBusy = true;
            DeleteClassCommand.NotifyCanExecuteChanged();
            StatusMessage = $"Eliminando '{classToDelete.Name}'...";
            await _s3Service.DeleteClassAsync(subject.Name, classToDelete.Name, cancellationToken).ConfigureAwait(true);
            _classesCache.Remove(subject.Name);

            var subjectIsActive = SelectedSubject is not null &&
                                  string.Equals(SelectedSubject.Name, subject.Name, StringComparison.OrdinalIgnoreCase);
            string? desiredSelection = null;
            if (subjectIsActive)
            {
                var removedIndex = Classes.IndexOf(classToDelete);
                if (removedIndex >= 0)
                {
                    var wasSelected = ReferenceEquals(SelectedClass, classToDelete);
                    Classes.RemoveAt(removedIndex);
                    if (wasSelected)
                    {
                        if (Classes.Count == 0)
                        {
                            SelectedClass = null;
                        }
                        else
                        {
                            var nextIndex = Math.Min(removedIndex, Classes.Count - 1);
                            SelectedClass = Classes[nextIndex];
                        }
                    }

                    desiredSelection = SelectedClass?.Name;
                }
            }

            AddLog($"Clase '{classToDelete.Name}' eliminada de {subject.Name}.");

            if (subjectIsActive)
            {
                await LoadClassesAsync(forceRefresh: true, preferredClassToSelect: desiredSelection).ConfigureAwait(true);
            }

            StatusMessage = "Clase eliminada.";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Transferencia cancelada.";
            AddLog("Transferencia cancelada por el usuario.");
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al eliminar: {ex.Message}";
            AddLog($"Error al eliminar: {ex}");
            return false;
        }
        finally
        {
            classToDelete.IsBusy = false;
            DeleteClassCommand.NotifyCanExecuteChanged();
            EndTransfer();
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
        OnPropertyChanged(nameof(CanStartTransfer));
        RefreshSubjectsCommand.NotifyCanExecuteChanged();
        RefreshClassesCommand.NotifyCanExecuteChanged();
        DeleteClassCommand.NotifyCanExecuteChanged();

        if (!value)
        {
            TryLoadPendingClasses();
        }
    }

    partial void OnIsRefreshingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(CanStartTransfer));
        RefreshSubjectsCommand.NotifyCanExecuteChanged();
        RefreshClassesCommand.NotifyCanExecuteChanged();

        if (!value)
        {
            TryLoadPendingClasses();
        }
    }

    private void RestoreClassSelection(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return;
        }

        var match = Classes.FirstOrDefault(c =>
            c.Name.Equals(className, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            SelectedClass = match;
        }
    }

    private void PopulateClasses(string subjectName, IEnumerable<string> classNames)
    {
        Classes.Clear();
        foreach (var className in classNames)
        {
            var prefix = $"{subjectName}/{className}".Replace("//", "/");
            Classes.Add(new ClassModel(className, $"{prefix}/"));
        }

        SelectedClass = Classes.FirstOrDefault();
    }

    private void TryLoadPendingClasses()
    {
        if (_pendingSubjectName is null)
        {
            return;
        }

        if (!string.Equals(SelectedSubject?.Name, _pendingSubjectName, StringComparison.Ordinal))
        {
            return;
        }

        var forceRefresh = _forceRefreshPending;
        _pendingSubjectName = null;
        _forceRefreshPending = false;
        var classSelection = _pendingClassSelection;
        _pendingClassSelection = null;
        _ = LoadClassesAsync(forceRefresh, classSelection);
    }

    private CancellationToken BeginTransfer(string description)
    {
        if (TransferInProgress)
        {
            throw new InvalidOperationException("Transfer already running.");
        }

        _transferCts = new CancellationTokenSource();
        ActiveTransferDescription = description;
        TransferInProgress = true;
        return _transferCts.Token;
    }

    private void EndTransfer()
    {
        _transferCts?.Dispose();
        _transferCts = null;
        TransferInProgress = false;
        ActiveTransferDescription = null;
    }

    public bool CancelActiveTransfer()
    {
        if (_transferCts is null)
        {
            return false;
        }

        _transferCts.Cancel();
        return true;
    }

    partial void OnSubjectFilterTextChanged(string value)
    {
        SubjectsView.Refresh();
    }

    partial void OnTransferInProgressChanged(bool value)
    {
        DeleteClassCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStartTransfer));
    }

    private bool FilterSubject(object? item)
    {
        if (item is not SubjectModel subject)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SubjectFilterText))
        {
            return true;
        }

        return subject.Name.Contains(SubjectFilterText, StringComparison.OrdinalIgnoreCase);
    }
}
