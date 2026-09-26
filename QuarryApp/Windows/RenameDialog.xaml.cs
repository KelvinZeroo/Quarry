using System.Windows;
using System.Windows.Media;
using QuarryApp.Models;

namespace QuarryApp.Windows;

public partial class RenameDialog : Window
{
    public string NewName => NameTextBox.Text.Trim();

    public RenameDialog(string initialName, Settings? settings = null)
    {
        InitializeComponent();
        NameTextBox.Text = initialName;
        NameTextBox.SelectAll();
        NameTextBox.Focus();

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
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            MessageBox.Show("Please enter a valid file name.", "Quarry", MessageBoxButton.OK, MessageBoxImage.Warning);
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
