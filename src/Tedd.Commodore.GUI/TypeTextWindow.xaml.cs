using System.Windows;

namespace Tedd.Commodore.GUI;

public partial class TypeTextWindow : Window
{
    public string Text => Input.Text.Replace("\r\n", "\n");

    public TypeTextWindow()
    {
        InitializeComponent();
        Input.Focus();
        Input.CaretIndex = Input.Text.Length;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
