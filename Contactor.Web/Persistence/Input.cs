namespace Contactor.Web.Persistence;

/// <summary>
/// The board's input. For later: the table exists, but nothing shows it yet.
/// </summary>
public sealed class Input
{
    public const int SingletonId = 1;
    public const int TextMaxLength = 40;

    public int Id { get; set; } = SingletonId;

    public string Name { get; set; } = "";

    public string ClosedText { get; set; } = "";

    public string OpenText { get; set; } = "";

    public bool NotifyOnOpen { get; set; }

    public bool NotifyOnClose { get; set; }
}
