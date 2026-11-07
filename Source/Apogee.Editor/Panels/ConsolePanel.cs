using System.Numerics;
using Apogee.Core.Logging;
using Apogee.DevTools;
using Apogee.Native.CImGui;

namespace Apogee.Editor.Panels;

/// <summary>The log: the engine's and the game's messages, filtered by level, scrolled to the newest.</summary>
public sealed class ConsolePanel
{
    private int _level = (int)LogLevel.Information;
    private long _seen;

    public void Draw(EditorContext ctx)
    {
        if (!Ui.Begin("Console"))
        {
            Ui.End();
            return;
        }

        Ui.Combo("Level", ref _level, "Trace", "Debug", "Information", "Warning", "Error");
        Gui.SameLine();
        if (Gui.SmallButton("Clear"))
        {
            ctx.Log.Clear();
        }

        Ui.Separator();
        if (Gui.BeginChild("##log"))
        {
            foreach (LogEntry entry in ctx.Log.Entries((LogLevel)_level))
            {
                Vector4 color = entry.Level switch
                {
                    LogLevel.Error => new Vector4(1f, 0.45f, 0.4f, 1f),
                    LogLevel.Warning => new Vector4(1f, 0.85f, 0.4f, 1f),
                    LogLevel.Debug or LogLevel.Trace => new Vector4(0.6f, 0.6f, 0.6f, 1f),
                    _ => new Vector4(0.9f, 0.9f, 0.9f, 1f),
                };
                Ui.TextColored(color, $"{entry.Time:HH:mm:ss} [{entry.Category}] {entry.Message}");
            }

            if (_seen != ctx.Log.Version)
            {
                _seen = ctx.Log.Version;
                ImGui.SetScrollHereY(1f);
            }
        }

        Gui.EndChild();
        Ui.End();
    }
}
