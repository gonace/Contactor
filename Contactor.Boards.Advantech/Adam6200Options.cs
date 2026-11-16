namespace Contactor.Boards.Advantech;

public sealed class Adam6200Options
{
    public const string SectionName = "AdvantechAdam6200";
    public string Host { get; set; } = "";
    public int Port { get; set; } = Adam6200Models.DefaultPort;
    public int PollSeconds { get; set; } = 2;
    public byte UnitId { get; set; } = Adam6200Models.DefaultUnitId;
}
