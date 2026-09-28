using System.Collections.ObjectModel;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Maui.Services;

namespace Tedd.Commodore.Maui.Pages;

/// <summary>
/// Editor for the physical key -> action table (<see cref="KeyBindings"/>). Works on a copy; "Save" applies the
/// copy to the running session (through the runner, so the emulator thread sees a consistent table) and writes
/// it to disk.
///
/// It is a window of its own rather than a modal dialog: the key capture needs a keyboard hook that is not the
/// main window's, or every captured key would also reach the emulator.
/// </summary>
public partial class KeyBindingsPage : ContentPage
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
    private static readonly JoystickInput[] JoystickInputs = Enum.GetValues<JoystickInput>();
    private static readonly SystemCommand[] SystemCommands = Enum.GetValues<SystemCommand>();

    private readonly EmulatorRunner _runner;
    private readonly string _path;
    private readonly KeyBindings _edited;
    private readonly ObservableCollection<BindingRow> _rows = new();
    private readonly Dictionary<(int Port, JoystickInput Input), Label> _joyLabels = new();
    private readonly TaskCompletionSource _closed = new();
    private KeyboardHook? _keyboard;
    private CaptureMode _capture;
    private BindingRow? _rebindRow;
    private int _capturePort;
    private JoystickInput _captureInput;
    private string? _newCode;
    private bool _ready;

    private KeyBindingsPage(EmulatorRunner runner, string path)
    {
        InitializeComponent();
        _runner = runner;
        _path = path;
        _edited = runner.Session.Bindings.Clone();
        PathText.Text = path;

        BindingsGrid.ItemsSource = _rows;
        foreach (var choice in KeyChoices) C64KeyBox.Items.Add(choice.Name);
        C64KeyBox.SelectedIndex = 0;
        JoyPortBox.Items.Add("1");
        JoyPortBox.Items.Add("2");
        JoyPortBox.SelectedIndex = 1;
        foreach (var input in JoystickInputs) JoyInputBox.Items.Add(input.ToString());
        JoyInputBox.SelectedIndex = 0;
        foreach (var command in SystemCommands) SysCommandBox.Items.Add(command.ToString());
        SysCommandBox.SelectedIndex = 0;

        BuildJoystickPanel(JoyPanel1, 1);
        BuildJoystickPanel(JoyPanel2, 2);
        RefreshAll();
    }

    /// <summary>Opens the editor in its own window and returns when the user closes it.</summary>
    public static Task ShowAsync(Page owner, EmulatorRunner runner, string path)
    {
        _ = owner;
        var page = new KeyBindingsPage(runner, path);
        var window = new Window(page) { Title = "Key Bindings", Width = 1020, Height = 680 };
        window.Destroying += (_, _) => page._closed.TrySetResult();
        Application.Current?.OpenWindow(window);
        return page._closed.Task;
    }

    private void Page_Loaded(object? sender, EventArgs e)
    {
        if (_ready) return;
        _ready = true;
        if (Window is { } window)
        {
            _keyboard = KeyboardHook.Attach(window);
            if (_keyboard is not null) _keyboard.KeyDown += OnKeyDown;
        }
    }

    private void Page_Unloaded(object? sender, EventArgs e)
    {
        if (_keyboard is not null)
        {
            _keyboard.KeyDown -= OnKeyDown;
            _keyboard.Dispose();
            _keyboard = null;
        }
        _closed.TrySetResult();
    }

    private void Close()
    {
        if (Window is { } window) Application.Current?.CloseWindow(window);
        _closed.TrySetResult();
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
        panel.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(50)));
        panel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        panel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        panel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        panel.ColumnSpacing = 4;
        int row = 0;
        foreach (var input in JoystickInputs)
        {
            panel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            panel.Add(new Label { Text = input.ToString(), VerticalOptions = LayoutOptions.Center }, 0, row);

            var keys = new Label { VerticalOptions = LayoutOptions.Center, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation };
            panel.Add(keys, 1, row);
            _joyLabels[(port, input)] = keys;

            var captured = input;
            var set = new Button { Text = "Set...", Padding = new Thickness(8, 1), Margin = new Thickness(0, 2) };
            set.Clicked += (_, _) =>
            {
                _capturePort = port;
                _captureInput = captured;
                BeginCapture(CaptureMode.Joystick, $"Joystick {port}: {captured}");
            };
            panel.Add(set, 2, row);

            var clear = new Button { Text = "Clear", Padding = new Thickness(8, 1), Margin = new Thickness(0, 2) };
            clear.Clicked += (_, _) =>
            {
                foreach (var code in _edited.CodesFor(InputAction.ForJoystick(port, captured)).ToList())
                    _edited.Remove(code);
                RefreshAll();
            };
            panel.Add(clear, 3, row);
            row++;
        }
    }

    private void RefreshJoystickPanel()
    {
        foreach (var ((port, input), label) in _joyLabels)
        {
            var codes = _edited.CodesFor(InputAction.ForJoystick(port, input)).Select(KeyCodes.Display).ToList();
            label.Text = codes.Count == 0 ? "(none)" : string.Join(", ", codes);
            if (codes.Count == 0)
                label.SetAppThemeColor(Label.TextColorProperty, Color.FromRgb(0x80, 0x80, 0x80), Color.FromRgb(0x90, 0x90, 0x90));
            else
                label.ClearValue(Label.TextColorProperty);
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
        string filter = (FilterBox.Text ?? "").Trim();
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
                BindingsGrid.ScrollTo(row, position: ScrollToPosition.MakeVisible, animate: false);
                return;
            }
        }
    }

    private void Filter_TextChanged(object? sender, TextChangedEventArgs e) => RefreshRows();

    private void Grid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (BindingsGrid.SelectedItem is not BindingRow row) return;
        _newCode = row.Code;
        ShowNewKey();
        LoadChooser(row.InputAction);
    }

    private async void Rebind_Clicked(object? sender, EventArgs e)
    {
        if (BindingsGrid.SelectedItem is not BindingRow row)
        {
            await DisplayAlertAsync("Rebind", "Select a binding first.", "OK");
            return;
        }
        _rebindRow = row;
        BeginCapture(CaptureMode.Rebind, $"{row.Action}  (currently {row.KeyName})");
    }

    private void Remove_Clicked(object? sender, EventArgs e)
    {
        if (BindingsGrid.SelectedItem is not BindingRow row) return;
        _edited.Remove(row.Code);
        RefreshAll();
    }

    #endregion

    #region Action chooser

    private void CaptureNewKey_Clicked(object? sender, EventArgs e) => BeginCapture(CaptureMode.NewKey, "Key to assign");

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
                JoyInputBox.SelectedIndex = Array.IndexOf(JoystickInputs, action.Joystick);
                break;
            case InputActionKind.System:
                KindSysRadio.IsChecked = true;
                SysCommandBox.SelectedIndex = Array.IndexOf(SystemCommands, action.Command);
                break;
        }
    }

    private InputAction ChooserAction()
    {
        if (KindJoyRadio.IsChecked)
            return InputAction.ForJoystick(Math.Max(0, JoyPortBox.SelectedIndex) + 1, JoystickInputs[Math.Max(0, JoyInputBox.SelectedIndex)]);
        if (KindSysRadio.IsChecked)
            return InputAction.ForSystem(SystemCommands[Math.Max(0, SysCommandBox.SelectedIndex)]);
        var choice = KeyChoices[Math.Clamp(C64KeyBox.SelectedIndex, 0, KeyChoices.Length - 1)];
        return InputAction.ForKey(choice.Key, ShiftBox.IsChecked);
    }

    private async void Assign_Clicked(object? sender, EventArgs e)
    {
        if (_newCode is null)
        {
            await DisplayAlertAsync("Assign", "Capture a key first (\"Press key...\") or select a row in the table.", "OK");
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
        CaptureOverlay.IsVisible = true;
    }

    private void EndCapture()
    {
        _capture = CaptureMode.None;
        CaptureOverlay.IsVisible = false;
        _rebindRow = null;
    }

    private void CancelCapture_Clicked(object? sender, EventArgs e) => EndCapture();

    private void OnKeyDown(PhysicalKeyEvent e)
    {
        if (_capture == CaptureMode.None) return;
        e.Handled = true;
        if (e.IsRepeat) return;

        var mode = _capture;
        var rebindRow = _rebindRow;
        var code = e.Code;
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

    private async void ResetDefaults_Clicked(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Reset to defaults", "Replace all bindings with the defaults?", "Yes", "No"))
            return;
        _edited.CopyFrom(KeyBindings.CreateDefault());
        RefreshAll();
    }

    private async void Save_Clicked(object? sender, EventArgs e)
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
            await DisplayAlertAsync("Key bindings", "The bindings are in effect but could not be saved:\n" + ex.Message, "OK");
        }
        Close();
    }

    private void Cancel_Clicked(object? sender, EventArgs e) => Close();

    #endregion
}
