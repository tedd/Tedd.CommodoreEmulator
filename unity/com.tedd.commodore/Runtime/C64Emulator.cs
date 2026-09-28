// Compiled by Unity only: the guard keeps this file out of the .NET solution (it is not part of any csproj) and the
// code is kept to C# 9 so that Unity 2021.3's compiler accepts it.
#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using Tedd.Commodore.Hosting;
using UnityEngine;

namespace Tedd.Commodore.Unity
{
    /// <summary>
    /// A Commodore 64 in a scene. Every Update it advances the emulator by the frame's delta time, uploads the
    /// 384 x 272 picture to <see cref="Texture"/> (shown on <see cref="targetRenderer"/>'s material and/or a uGUI
    /// RawImage), plays the SID through the AudioSource on the same GameObject (OnAudioFilterRead) and forwards
    /// keyboard input from the legacy Input manager through the emulator's key bindings.
    /// ROM images come from TextAsset fields (files renamed to *.bytes) or from StreamingAssets.
    /// </summary>
    [AddComponentMenu("Tedd.Commodore/C64 Emulator")]
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    public class C64Emulator : MonoBehaviour
    {
        [Header("ROM images as TextAssets (rename the files to *.bytes so Unity imports them as binary)")]
        [Tooltip("BASIC V2 ROM, 8192 bytes")]
        public TextAsset basicRom;
        [Tooltip("KERNAL ROM, 8192 bytes")]
        public TextAsset kernalRom;
        [Tooltip("Character generator ROM, 4096 bytes")]
        public TextAsset chargenRom;
        [Tooltip("1541 DOS ROM, 16384 bytes. Optional: without it there is no disk drive (.d64 cannot be used).")]
        public TextAsset driveRom;

        [Header("...or ROM files under Assets/StreamingAssets/<folder>/")]
        public string streamingAssetsFolder = "C64";
        public string basicFileName = "basic.bin";
        public string kernalFileName = "kernal.bin";
        public string chargenFileName = "chargen.bin";
        public string driveFileName = "1541.bin";

        [Header("Output")]
        [Tooltip("The main texture of this renderer's material receives the picture (a Quad with an Unlit material works well).")]
        public Renderer targetRenderer;
#if TEDD_COMMODORE_UGUI
        [Tooltip("A uGUI RawImage that receives the picture.")]
        public UnityEngine.UI.RawImage targetImage;
#endif
        [Tooltip("Unity textures start at the bottom-left corner; keep this on unless you flip the UVs yourself.")]
        public bool flipVertically = true;
        [Tooltip("Advance the emulator with unscaled time so that Time.timeScale does not affect it.")]
        public bool useUnscaledTime = false;

        [Header("Input")]
        [Tooltip("Poll the legacy Input manager every frame and forward key presses through the key bindings.")]
        public bool captureKeyboard = true;
        [Tooltip("Release every key and joystick input when the application loses focus.")]
        public bool releaseInputOnFocusLoss = true;
        [Tooltip("Act on bound system commands (F11 reset, Pause key pauses, ...) in addition to raising Command.")]
        public bool handleSystemCommands = true;

        [Header("Media")]
        [Tooltip("Optional .d64/.t64/.prg/.crt to attach at start (rename the file to *.bytes to import it as a TextAsset).")]
        public TextAsset startupMedia;
        [Tooltip("Original file name of the startup media; its extension selects the media type (e.g. game.d64).")]
        public string startupMediaFileName = "";
        public bool autostartMedia = true;

        /// <summary>The bridge to the emulator; available after Awake.</summary>
        public C64Bridge Bridge { get; private set; }

        /// <summary>The 384 x 272 RGBA32 point-filtered texture that receives every frame.</summary>
        public Texture2D Texture { get; private set; }

        /// <summary>Raised for host commands requested through the key bindings (reset, screenshot, pause, warp...).</summary>
        public event Action<SystemCommand> Command;

        private byte[] _rgba;
        private KeyCode[] _keyCodes;
        private string[] _webCodes;
        private int _heldKeys;
        private volatile bool _audioEnabled;

        /// <summary>Pauses emulation (the picture freezes, audio stops).</summary>
        public bool Paused
        {
            get => Bridge != null && Bridge.Paused;
            set { if (Bridge != null) Bridge.Paused = value; }
        }

        /// <summary>Runs several frames per Update with audio muted (fast loading).</summary>
        public bool Warp
        {
            get => Bridge != null && Bridge.Warp;
            set { if (Bridge != null) Bridge.Warp = value; }
        }

        /// <summary>The text on the C64 screen (25 lines).</summary>
        public string ScreenText => Bridge != null ? Bridge.ScreenText : "";

        private void Awake()
        {
            try
            {
                Bridge = C64Bridge.Create(
                    LoadRom(basicRom, basicFileName, "BASIC"),
                    LoadRom(kernalRom, kernalFileName, "KERNAL"),
                    LoadRom(chargenRom, chargenFileName, "character"),
                    TryLoadRom(driveRom, driveFileName),
                    AudioSettings.outputSampleRate);
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
                enabled = false;
                return;
            }
            Bridge.Command += OnCommand;

            Texture = new Texture2D(Bridge.FrameWidth, Bridge.FrameHeight, TextureFormat.RGBA32, false)
            {
                name = "C64 frame",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _rgba = new byte[Bridge.FrameWidth * Bridge.FrameHeight * 4];
            if (targetRenderer != null)
                targetRenderer.material.mainTexture = Texture;
#if TEDD_COMMODORE_UGUI
            if (targetImage != null)
                targetImage.texture = Texture;
#endif
            BuildKeyTable();

            if (startupMedia != null)
            {
                string name = string.IsNullOrEmpty(startupMediaFileName) ? startupMedia.name : startupMediaFileName;
                AttachMedia(startupMedia.bytes, name, autostartMedia);
            }
        }

        private void Start()
        {
            // OnAudioFilterRead acts as the sound source: the AudioSource needs no clip, it only has to play.
            var source = GetComponent<AudioSource>();
            source.loop = true;
            if (!source.isPlaying)
                source.Play();
            _audioEnabled = Bridge != null;
        }

        private void OnEnable()
        {
            _audioEnabled = Bridge != null;
        }

        private void OnDisable()
        {
            _audioEnabled = false;
            if (Bridge != null)
                Bridge.ReleaseAll();
            _heldKeys = 0;
        }

        private void OnDestroy()
        {
            _audioEnabled = false;
            if (Texture != null)
                Destroy(Texture);
        }

        private void Update()
        {
            if (Bridge == null)
                return;
            if (captureKeyboard)
                PollKeyboard();
            double dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            if (Bridge.Update(dt))
            {
                Bridge.CopyFrameRgba(_rgba, flipVertically);
                Texture.LoadRawTextureData(_rgba);
                Texture.Apply(false, false);
            }
        }

        // Runs on the audio thread. C64Bridge.ReadAudio is designed for exactly this callback.
        private void OnAudioFilterRead(float[] data, int channels)
        {
            var bridge = Bridge;
            if (_audioEnabled && bridge != null)
                bridge.ReadAudio(data, channels);
            else
                Array.Clear(data, 0, data.Length);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus && releaseInputOnFocusLoss && Bridge != null)
            {
                Bridge.ReleaseAll();
                _heldKeys = 0;
            }
        }

        #region Public API

        /// <summary>Attaches a .d64/.t64/.prg/.crt image (type detected from the file name's extension or the content).</summary>
        public void AttachMedia(byte[] data, string fileName, bool autostart = true)
        {
            if (Bridge != null)
                Bridge.AttachMedia(data, fileName, autostart);
        }

        /// <summary>Attaches a media file from disk.</summary>
        public void AttachMediaFile(string path, bool autostart = true)
        {
            AttachMedia(File.ReadAllBytes(path), Path.GetFileName(path), autostart);
        }

        public void EjectDisk()
        {
            if (Bridge != null) Bridge.EjectDisk();
        }

        public void DetachCartridge()
        {
            if (Bridge != null) Bridge.DetachCartridge();
        }

        /// <summary>Resets the C64 (hard = power cycle).</summary>
        public void Reset(bool hard = false)
        {
            if (Bridge != null) Bridge.Reset(hard);
        }

        /// <summary>Types text through the KERNAL keyboard buffer ('\n' = RETURN).</summary>
        public void TypeText(string text)
        {
            if (Bridge != null) Bridge.TypeText(text);
        }

        /// <summary>Drives a joystick input directly (game pads, on-screen controls).</summary>
        public void SetJoystick(int port, JoystickInput input, bool pressed)
        {
            if (Bridge != null) Bridge.SetJoystick(port, input, pressed);
        }

        #endregion

        #region Keyboard

        private void BuildKeyTable()
        {
            var codes = new List<KeyCode>();
            var web = new List<string>();
            var seen = new HashSet<int>();
            foreach (KeyCode keyCode in Enum.GetValues(typeof(KeyCode)))
            {
                if (!seen.Add((int)keyCode))
                    continue; // aliases such as LeftApple/LeftCommand share a value
                string code = UnityKeyCodes.ToWebCode(keyCode.ToString());
                if (code == null)
                    continue;
                codes.Add(keyCode);
                web.Add(code);
            }
            _keyCodes = codes.ToArray();
            _webCodes = web.ToArray();
        }

        private void PollKeyboard()
        {
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            if (!Input.anyKey && _heldKeys == 0)
                return;
            for (int i = 0; i < _keyCodes.Length; i++)
            {
                KeyCode keyCode = _keyCodes[i];
                if (Input.GetKeyDown(keyCode))
                {
                    Bridge.KeyDown(_webCodes[i]);
                    _heldKeys++;
                }
                if (Input.GetKeyUp(keyCode))
                {
                    Bridge.KeyUp(_webCodes[i]);
                    if (_heldKeys > 0) _heldKeys--;
                }
            }
#endif
        }

        #endregion

        private void OnCommand(SystemCommand command)
        {
            if (handleSystemCommands)
            {
                switch (command)
                {
                    case SystemCommand.Reset: Bridge.Reset(false); break;
                    case SystemCommand.HardReset: Bridge.Reset(true); break;
                    case SystemCommand.Pause: Bridge.Paused = !Bridge.Paused; break;
                    case SystemCommand.Warp: Bridge.Warp = !Bridge.Warp; break;
                }
            }
            var handler = Command;
            if (handler != null)
                handler(command);
        }

        private byte[] LoadRom(TextAsset asset, string fileName, string what)
        {
            byte[] data = TryLoadRom(asset, fileName);
            if (data == null)
                throw new FileNotFoundException("C64 " + what + " ROM not found: assign a TextAsset or place " + fileName +
                                                " in Assets/StreamingAssets/" + streamingAssetsFolder);
            return data;
        }

        private byte[] TryLoadRom(TextAsset asset, string fileName)
        {
            if (asset != null)
                return asset.bytes;
            if (string.IsNullOrEmpty(fileName))
                return null;
            string path = Path.Combine(Application.streamingAssetsPath, streamingAssetsFolder ?? "", fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }
}
#endif
