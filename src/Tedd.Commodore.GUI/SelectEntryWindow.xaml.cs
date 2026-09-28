using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using Tedd.Commodore.Emulator.Media;

namespace Tedd.Commodore.GUI;

public partial class SelectEntryWindow : Window
{
    public int SelectedIndex { get; private set; }

    public SelectEntryWindow(T64Image image)
    {
        InitializeComponent();
        var rows = new List<object>();
        foreach (var e in image.Entries)
            rows.Add(new { e.Name, Type = e.FileType == 0x82 ? "PRG" : $"${e.FileType:X2}", Start = $"${e.StartAddress:X4}", End = $"${e.EndAddress:X4}" });
        List.ItemsSource = rows;
        List.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (List.SelectedIndex < 0) return;
        SelectedIndex = List.SelectedIndex;
        DialogResult = true;
    }
}
