// purpose: the transfer prompt: the alt transfer key opens a small IMGUI text box that runs the `transfer` commands (list,
//          <count> <index or label>, cancel) and shows their output, because the game's debug console is not reachable
// owns:    TransferPromptUi
// deps:    TransferCommand (CaptureAim, ReleaseAim, RunCaptured, GetRunningText, IsCapturedShelfGone, GetShelfName),
//          InputBlocker, Styles, ModConfig (TransferAltKey), LabelsFeature.MaxNameLength, IModLog, UnityEngine IMGUI
// caution: I1/I2 - never touches the gateway; a start goes through TransferCommand to the runner (TransferDriver). The aimed
//          slot is captured on open, not on Enter. Update and OnGUI are P2 boundaries: a throw logs error event=ui.prompt,
//          gives input back at once and halts the prompt for the session

using System;
using System.Collections.Generic;

using Il2CppInterop.Runtime.Attributes;

using UnityEngine;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Plugin.Commands;
using ShelfTransfer.Plugin.Config;
using ShelfTransfer.Plugin.Labels;

using LogLevel = ShelfTransfer.Core.Logging.LogLevel;

namespace ShelfTransfer.Plugin.Ui;

public sealed class TransferPromptUi : MonoBehaviour
{
    private const int _maxOutputLines = 40;
    // Room for a count, a space and the longest label.
    private const int _maxInputLength = LabelsFeature.MaxNameLength + 16;
    private const float _toastSeconds = 5f;
    private const string _inputControl = "ShelfTransfer.TransferInput";
    private const string _inputBlockName = "ShelfTransfer.TransferPromptInput";

    private const float _panelWidth = 560f;
    private const float _panelTopFraction = 0.22f;
    private const float _padding = 16f;
    private const float _lineHeight = 22f;
    private const float _fieldHeight = 28f;
    private const float _gap = 6f;
    private const float _outputAreaHeight = 240f;
    private const float _scrollbarWidth = 20f;
    private const float _buttonWidth = 90f;
    private const float _toastWidth = 460f;
    private const float _toastTop = 40f;

    private const string _titleText = "Transfer";
    private const string _hintText = "list  |  <count> <index or label>  |  cancel      Enter runs, Esc closes";
    private const string _runText = "Run";
    private const string _closeText = "Close";
    private const string _noAimText = "No aimed slot. Aim at a shelf slot that holds items, then press + to move them.";
    private const string _runningPrefix = "Running: ";
    private const string _cancelHintText = "Type cancel to stop after the current item.";
    private const string _inputEcho = "> ";

    private IModLog? _log;
    private ModConfig? _config;
    private TransferCommand? _command;
    private bool _isHalted;

    private readonly InputBlocker _inputBlocker = new(_inputBlockName, "ui.prompt");
    private readonly List<string> _output = new();

    private bool _isOpen;
    private string _input = string.Empty;
    private int _focusFrame = -1;
    private bool _isOutputDirty;
    private string _outputText = string.Empty;
    private float _outputHeight;
    private Vector2 _outputScroll;
    private GUIStyle? _wrapStyle;
    private Styles? _wrapStyleOwner;

    // Set by the driver's finish callback, which runs outside any catch, so it only stores; Update shows it.
    private string? _pendingResult;
    private string? _toast;
    private float _toastUntil;

    public TransferPromptUi(IntPtr ptr) : base(ptr) { }

    // Managed parameters cannot cross into Il2Cpp; hidden so ClassInjector does not try to expose it.
    [HideFromIl2Cpp]
    public void Init(IModLog log, ModConfig config, TransferCommand command)
    {
        _log = log;
        _config = config;
        _command = command;
    }

    public void Update()
    {
        if (_log is null || _config is null || _command is null || _isHalted)
        {
            return;
        }

        try
        {
            Tick(_log, _config, _command);
        }
        catch (Exception ex)
        {
            Halt(_log, ex);
        }
    }

    public void OnGUI()
    {
        if (_log is null || _command is null || _isHalted || (!_isOpen && _toast is null))
        {
            return;
        }

        try
        {
            Draw(_log, _command);
        }
        catch (Exception ex)
        {
            Halt(_log, ex);
        }
    }

    public void OnDestroy() => _inputBlocker.ReleaseNow();

    [HideFromIl2Cpp]
    private void Tick(IModLog log, ModConfig config, TransferCommand command)
    {
        _inputBlocker.Tick(log);
        ShowPendingResult();
        if (!_isOpen)
        {
            // Not while any panel holds input: in the label editor the key is a letter of the name, and while releasing
            // the cursor is still a panel's, so Block would record it as the state to restore.
            KeyCode key = InputBlocker.IsAnyHeld ? KeyCode.None : GetOpenKeyDown(config);
            if (key != KeyCode.None)
            {
                TryOpen(log, command, key);
            }

            return;
        }

        string? lostReason = GetLostReason(command);
        if (lostReason is not null)
        {
            Close(log, lostReason);
        }
        else
        {
            HandlePanelKey(log, command, _inputBlocker.ReadPanelKey());
        }
    }

    [HideFromIl2Cpp]
    private void TryOpen(IModLog log, TransferCommand command, KeyCode key)
    {
        if (Camera.main == null)
        {
            WriteNoOpen(log, "no_world");
            return;
        }

        // A free cursor means a game menu, inventory or chat owns the screen; opening over it would fight its input.
        CursorLockMode cursor = Cursor.lockState;
        if (cursor != CursorLockMode.Locked)
        {
            WriteNoOpen(log, "cursor_free", cursor.ToString());
            return;
        }

        AimTarget? aim = command.CaptureAim();
        string? running = command.GetRunningText();
        _output.Clear();
        _output.Add(aim is null
            ? _noAimText
            : $"Source: {aim.Count} of item {aim.Item.Value} on {command.GetShelfName(aim.Shelf)}");
        if (running is not null)
        {
            _output.Add(_runningPrefix + running);
            _output.Add(_cancelHintText);
        }

        _isOutputDirty = true;
        _input = string.Empty;
        _toast = null;
        // Next frame: focusing now would let this frame's key event type its character into the field.
        _focusFrame = Time.frameCount + 1;

        _inputBlocker.Block();
        _isOpen = true;
        log.Write(LogLevel.Info, "ui.prompt", new KeyValuePair<string, object?>[]
        {
            new("action", "open"),
            new("key", key.ToString()),
            new("shelf", aim?.Shelf.Key),
            new("slot", aim?.Slot.Index),
            new("item", aim?.Item.Value),
            new("count", aim?.Count),
            new("running", running is not null),
        });
    }

    [HideFromIl2Cpp]
    private static string? GetLostReason(TransferCommand command)
    {
        // Loading screens and the world teardown have no main camera (the same signal AimWatcher uses).
        if (Camera.main == null)
        {
            return "world_left";
        }

        return command.IsCapturedShelfGone ? "shelf_gone" : null;
    }

    [HideFromIl2Cpp]
    private void HandlePanelKey(IModLog log, TransferCommand command, KeyCode key)
    {
        if (key == KeyCode.Escape)
        {
            Close(log, "esc");
        }
        else if (key is KeyCode.Return or KeyCode.KeypadEnter)
        {
            Submit(log, command);
        }
    }

    [HideFromIl2Cpp]
    private void Submit(IModLog log, TransferCommand command)
    {
        // Re-checked here for the Run button and the OnGUI Enter, which run after this frame's Update check.
        string? lostReason = GetLostReason(command);
        if (lostReason is not null)
        {
            Close(log, lostReason);
            return;
        }

        string text = _input.Trim();
        _input = string.Empty;
        log.Write(LogLevel.Info, "ui.prompt", new KeyValuePair<string, object?>[]
        {
            new("action", "submit"),
            new("args", text),
        });

        AppendOutput(_inputEcho + text);
        List<string> lines = new();
        bool hasStarted = command.RunCaptured(text, lines, OnFinished);
        foreach (string line in lines)
        {
            AppendOutput(line);
        }

        if (hasStarted)
        {
            Close(log, "started");
        }
    }

    [HideFromIl2Cpp]
    private void Close(IModLog log, string reason)
    {
        log.Write(LogLevel.Info, "ui.prompt", new KeyValuePair<string, object?>[]
        {
            new("action", "close"),
            new("reason", reason),
        });
        ResetPrompt();
        _inputBlocker.BeginRelease(reason);
    }

    [HideFromIl2Cpp]
    private void ResetPrompt()
    {
        _isOpen = false;
        _input = string.Empty;
        _focusFrame = -1;
        _command?.ReleaseAim();
    }

    [HideFromIl2Cpp]
    private void Halt(IModLog log, Exception ex)
    {
        _isHalted = true;
        log.WriteException("ui.prompt", ex);
        ResetPrompt();
        _inputBlocker.ReleaseNow();
    }

    [HideFromIl2Cpp]
    private void OnFinished(string text) => _pendingResult = text;

    // A run started from the prompt closes it, so its result also shows as a toast when the prompt is shut.
    [HideFromIl2Cpp]
    private void ShowPendingResult()
    {
        string? result = _pendingResult;
        if (result is null)
        {
            return;
        }

        _pendingResult = null;
        AppendOutput(result);
        if (!_isOpen)
        {
            _toast = result;
            _toastUntil = Time.unscaledTime + _toastSeconds;
        }
    }

    [HideFromIl2Cpp]
    private void AppendOutput(string line)
    {
        _output.Add(line);
        if (_output.Count > _maxOutputLines)
        {
            _output.RemoveRange(0, _output.Count - _maxOutputLines);
        }

        _isOutputDirty = true;
    }

    [HideFromIl2Cpp]
    private void Draw(IModLog log, TransferCommand command)
    {
        Styles styles = Styles.Get();
        GUIStyle wrap = GetWrapStyle(styles);
        if (_isOpen)
        {
            // Esc never reaches Input.GetKeyDown while input is blocked (card 54); the IMGUI event does.
            HandlePanelKey(log, command, _inputBlocker.ReadPanelKeyEvent());
        }

        if (_isOpen)
        {
            DrawPrompt(log, command, styles, wrap);
            return;
        }

        if (_toast is null || Time.unscaledTime >= _toastUntil)
        {
            _toast = null;
            return;
        }

        Rect toast = new((Screen.width - _toastWidth) / 2f, _toastTop, _toastWidth, (2f * _lineHeight) + (2f * _gap));
        GUI.Box(toast, string.Empty, styles.Panel);
        GUI.Label(new Rect(toast.x + _gap, toast.y + _gap, toast.width - (2f * _gap), 2f * _lineHeight), _toast, wrap);
    }

    [HideFromIl2Cpp]
    private void DrawPrompt(IModLog log, TransferCommand command, Styles styles, GUIStyle wrap)
    {
        float panelHeight = (2f * _padding) + (2f * (_lineHeight + _gap)) + _outputAreaHeight + _gap
            + (2f * (_fieldHeight + _gap));
        Rect panel = new((Screen.width - _panelWidth) / 2f, Screen.height * _panelTopFraction, _panelWidth, panelHeight);
        GUI.Box(panel, string.Empty, styles.Panel);

        float x = panel.x + _padding;
        float width = panel.width - (2f * _padding);
        float y = panel.y + _padding;

        GUI.Label(new Rect(x, y, width, _lineHeight), _titleText, styles.Title);
        y += _lineHeight + _gap;
        GUI.Label(new Rect(x, y, width, _lineHeight), _hintText, styles.Muted);
        y += _lineHeight + _gap;

        DrawOutput(wrap, new Rect(x, y, width, _outputAreaHeight));
        y += _outputAreaHeight + _gap;

        GUI.SetNextControlName(_inputControl);
        _input = GUI.TextField(new Rect(x, y, width, _fieldHeight), _input, _maxInputLength, styles.TextField);
        if (_focusFrame >= 0 && Time.frameCount >= _focusFrame)
        {
            GUI.FocusControl(_inputControl);
            _focusFrame = -1;
        }

        y += _fieldHeight + _gap;
        float buttonsX = x + width - (2f * _buttonWidth) - _gap;
        if (GUI.Button(new Rect(buttonsX, y, _buttonWidth, _fieldHeight), _runText, styles.Button))
        {
            Submit(log, command);
        }
        else if (GUI.Button(new Rect(buttonsX + _buttonWidth + _gap, y, _buttonWidth, _fieldHeight), _closeText, styles.Button))
        {
            Close(log, "button");
        }
    }

    // OnGUI runs several times a frame; the text and its wrapped height are rebuilt only when the output changes.
    [HideFromIl2Cpp]
    private void DrawOutput(GUIStyle wrap, Rect area)
    {
        float viewWidth = area.width - _scrollbarWidth;
        if (_isOutputDirty)
        {
            _isOutputDirty = false;
            _outputText = string.Join("\n", _output);
            _outputHeight = wrap.CalcHeight(new GUIContent(_outputText), viewWidth);
            _outputScroll = new Vector2(0f, Math.Max(0f, _outputHeight - area.height));
        }

        Rect content = new(0f, 0f, viewWidth, Math.Max(_outputHeight, area.height));
        _outputScroll = GUI.BeginScrollView(area, _outputScroll, content);
        GUI.Label(content, _outputText, wrap);
        GUI.EndScrollView();
    }

    [HideFromIl2Cpp]
    private GUIStyle GetWrapStyle(Styles styles)
    {
        if (_wrapStyle is null || !ReferenceEquals(_wrapStyleOwner, styles))
        {
            _wrapStyle = new GUIStyle(styles.Body) { wordWrap = true };
            _wrapStyleOwner = styles;
        }

        return _wrapStyle;
    }

    // The alt key only: the transfer key opens the overlay (card 20), so each key opens exactly one UI. None disables it.
    private static KeyCode GetOpenKeyDown(ModConfig config)
    {
        KeyCode alternate = config.TransferAltKey.Value;
        return alternate != KeyCode.None && Input.GetKeyDown(alternate) ? alternate : KeyCode.None;
    }

    [HideFromIl2Cpp]
    private static void WriteNoOpen(IModLog log, string reason, string? cursor = null)
    {
        List<KeyValuePair<string, object?>> line = new()
        {
            new("action", "none"),
            new("reason", reason),
        };
        if (cursor is not null)
        {
            line.Add(new("cursor", cursor));
        }

        log.Write(LogLevel.Info, "ui.prompt", line);
    }
}
