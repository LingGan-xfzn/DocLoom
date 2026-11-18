using System.Windows;
using System.Windows.Controls;
using DocLoom.App.ViewModels;

namespace DocLoom.App.Views;

public partial class LayoutPage : UserControl
{
    public LayoutPage()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetKeyFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var path = TryGetKeyFile(e);
        if (path is null || DataContext is not LayoutViewModel viewModel)
        {
            return;
        }

        await viewModel.LoadTemplateAsync(path);
    }

    private static string? TryGetKeyFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        return e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => f.EndsWith(".xiumi-key", StringComparison.OrdinalIgnoreCase)
                                        || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            : null;
    }
}