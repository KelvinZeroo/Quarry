using System.Windows;
using System.Windows.Media;
using QuarryApp.Models;

namespace QuarryApp.Windows;

public partial class FilePropertiesDialog : Window
{
    public FilePropertiesDialog(DownloadJob job, Settings? settings = null)
    {
        InitializeComponent();

        TitleText.Text = job.Title;
        PathText.Text = !string.IsNullOrEmpty(job.FilePath) ? job.FilePath : job.Folder;
        UrlText.Text = job.Url;
        SizeText.Text = job.SizeDisplay;
        StatusText.Text = $"{job.StatusDisplay} ({job.ProgressDisplay})";
        DateText.Text = job.DateAdded.ToString("f");

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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
