namespace SCU.Models;

public class StartupItem
{
    public int Index { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;

    public string ToolTipText =>
        $"Источник: {Source}\nКоманда: {Command}";

    public StartupItem() { }

    public StartupItem(int index, string source, string name, string command)
    {
        Index = index;
        Source = source;
        Name = name;
        Command = command;
    }
}