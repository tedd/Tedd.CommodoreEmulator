using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.GUI;

/// <summary>
/// Editor for the physical key -> action table (<see cref="KeyBindings"/>). Works on a copy; "Save" applies the
/// copy to the running session (through the runner, so the emulator thread sees a consistent table) and writes
/// it to disk.
/// </summary>
public partial class KeyBindingsWindow : Window
{
    private enum CaptureMode { None, Rebind, NewKey, Joystick }

    /// <summary>One row of the binding table.</summary>
    public sealed class BindingRow
    {
        public BindingRow(string code, InputAction action)
        {
            Code = code;
            InputAction = action;
            KeyName = KeyCodes.Display(code);
            Kind = action.Kind switch
            {
                InputActionKind.Key => "C64 key",
                InputActionKind.Joystick => "Joystick",
                _ => "Command",
            };
            Action = action.Describe();
        }

        public string Code { get; }
        public string KeyName { get; }
        public string Kind { get; }
        public string Action { get; }
        public InputAction InputAction { get; }

        public bool Matches(string filter) =>
            KeyName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Code.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Kind.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            Action.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Entry of the C64 key chooser.</summary>
    public sealed record KeyChoice(C64Key Key, string Name);

    private static readonly KeyChoice[] KeyChoices = BuildKeyChoices();

    private readonly EmulatorRunner _runner;
    private readonly string _path;
    private readonly KeyBindings _edited;
    private readonly ObservableCollection<BindingRow> _rows = new();
    private readonly Dictionary<(int Port, JoystickInput Input), TextBlock> _joyLabels = new();
    private CaptureMode _capture;
    private BindingRow? _rebindRow;
    private int _capturePort;
    private JoystickInput _captureInput;
    private string? _newCode;

    public KeyBindingsWindow(EmulatorRunner runner, string path)
    {
        InitializeComponent();
        _runner = runner;
        _path = path;
        _edited = runner.Session.Bindings.Clone();
        PathText.Text = path;
        PathText.ToolTip = path;

        BindingsGrid.ItemsSource = _rows;
        C64KeyBox.ItemsSource = KeyChoices;
        C64KeyBox.SelectedIndex = 0;
        JoyInputBox.ItemsSource = Enum.GetValues<JoystickInput>();
        JoyInputBox.SelectedIndex = 0;
        SysCommandBox.ItemsSource = Enum.GetValues<SystemCommand>();
        SysCommandBox.SelectedIndex = 0;

        BuildJoystickPanel(JoyPanel1, 1);
        BuildJoystickPanel(JoyPanel2, 2);
        RefreshAll();
    }

    private static KeyChoice[] BuildKeyChoices()
    {
        var all = Enum.GetValues<C64Key>();
        int Rank(C64Key k)
        {
            var n = k.ToString();
            if (n.Length == 1 && char.IsLetter(n[0])) return 0;
            if (n.Length == 2 && n[0] == 'D' && char.IsDigit(n[1])) return 1;
            return 2;
        }
        return all
            .OrderBy(Rank)
            .ThenBy(k => Rank(k) == 2 ? (int)k : 0)
            .ThenBy(k => InputAction.DescribeKey(k), StringComparer.Ordinal)
            .Select(k => new KeyChoice(k, InputAction.DescribeKey(k)))
            .ToArray();
    }

    #region Joystick panel

    private void BuildJoystickPanel(Grid panel, int port)
    {
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        foreach (var input in Enum.GetValues<JoystickInput>())
        {
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = input.ToString(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(label, row); Grid.SetColumn(label, 0);
            panel.Children.Add(label);

            var keys = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 6, 2) };
            Grid.SetRow(keys, row); Grid.SetColumn(keys, 1);
            panel.Children.Add(keys);
            _joyLabels[(port, input)] = keys;

            var set = new Button { Content = "Set...", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 2, 4, 2), Tag = (port, input) };
            set.Click += JoySet_Click;
            Grid.SetRow(set, row); Grid.SetColumn(set, 2);
            panel.Children.Add(set);

            var clear = new Button { Content = "Clear", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 2, 0, 2), Tag = (port, input) };
            clear.Click += JoyClear_Click;
            Grid.SetRow(clear, row); Grid.SetColumn(clear, 3);
            panel.Children.Add(clear);
            row++;
        }
    }

    private void JoySet_Click(object sender, RoutedEventArgs e)
    {
        var (port, input) = ((int, JoystickInput))((Button)sender).Tag;
        _capturePort = port;
        _captureInput = input;
        BeginCapture(CaptureMode.Joystick, $"Joystick {port}: {input}");
    }

    private void JoyClear_Click(object sender, RoutedEventArgs e)
    {
        var (port, input) = ((int, JoystickInput))((Button)sender).Tag;
        foreach (var code in _edited.CodesFor(InputAction.ForJoystick(port, input)).ToList())
            _edited.Remove(code);
        RefreshAll();
    }

    private void RefreshJoystickPanel()
    {
        foreach (var ((port, input), label) in _joyLabels)
        {
            var codes = _edited.CodesFor(InputAction.ForJoystick(port, input)).Select(KeyCodes.Display).ToList();
            label.Text = codes.Count == 0 ? "(none)" : string.Join(", ", codes);
            label.Foreground = codes.Count == 0 ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;
        }
    }

    #endregion

    #region Table

    private void RefreshAll()
    {
        RefreshRows();
        RefreshJoystickPanel();
    }

    private void RefreshRows()
    {
        string filter = FilterBox.Text.Trim();
        string? selected = (BindingsGrid.SelectedItem as BindingRow)?.Code;
        _rows.Clear();
        foreach (var code in OrderedCodes())
        {
            if (!_edited.TryGet(code, out var action)) continue;
            var row = new BindingRow(code, action);
            if (filter.Length > 0 && !row.Matches(filter)) continue;
            _rows.Add(row);
        }
        if (selected is not null) SelectCode(selected);
    }

    /// <summary>Bound codes in physical keyboard order (KeyCodes.All), then anything else alphabetically.</summary>
    private IEnumerable<string> OrderedCodes()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in KeyCodes.All)
            if (_edited.TryGet(code, out _) && seen.Add(code)) yield return code;
        foreach (var code in _edited.All.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            if (seen.Add(code)) yield return code;
    }

    private void SelectCode(string code)
    {
        foreach (var row in _rows)
        {
            if (string.Equals(row.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                BindingsGrid.SelectedItem = row;
                BindingsGrid.ScrollIntoView(row);
                return;
            }
        }
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) RefreshRows();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BindingsGrid.SelectedItem is not BindingRow row) return;
        _newCode = row.Code;
        ShowNewKey();
        LoadChooser(row.InputAction);
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => BeginRebind();

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capture != CaptureMode.None) return;
        switch (e.Key)
        {
            case Key.Enter: BeginRebind(); e.Handled = true; break;
            case Key.Delete: RemoveSelected(); e.Handled = true; break;
        }
    }

    private void Rebind_Click(object sender, RoutedEventArgs e) => BeginRebind();

    private void BeginRebind()
    {
        if (BindingsGrid.SelectedItem is not BindingRow row)
        {
            MessageBox.Show(this, "Select a binding first.", "Rebind", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _rebindRow = row;
        BeginCapture(CaptureMode.Rebind, $"{row.Action}  (currently {row.KeyName})");
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        if (BindingsGrid.SelectedItem is not BindingRow row) return;
        _edited.Remove(row.Code);
        RefreshAll();
    }

    #endregion

    #region Action chooser

    private void CaptureNewKey_Click(object sender, RoutedEventArgs e) => BeginCapture(CaptureMode.NewKey, "Key to assign");

    private void ShowNewKey()
    {
        if (_newCode is null)
        {
            NewKeyText.Text = "(none)";
            NewKeyCurrentText.Text = "";
            return;
        }
        NewKeyText.Text = $"{KeyCodes.Display(_newCode)}   [{_newCode}]";
        NewKeyCurrentText.Text = _edited.TryGet(_newCode, out var current)
            ? "Currently: " + current.Describe()
            : "Currently unbound";
    }

    private void LoadChooser(InputAction action)
    {
        switch (action.Kind)
        {
            case InputActionKind.Key:
                KindKeyRadio.IsChecked = true;
                for (int i = 0; i < KeyChoices.Length; i++)
                    if (KeyChoices[i].Key == action.Key) { C64KeyBox.SelectedIndex = i; break; }
                ShiftBox.IsChecked = action.Shift;
                break;
            case InputActionKind.Joystick:
                KindJoyRadio.IsChecked = true;
                JoyPortBox.SelectedIndex = action.JoystickPort - 1;
                JoyInputBox.SelectedItem = action.Joystick;
                break;
            case InputActionKind.System:
                KindSysRadio.IsChecked = true;
                SysCommandBox.SelectedItem = action.Command;
                break;
        }
    }

    private InputAction ChooserAction()
    {
        if (KindJoyRadio.IsChecked == true)
            return InputAction.ForJoystick(Math.Max(0, JoyPortBox.SelectedIndex) + 1, JoyInputBox.SelectedItem is JoystickInput j ? j : JoystickInput.Fire);
        if (KindSysRadio.IsChecked == true)
            return InputAction.ForSystem(SysCommandBox.SelectedItem is SystemCommand c ? c : SystemCommand.Reset);
        var choice = C64KeyBox.SelectedItem as KeyChoice ?? KeyChoices[0];
        return InputAction.ForKey(choice.Key, ShiftBox.IsChecked == true);
    }

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (_newCode is null)
        {
            MessageBox.Show(this, "Capture a key first (\"Press key...\") or select a row in the table.", "Assign", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _edited.Set(_newCode, ChooserAction());
        RefreshAll();
        SelectCode(_newCode);
        ShowNewKey();
    }

    #endregion

    #region Key capture

    private void BeginCapture(CaptureMode mode, string what)
    {
        _capture = mode;
        CaptureText.Text = "Press a key for:\n" + what;
        CaptureOverlay.Visibility = Visibility.Visible;
        CaptureOverlay.Focus();
    }

    private void EndCapture()
    {
        _capture = CaptureMode.None;
        CaptureOverlay.Visibility = Visibility.Collapsed;
        _rebindRow = null;
    }

    private void CancelCapture_Click(object sender, RoutedEventArgs e) => EndCapture();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capture == CaptureMode.None) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        if (!WpfKeyCodes.TryGetCode(e, out var code)) return;

        var mode = _capture;
        var rebindRow = _rebindRow;
        EndCapture();
        switch (mode)
        {
            case CaptureMode.Rebind:
                if (rebindRow is null) return;
                _edited.Remove(rebindRow.Code);
                _edited.Set(code, rebindRow.InputAction);
                RefreshAll();
                SelectCode(code);
                break;
            case CaptureMode.NewKey:
                _newCode = code;
                ShowNewKey();
                if (_edited.TryGet(code, out var existing)) LoadChooser(existing);
                break;
            case CaptureMode.Joystick:
                _edited.Set(code, InputAction.ForJoystick(_capturePort, _captureInput));
                RefreshAll();
                SelectCode(code);
                break;
        }
    }

    #endregion

    #region Dialog buttons

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Replace all bindings with the defaults?", "Reset to defaults", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _edited.CopyFrom(KeyBindings.CreateDefault());
        RefreshAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var live = _runner.Session.Bindings;
        var session = _runner.Session;
        _runner.Invoke(() =>
        {
            session.ReleaseAllInput();
            live.CopyFrom(_edited);
        });
        try
        {
            live.Save(_path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The bindings are in effect but could not be saved:\n" + ex.Message, "Key bindings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    #endregion
}
