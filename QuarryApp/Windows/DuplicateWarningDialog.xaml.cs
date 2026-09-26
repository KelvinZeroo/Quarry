using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using QuarryApp.Models;

namespace QuarryApp.Windows;

public enum DuplicateAction
{
    Cancel,
    Overwrite,
    SaveAsNew,
    OpenFile
}

public partial class DuplicateWarningDialog : Window
{
    public DuplicateAction ResultAction { get; private set; } = DuplicateAction.Cancel;
    public string ExistingPath { get; }

    public DuplicateWarningDialog(string existingPath, Settings? settings = null)
    {
        InitializeComponent();
        ExistingPath = existingPath;
        FilePathText.Text = existingPath;

        if (settings != null)
        {
            try
            {
                var fontName = string.IsNullOrWhiteSpace(settings.FontFamily) ? "Manrope" : settings.FontFamily;
                FontFamily = new FontFamily(fontName);
                if (settings.FontSize > 0) FontSize = settings.FontSize;
            }
            catch { }
        }
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        ResultAction = DuplicateAction.Overwrite;
        DialogResult = true;
        Close();
    }

    private void SaveAsNew_Click(object sender, RoutedEventArgs e)
    {
        ResultAction = DuplicateAction.SaveAsNew;
        DialogResult = true;
        Close();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        ResultAction = DuplicateAction.OpenFile;
        if (File.Exists(ExistingPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = ExistingPath, UseShellExecute = true });
            }
            catch { }
        }
        DialogResult = false;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ResultAction = DuplicateAction.Cancel;
        DialogResult = false;
        Close();
    }
}
