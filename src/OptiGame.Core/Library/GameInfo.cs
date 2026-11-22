namespace OptiGame.Core.Library;

public enum GameEngine { Unknown, Unreal, Unity, Source, Source2, Godot, GameMaker, Other }

public sealed class GameInfo
{
    /// <summary>Stable id used for per-game slider choices, e.g. "steam:2615540" or "epic:Fortnite".</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string InstallDir { get; init; }
    public required string Store { get; init; }
    public string? SteamAppId { get; init; }
    public GameEngine Engine { get; set; } = GameEngine.Unknown;
    /// <summary>Engine-specific detail: Unreal project name, Unity "Company\Product", Source mod folder.</summary>
    public string? EngineDetail { get; set; }
    /// <summary>Every launcher or list that reported this install folder ("Steam", "GOG", "Added by you").</summary>
    public List<string> Sources { get; set; } = new();
    public string? GogId { get; init; }

    public string SourcesText => Sources.Count > 0 ? string.Join(", ", Sources) : Store;

    public override string ToString() => $"{Name} [{Store}] ({Engine})";
}
