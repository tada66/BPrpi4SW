using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

internal class CameraCommandInterpreter
{
    private readonly libgphoto2Driver _driver;
    private readonly Dictionary<string, List<CommandInstruction>> _commands = new();
    private readonly Dictionary<string, string> _variables = new();
    private readonly Dictionary<string, bool> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    public CameraCommandInterpreter(libgphoto2Driver driver)
    {
        _driver = driver;
    }

    public bool HasCapability(string name) => _capabilities.TryGetValue(name, out var v) && v;

    /// <summary>
    /// Gets a variable defined in config (e.g., DEFINE IsoWidget iso).
    /// Returns the defaultValue if not defined.
    /// </summary>
    public string GetVariable(string name, string defaultValue = "") =>
        _variables.TryGetValue(name, out var v) ? v : defaultValue;

    public void Clear()
    {
        _commands.Clear();
        _variables.Clear();
        _capabilities.Clear();
        // Defaults
        _capabilities["Configuration"] = true;
        _capabilities["ImageCapture"] = true;
    }

    public void Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Logger.Notice($"Command file not found at {filePath}");
            return;
        }

        var lines = File.ReadAllLines(filePath);
        string? currentCommand = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

            if (trimmed.StartsWith("DEFINE"))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    _variables[parts[1]] = parts[2];
                }
                continue;
            }

            if (trimmed.StartsWith("CAPABILITY"))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var key = parts[1];
                    var val = true;
                    if (parts.Length >= 3) bool.TryParse(parts[2], out val);
                    _capabilities[key] = val;
                }
                continue;
            }

            if (trimmed.StartsWith("-") && !trimmed.StartsWith("--"))
            {
                currentCommand = trimmed.Substring(1);
                _commands[currentCommand] = new List<CommandInstruction>();
            }
            else if (currentCommand != null)
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var type = parts[0].ToUpperInvariant();
                    var args = parts.Skip(1).ToArray();
                    _commands[currentCommand].Add(new CommandInstruction(type, args));
                }
            }
        }
        Logger.Info($"Loaded {_commands.Count} commands, {_variables.Count} variables, {_capabilities.Count} capabilities from {filePath}");
    }

    public void Execute(string commandName, params object[] args)
    {
        if (!_commands.TryGetValue(commandName, out var instructions))
        {
            Logger.Warn($"Command '{commandName}' not found.");
            return;
        }

        foreach (var instr in instructions)
        {
            switch (instr.Type)
            {
                case "SET":
                    // SET widget TO value
                    // SET widget value
                    if (instr.Args.Length >= 2)
                    {
                        var widget = instr.Args[0];
                        var val = instr.Args.Length >= 3 && instr.Args[1].Equals("TO", StringComparison.OrdinalIgnoreCase) 
                            ? instr.Args[2] 
                            : instr.Args[1];
                        
                        val = FormatArg(val, args);
                        _driver.SetWidgetValueByPath(widget, val);
                    }
                    break;
                case "TOGGLE":
                    if (instr.Args.Length >= 1)
                    {
                        var widget = instr.Args[0];
                        ToggleSetting(widget);
                    }
                    break;
                case "WAIT":
                    if (instr.Args.Length >= 1)
                    {
                        var val = FormatArg(instr.Args[0], args);
                        if (int.TryParse(val, out int ms))
                        {
                            Thread.Sleep(ms);
                        }
                    }
                    break;
                case "EXECUTE":
                     if (instr.Args.Length >= 1)
                     {
                         Execute(instr.Args[0], args);
                     }
                     break;
            }
        }
    }

    private string FormatArg(string val, object[] args)
    {
        try
        {
            if (val.Contains("{") && val.Contains("}"))
            {
                return string.Format(val, args);
            }
        }
        catch (FormatException)
        {
            // Ignore format errors, return original
        }
        return val;
    }

    private void ToggleSetting(string settingPath) {
        int wait = 50;
        if (_variables.TryGetValue("TOGGLE_WAIT", out var val) && int.TryParse(val, out int parsed))
        {
            wait = parsed;
        }
        _driver.SetWidgetValueByPath(settingPath, "1");
        Thread.Sleep(wait);
        _driver.SetWidgetValueByPath(settingPath, "0");
    }

    private record CommandInstruction(string Type, string[] Args);
}
