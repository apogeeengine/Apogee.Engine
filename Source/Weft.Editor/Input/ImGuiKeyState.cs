using Weft.DevTools;
using Weft.Native.CImGui;

namespace Weft.Editor.Input;

/// <summary>The keyboard as ImGui sees it this frame. On macOS ImGui reports the Cmd key as Ctrl, which is why <see cref="KeyMods.Primary"/> is read from the Ctrl keys everywhere.</summary>
public sealed class ImGuiKeyState(ImGuiHost host) : IKeyState
{
    /// <inheritdoc />
    public bool IsPressed(ImGuiKey key) => Gui.KeyPressed(key);

    /// <inheritdoc />
    public bool IsDown(ImGuiKey key) => Gui.KeyDown(key);

    /// <inheritdoc />
    public bool KeyboardCaptured => host.WantsKeyboard;
}
