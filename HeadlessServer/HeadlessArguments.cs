using System;
using System.Collections.Generic;

namespace HeadlessServer;

/// <summary>
/// The command line's two readers apart. Configuration keys (<c>--Section:Key=value</c> or
/// <c>--Section:Key value</c>) go to the configuration only; everything else goes to
/// CommandLineParser (<see cref="RunOptions"/>), which refuses an option it does not know. Before
/// they were split, a configuration key on the command line failed the parse.
/// </summary>
public static class HeadlessArguments
{
    /// <summary>The arguments <see cref="RunOptions"/> are parsed from: all but configuration keys and their values.</summary>
    public static string[] ForParser(string[] args)
    {
        List<string> kept = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (IsConfigurationKey(args[i]))
            {
                if (TakesNextAsValue(args, i))
                {
                    i++;
                }
                continue;
            }

            kept.Add(args[i]);
        }

        return [.. kept];
    }

    /// <summary>The configuration keys and their values, for the configuration's command-line source.</summary>
    public static string[] ForConfiguration(string[] args)
    {
        List<string> keys = [];
        for (int i = 0; i < args.Length; i++)
        {
            if (!IsConfigurationKey(args[i]))
            {
                continue;
            }

            keys.Add(args[i]);
            if (TakesNextAsValue(args, i))
            {
                keys.Add(args[++i]);
            }
        }

        return [.. keys];
    }

    /// <summary><c>--Section:Key</c>, with or without <c>=value</c>: a <c>:</c> in the name marks it.</summary>
    public static bool IsConfigurationKey(string arg)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        int equals = arg.IndexOf('=');
        string name = equals < 0 ? arg[2..] : arg[2..equals];
        return name.Contains(':') && !name.StartsWith(':') && !name.EndsWith(':');
    }

    // The next argument is the key's value unless it is an option: a negative number is a value
    // (--Navmesh:Query:EdgeMargin -1), which was dropped, or took the next key as the value (known issue 77).
    private static bool TakesNextAsValue(string[] args, int i)
    {
        return !args[i].Contains('=') && i + 1 < args.Length && (!args[i + 1].StartsWith('-') || IsNegativeNumber(args[i + 1]));
    }

    private static bool IsNegativeNumber(string arg)
    {
        return arg.Length > 1 && arg[0] == '-' &&
               (char.IsAsciiDigit(arg[1]) || arg[1] == '.' && arg.Length > 2 && char.IsAsciiDigit(arg[2]));
    }
}
