using System;
using System.Collections.Generic;
using Tedd.Commodore.Hosting;
using static SDL2.SDL;

namespace Tedd.Commodore.Sdl;

/// <summary>
/// Opens every SDL game controller (hot-plug aware) and maps d-pad / left stick to the C64 joystick directions and
/// A / B to fire, all on one joystick port. Several controllers are OR-ed together.
/// </summary>
/// <remarks>
/// Feed every SDL event through <see cref="HandleEvent"/> from the SDL event thread; the sink reports changes of the
/// merged joystick state through the callback given to the constructor (which the host typically routes to
/// <see cref="EmulatorSession.SetJoystick"/> under the runner lock). Controllers that are already plugged in when SDL
/// starts are announced by SDL with <c>SDL_CONTROLLERDEVICEADDED</c> events, so start-up and hot-plug share one path;
/// the constructor also enumerates them in case those events were already consumed.
/// </remarks>
public sealed class SdlGameControllers : IDisposable
{
    private sealed class Pad
    {
        public IntPtr Handle;
        public string Name = "";
        public bool DpadUp, DpadDown, DpadLeft, DpadRight, Fire;
        public short StickX, StickY;
    }

    private readonly Dictionary<int, Pad> _pads = new();
    private readonly bool[] _state = new bool[5];
    private readonly Action<int, JoystickInput, bool> _apply;
    private bool _disposed;

    /// <param name="apply">Receives (port, input, pressed) whenever the merged state of an input changes.</param>
    /// <param name="port">C64 joystick port (1 or 2) the controllers drive.</param>
    public SdlGameControllers(Action<int, JoystickInput, bool> apply, int port = 2)
    {
        if (port is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(port));
        _apply = apply;
        Port = port;
        if (SDL_InitSubSystem(SDL_INIT_GAMECONTROLLER) != 0)
            throw new SdlException("SDL_InitSubSystem(SDL_INIT_GAMECONTROLLER)");
        int n = SDL_NumJoysticks();
        for (int i = 0; i < n; i++)
            Open(i);
    }

    /// <summary>The C64 joystick port (1 or 2) the controllers are connected to.</summary>
    public int Port { get; }
    /// <summary>Stick deflection (0..32767) treated as a direction press.</summary>
    public int DeadZone { get; set; } = 8000;
    /// <summary>Number of controllers currently open.</summary>
    public int Count => _pads.Count;
    /// <summary>Names of the open controllers.</summary>
    public IEnumerable<string> Names
    {
        get { foreach (var p in _pads.Values) yield return p.Name; }
    }
    /// <summary>Connection / error messages.</summary>
    public event Action<string>? Log;

    /// <summary>Processes a controller event; returns false for events that are not controller related.</summary>
    public bool HandleEvent(in SDL_Event e)
    {
        switch (e.type)
        {
            case SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                Open(e.cdevice.which);           // which = device index
                return true;
            case SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                Close(e.cdevice.which);          // which = instance id
                return true;
            case SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
            case SDL_EventType.SDL_CONTROLLERBUTTONUP:
                if (_pads.TryGetValue(e.cbutton.which, out var pad))
                {
                    bool down = e.type == SDL_EventType.SDL_CONTROLLERBUTTONDOWN;
                    switch ((SDL_GameControllerButton)e.cbutton.button)
                    {
                        case SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A:
                        case SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B:
                            pad.Fire = down; break;
                        case SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP: pad.DpadUp = down; break;
                        case SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN: pad.DpadDown = down; break;
                        case SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT: pad.DpadLeft = down; break;
                        case SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT: pad.DpadRight = down; break;
                        default: return true;
                    }
                    Refresh();
                }
                return true;
            case SDL_EventType.SDL_CONTROLLERAXISMOTION:
                if (_pads.TryGetValue(e.caxis.which, out pad))
                {
                    switch ((SDL_GameControllerAxis)e.caxis.axis)
                    {
                        case SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX: pad.StickX = e.caxis.axisValue; break;
                        case SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY: pad.StickY = e.caxis.axisValue; break;
                        default: return true;
                    }
                    Refresh();
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>Releases every input (window lost focus). The next event re-applies the real state.</summary>
    public void Release()
    {
        foreach (var p in _pads.Values)
        {
            p.DpadUp = p.DpadDown = p.DpadLeft = p.DpadRight = p.Fire = false;
            p.StickX = p.StickY = 0;
        }
        Refresh();
    }

    private void Open(int deviceIndex)
    {
        if (SDL_IsGameController(deviceIndex) != SDL_bool.SDL_TRUE)
            return;
        int instance = SDL_JoystickGetDeviceInstanceID(deviceIndex);
        if (instance >= 0 && _pads.ContainsKey(instance))
            return;
        var handle = SDL_GameControllerOpen(deviceIndex);
        if (handle == IntPtr.Zero)
        {
            Log?.Invoke($"Could not open controller {deviceIndex}: {SDL_GetError()}");
            return;
        }
        instance = SDL_JoystickInstanceID(SDL_GameControllerGetJoystick(handle));
        if (_pads.ContainsKey(instance))
        {
            SDL_GameControllerClose(handle);
            return;
        }
        var pad = new Pad { Handle = handle, Name = SDL_GameControllerName(handle) ?? "Game controller" };
        _pads[instance] = pad;
        Log?.Invoke($"Controller connected: {pad.Name} -> joystick port {Port}");
    }

    private void Close(int instance)
    {
        if (!_pads.Remove(instance, out var pad))
            return;
        SDL_GameControllerClose(pad.Handle);
        Log?.Invoke($"Controller disconnected: {pad.Name}");
        Refresh();
    }

    private void Refresh()
    {
        Span<bool> now = stackalloc bool[5];
        foreach (var p in _pads.Values)
        {
            now[(int)JoystickInput.Up] |= p.DpadUp || p.StickY < -DeadZone;
            now[(int)JoystickInput.Down] |= p.DpadDown || p.StickY > DeadZone;
            now[(int)JoystickInput.Left] |= p.DpadLeft || p.StickX < -DeadZone;
            now[(int)JoystickInput.Right] |= p.DpadRight || p.StickX > DeadZone;
            now[(int)JoystickInput.Fire] |= p.Fire;
        }
        for (int i = 0; i < now.Length; i++)
        {
            if (now[i] == _state[i]) continue;
            _state[i] = now[i];
            _apply(Port, (JoystickInput)i, now[i]);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var p in _pads.Values)
            SDL_GameControllerClose(p.Handle);
        _pads.Clear();
        SDL_QuitSubSystem(SDL_INIT_GAMECONTROLLER);
    }
}
