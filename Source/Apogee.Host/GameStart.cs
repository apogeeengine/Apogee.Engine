using Apogee.Content.Packs;

namespace Apogee.Host;

/// <summary>What <see cref="ScriptSession.StartGame(GameStart)"/> starts: a game folder and how to run it.</summary>
public sealed record GameStart
{
    /// <summary>The game's folder: <c>res://</c>, and where its <c>game.xml</c> and <c>data/*.pack</c> are.</summary>
    public required string Folder { get; init; }

    /// <summary>The folder of the player's files (<c>user://</c>). Null: the one <c>game.xml</c> names, or one named after the game folder when there is no <c>game.xml</c>.</summary>
    public string? UserFolder { get; init; }

    /// <summary>Import changed assets into <c>res://imported</c> and reload what changes.</summary>
    public bool Watch { get; init; }

    /// <summary>Open the audio device (false: a silent audio system).</summary>
    public bool RealAudio { get; init; }

    /// <summary>Start Steam (false: Steam is off).</summary>
    public bool RealSteam { get; init; }

    /// <summary>False makes everything a game has without running the packages' scripts (or loading the entry scene): what an editor edits before Play.</summary>
    public bool RunScripts { get; init; } = true;

    /// <summary>Scene text loaded into the world before the scripts run (what Play starts from). Null: the entry scene of <c>game.xml</c>, if it has one.</summary>
    public string? Scene { get; init; }

    /// <summary>The key of the game's packs. Null: the one in the environment (<c>APOGEE_PACK_KEY</c>).</summary>
    public PackKey? Key { get; init; }

    /// <summary>The game is its packs: the folder's loose files are not mounted as <c>res://</c>, only <c>data/*.pack</c> are. Not together with <see cref="Watch"/>.</summary>
    public bool PacksOnly { get; init; }
}
