using CommunityToolkit.Mvvm.ComponentModel;

namespace S3VideoManager.Models;

public partial class ClassModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    public ClassModel(string name, string prefix)
    {
        Name = name;
        Prefix = prefix;
    }

    public string Name { get; }

    /// <summary>
    /// Fully-qualified prefix inside the bucket (e.g. materia/clase/).
    /// </summary>
    public string Prefix { get; }
}
