using System.Windows;
using System.Windows.Media;
using QuarryApp.Models;

namespace QuarryApp.Windows;

public class ColumnsVisibilityState
{
    public bool ShowFileName { get; set; } = true;
    public bool ShowSize { get; set; } = true;
    public bool ShowStatus { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool ShowTransferRate { get; set; } = true;
    public bool ShowTimeLeft { get; set; } = true;
    public bool ShowLastTryDate { get; set; } = true;
    public bool ShowUrl { get; set; } = true;
    public bool ShowFolder { get; set; } = false;
    public bool ShowCategory { get; set; } = false;
    public bool ShowQuality { get; set; } = false;
}

public partial class CustomizeColumnsDialog : Window
{
    public ColumnsVisibilityState State { get; private set; }

    public CustomizeColumnsDialog(ColumnsVisibilityState current, Settings? settings = null)
    {
        InitializeComponent();
        State = current;

        CheckFileName.IsChecked = current.ShowFileName;
        CheckSize.IsChecked = current.ShowSize;
        CheckStatus.IsChecked = current.ShowStatus;
        CheckProgress.IsChecked = current.ShowProgress;
        CheckTransferRate.IsChecked = current.ShowTransferRate;
        CheckTimeLeft.IsChecked = current.ShowTimeLeft;
        CheckLastTryDate.IsChecked = current.ShowLastTryDate;
        CheckUrl.IsChecked = current.ShowUrl;
        CheckFolder.IsChecked = current.ShowFolder;
        CheckCategory.IsChecked = current.ShowCategory;
        CheckQuality.IsChecked = current.ShowQuality;

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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        State.ShowFileName = CheckFileName.IsChecked == true;
        State.ShowSize = CheckSize.IsChecked == true;
        State.ShowStatus = CheckStatus.IsChecked == true;
        State.ShowProgress = CheckProgress.IsChecked == true;
        State.ShowTransferRate = CheckTransferRate.IsChecked == true;
        State.ShowTimeLeft = CheckTimeLeft.IsChecked == true;
        State.ShowLastTryDate = CheckLastTryDate.IsChecked == true;
        State.ShowUrl = CheckUrl.IsChecked == true;
        State.ShowFolder = CheckFolder.IsChecked == true;
        State.ShowCategory = CheckCategory.IsChecked == true;
        State.ShowQuality = CheckQuality.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        CheckFileName.IsChecked = true;
        CheckSize.IsChecked = true;
        CheckStatus.IsChecked = true;
        CheckProgress.IsChecked = true;
        CheckTransferRate.IsChecked = true;
        CheckTimeLeft.IsChecked = true;
        CheckLastTryDate.IsChecked = true;
        CheckUrl.IsChecked = true;
        CheckFolder.IsChecked = false;
        CheckCategory.IsChecked = false;
        CheckQuality.IsChecked = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
