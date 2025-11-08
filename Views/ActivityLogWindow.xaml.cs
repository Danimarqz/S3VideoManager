using System;
using System.Windows;
using S3VideoManager.ViewModels;

namespace S3VideoManager.Views;

public partial class ActivityLogWindow : Window
{
    public ActivityLogWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
