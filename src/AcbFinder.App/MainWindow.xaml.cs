using System.Windows;
using System.Windows.Controls;

namespace AcbFinder.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
        => LogBox.ScrollToEnd();
}
