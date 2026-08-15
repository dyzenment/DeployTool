namespace Dytools.DeployTool.Wizard;

/// <summary>Plain-Console input helpers shared by the scaffold flow and the editor.</summary>
internal static class Prompt
{
    /// <summary>Free text with a default; blank keeps the default.</summary>
    public static string Ask(string label, string def)
    {
        Console.Write($"{label} [{def}]: ");
        var s = Console.ReadLine();
        return string.IsNullOrWhiteSpace(s) ? def : s.Trim();
    }

    /// <summary>Required free text; re-asks until something is entered.</summary>
    public static string AskRequired(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var s = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            Console.WriteLine("  (required)");
        }
    }

    /// <summary>Optional free text; blank returns null.</summary>
    public static string? AskOptional(string label)
    {
        Console.Write($"{label} (optional): ");
        var s = Console.ReadLine();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>
    /// Edit an optional value shown with its current contents: blank keeps it, "-" clears it.
    /// </summary>
    public static string? EditOptional(string label, string? current)
    {
        Console.Write($"{label} [{current}] (blank=keep, - to clear): ");
        var s = Console.ReadLine();
        if (string.IsNullOrEmpty(s)) return current;
        s = s.Trim();
        return s == "-" ? null : s;
    }

    public static bool Confirm(string label, bool def)
    {
        Console.Write($"{label} [{(def ? "Y/n" : "y/N")}]: ");
        var s = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return def;
        return s is "y" or "yes";
    }

    public static int AskInt(string label, int def)
    {
        while (true)
        {
            Console.Write($"{label} [{def}]: ");
            var s = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(s)) return def;
            if (int.TryParse(s.Trim(), out var v)) return v;
            Console.WriteLine("  (enter a number)");
        }
    }

    public static T AskChoice<T>(string label, (T Value, string Text)[] options)
    {
        Console.WriteLine(label);
        for (var i = 0; i < options.Length; i++)
            Console.WriteLine($"    {i + 1}) {options[i].Text}");
        while (true)
        {
            Console.Write($"  Choose [1-{options.Length}]: ");
            var s = Console.ReadLine();
            if (int.TryParse(s?.Trim(), out var n) && n >= 1 && n <= options.Length)
                return options[n - 1].Value;
            Console.WriteLine("    (enter one of the numbers)");
        }
    }
}
