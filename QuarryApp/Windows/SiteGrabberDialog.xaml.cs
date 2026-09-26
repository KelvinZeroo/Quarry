using System.Windows;
using System.Windows.Media;
using QuarryApp.Models;

namespace QuarryApp.Windows;

public partial class SiteGrabberDialog : Window
{
    public string StartUrl => StartUrlTextBox.Text.Trim();
    public int? MaxScan => int.TryParse(MaxScanTextBox.Text.Trim(), out var val) ? val : null;

    public SiteGrabberDialog(Settings? settings = null)
    {
        InitializeComponent();

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

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(StartUrlTextBox.Text))
        {
            MessageBox.Show("Please enter a valid starting URL.", "Site Grabber", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
