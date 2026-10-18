// purpose: blocks game input and frees the cursor while a mod text panel is open, hands both back on close, and reads
//          the panel's Enter/Esc once per press
// owns:    InputBlocker
// deps:    IModLog, interop Sons.Input (InputActionMapState, InputCursorState), UnityEngine (Input, Event, Cursor)
// caution: I2 - switches only the game's local input state. Blocked = the game's own InputActionMapState (Console) and
//          InputCursorState on a private object set active; inactive = the game restores its own map and cursor.
//          Release waits for Enter/Esc to be up so the closing key never reaches the game. IsAnyHeld spans every
//          instance, so two panels never hold input at once

using System;
using System.Collections.Generic;

using Il2CppInterop.Runtime;

using Sons.Input;
using TheForest.Utils;

using UnityEngine;

using ShelfTransfer.Core.Logging;

using LogLevel = ShelfTransfer.Core.Logging.LogLevel;
using UnityObject = UnityEngine.Object;

namespace ShelfTransfer.Plugin.Ui;

public sealed class InputBlocker
{
    // RedLoader's SUI panels use 100 for the same component; above the game's world cursor state.
    private const int _cursorPriority = 100;

    // Two frames lets the game's cursor state settle (it may apply in LateUpdate) before the restore check reads it.
    private const int _restoreCheckDelayFrames = 2;

    private static int _heldCount;

    private readonly string _objectName;
    private readonly string _logEvent;

    private GameObject? _block;
    private bool _isHeld;
    private int _restoreCheckFrame = -1;
    private string _closeReason = string.Empty;
    private CursorLockMode _cursorBefore;
    private bool _wasCursorVisible;
    private KeyCode _pressedPanelKey = KeyCode.None;

    public InputBlocker(string objectName, string logEvent)
    {
        _objectName = objectName;
        _logEvent = logEvent;
    }

    /// <summary>True while any panel has input blocked or is still waiting to release it.</summary>
    public static bool IsAnyHeld => _heldCount > 0;

    public bool IsReleasing { get; private set; }

    public void Block()
    {
        _cursorBefore = Cursor.lockState;
        _wasCursorVisible = Cursor.visible;
        IsReleasing = false;
        _restoreCheckFrame = -1;
        _pressedPanelKey = KeyCode.None;
        if (_block == null)
        {
            _block = CreateBlock(_objectName);
        }

        _block.SetActive(true);
        SetHeld(true);
    }

    /// <summary>Gives input back once Enter/Esc are up; the restore check follows two frames later. No-op unless held.</summary>
    public void BeginRelease(string reason)
    {
        if (!_isHeld || IsReleasing)
        {
            return;
        }

        _closeReason = reason;
        IsReleasing = true;
    }

    /// <summary>Called every Update by the owning panel, open or closed.</summary>
    public void Tick(IModLog log)
    {
        if (IsReleasing)
        {
            TryFinishRelease();
        }
        else if (_restoreCheckFrame >= 0 && Time.frameCount >= _restoreCheckFrame)
        {
            WriteRestoreCheck(log);
        }
    }

    /// <summary>Immediate release with no key wait and no restore check: for a halted or destroyed panel.</summary>
    public void ReleaseNow()
    {
        IsReleasing = false;
        if (_block != null)
        {
            _block.SetActive(false);
        }

        SetHeld(false);
    }

    /// <summary>Escape, Return, KeypadEnter pressed this frame (Update path), else None.</summary>
    public KeyCode ReadPanelKey()
    {
        KeyCode key = Input.GetKeyDown(KeyCode.Escape) ? KeyCode.Escape
            : Input.GetKeyDown(KeyCode.Return) ? KeyCode.Return
            : Input.GetKeyDown(KeyCode.KeypadEnter) ? KeyCode.KeypadEnter
            : KeyCode.None;
        if (key != KeyCode.None)
        {
            _pressedPanelKey = key;
        }

        return key;
    }

    /// <summary>
    /// The same keys from the current IMGUI event (OnGUI path), once per press: Input.GetKeyDown misses Esc while input
    /// is blocked or a text field has focus (card 54). A press the Update path already took, and OS key repeat, give None.
    /// </summary>
    public KeyCode ReadPanelKeyEvent()
    {
        Event current = Event.current;
        if (current == null || !IsPanelKey(current.keyCode))
        {
            return KeyCode.None;
        }

        KeyCode key = current.keyCode;
        EventType type = current.type;
        if (type == EventType.KeyUp)
        {
            if (key == _pressedPanelKey)
            {
                _pressedPanelKey = KeyCode.None;
            }

            return KeyCode.None;
        }

        if (type != EventType.KeyDown)
        {
            return KeyCode.None;
        }

        // Used so the focused text field never sees Enter/Esc.
        current.Use();
        if (key == _pressedPanelKey)
        {
            return KeyCode.None;
        }

        _pressedPanelKey = key;
        return key;
    }

    private static bool IsPanelKey(KeyCode key) => key is KeyCode.Escape or KeyCode.Return or KeyCode.KeypadEnter;

    private void SetHeld(bool isHeld)
    {
        if (isHeld == _isHeld)
        {
            return;
        }

        _isHeld = isHeld;
        _heldCount += isHeld ? 1 : -1;
    }

    private void TryFinishRelease()
    {
        if (Input.GetKey(KeyCode.Escape) || Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter))
        {
            return;
        }

        ReleaseNow();
        _restoreCheckFrame = Time.frameCount + _restoreCheckDelayFrames;
    }

    // Evidence for "no stuck cursor": after an in-world close the cursor must be back where it was at open.
    private void WriteRestoreCheck(IModLog log)
    {
        _restoreCheckFrame = -1;
        CursorLockMode cursor = Cursor.lockState;
        bool isVisible = Cursor.visible;
        bool isRestored = cursor == _cursorBefore && isVisible == _wasCursorVisible;
        // Leaving the world legitimately frees the cursor for the menu, so only an in-world close must match.
        bool shouldMatch = _closeReason is not ("world_left" or "shelf_gone");
        log.Write(isRestored || !shouldMatch ? LogLevel.Info : LogLevel.Warn, _logEvent, new KeyValuePair<string, object?>[]
        {
            new("action", "input"),
            new("outcome", isRestored ? "restored" : "changed"),
            new("reason", _closeReason),
            new("cursor", cursor.ToString()),
            new("visible", isVisible),
            new("cursor_before", _cursorBefore.ToString()),
            new("visible_before", _wasCursorVisible),
        });
    }

    // Built inactive so both components hold their settings before OnEnable; SetActive(true) is then the game's own
    // "UI open" signal (map state + hardware cursor), and SetActive(false) lets the game restore what it had before.
    private static GameObject CreateBlock(string name)
    {
        GameObject block = new(name);
        block.SetActive(false);
        UnityObject.DontDestroyOnLoad(block);

        InputActionMapState map = AddGameComponent<InputActionMapState>(block);
        map._applyState = InputState.Console;
        InputCursorState cursor = AddGameComponent<InputCursorState>(block);
        cursor._enabled = true;
        cursor._hardwareCursor = true;
        cursor._priority = _cursorPriority;
        return block;
    }

    // Non-generic AddComponent plus TryCast: the generic overload depends on an inflated method existing in the game.
    private static T AddGameComponent<T>(GameObject target) where T : Component
    {
        Component added = target.AddComponent(Il2CppType.Of<T>());
        return added.TryCast<T>() ?? throw new InvalidOperationException($"AddComponent did not return {typeof(T).Name}");
    }
}
