namespace Contactor.Boards.Advantech;

public sealed class Adam6000Options
{
    public const string SectionName = "Advantech";
    public string Host { get; set; } = "";
    public int Port { get; set; } = Adam6000Models.DefaultPort;
    public int PollSeconds { get; set; } = 2;
}
