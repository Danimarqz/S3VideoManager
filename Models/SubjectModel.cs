using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace S3VideoManager.Models;

public partial class SubjectModel : ObservableObject
{
    private readonly ObservableCollection<ClassModel> _classes = new();

    public SubjectModel(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _classes.CollectionChanged += OnClassesChanged;
    }

    public string Name { get; }

    public ObservableCollection<ClassModel> Classes => _classes;

    public bool HasClasses => _classes.Count > 0;

    public void ClearClasses() => _classes.Clear();

    public void AddClass(ClassModel model) => _classes.Add(model);

    private void OnClassesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasClasses));
    }
}
