namespace SCU.Models;

using SCU.Common;

public class StartupItem
{
    public int Index { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;

    public string ToolTipText =>
        L.T("Источник: {0}\nКоманда: {1}", Source, Command);

    public StartupItem() { }

    public StartupItem(int index, string source, string name, string command)
    {
        Index = index;
        Source = source;
        Name = name;
        Command = command;
    }
}