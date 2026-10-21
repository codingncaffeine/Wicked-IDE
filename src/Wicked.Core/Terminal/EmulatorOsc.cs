using System.Globalization;
using System.Text;

namespace Wicked.Core.Terminal;

// Operating system commands (titles, colours, links, the clipboard, the shell's command markers) and device control strings.
public sealed partial class TerminalEmulator
{
    private CommandRecord? _current;
    private long _commandSequence;

    private void DispatchOsc(string text)
    {
        var semi = text.IndexOf(';', StringComparison.Ordinal);
        var code = semi < 0 ? text : text[..semi];
        var rest = semi < 0 ? "" : text[(semi + 1)..];
        if (!int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return;

        switch (number)
        {
            case 0:
            case 2:
                SetTitle(rest);
                break;
            case 4: PaletteCommand(rest); break;
            case 7: ReportDirectory(rest); break;
            case 8: Hyperlink(rest); break;
            case 9:
                if (rest.StartsWith("4;", StringComparison.Ordinal)) Progress(rest[2..]);
                break;
            case 10: DynamicColor(rest, 10); break;
            case 11: DynamicColor(rest, 11); break;
            case 12: DynamicColor(rest, 12); break;
            case 52: Clipboard(rest); break;
            case 104: ResetPalette(rest); break;
            case 110: _fgOverride = 0; break;
            case 111: _bgOverride = 0; break;
            case 112: _cursorOverride = 0; break;
            case 133: ShellMarker(rest, vsCode: false); break;
            case 633: ShellMarker(rest, vsCode: true); break;
            case 1337:
                if (rest.StartsWith("CurrentDir=", StringComparison.Ordinal)) SetDirectory(rest["CurrentDir=".Length..]);
                break;
        }
    }

    private void SetTitle(string title)
    {
        // Control characters in a title could be abused to fake the interface.
        var clean = new string(title.Where(c => !char.IsControl(c)).ToArray());
        if (clean.Length > 512) clean = clean[..512];
        _title = clean;
        TitleChanged?.Invoke(clean);
    }

    private void Progress(string rest)
    {
        var parts = rest.Split(';');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var state)) return;
        var value = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 100) : 0;
        ProgressChanged?.Invoke(Math.Clamp(state, 0, 4), value);
    }

    private void ReportDirectory(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "file") return;
        var path = Uri.UnescapeDataString(parsed.AbsolutePath);
        // A Windows path arrives as /C:/dir.
        if (path.Length > 2 && path[0] == '/' && path[2] == ':') path = path[1..];
        SetDirectory(path);
    }

    private void SetDirectory(string path)
    {
        if (path.Length == 0) return;
        WorkingDirectory = path;
        WorkingDirectoryChanged?.Invoke(path);
    }

    private void Hyperlink(string rest)
    {
        // params;uri: the params (id=...) group cells that belong to one link.
        var semi = rest.IndexOf(';', StringComparison.Ordinal);
        if (semi < 0) return;
        var uri = rest[(semi + 1)..];
        if (uri.Length == 0)
        {
            _link = 0;
            return;
        }

        var id = "";
        foreach (var part in rest[..semi].Split(':'))
        {
            if (part.StartsWith("id=", StringComparison.Ordinal)) id = part[3..];
        }

        _link = Links.Add(uri, id);
    }

    private void Clipboard(string rest)
    {
        if (!_options.AllowClipboardWrite) return;
        var semi = rest.IndexOf(';', StringComparison.Ordinal);
        if (semi < 0) return;
        var data = rest[(semi + 1)..];
        // A request to read the clipboard ("?") is never answered: it would hand its contents to whatever is running.
        if (data == "?" || data.Length == 0) return;
        try
        {
            ClipboardWrite?.Invoke(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
        }
        catch (FormatException)
        {
        }
    }

    // ── Colours ──────────────────────────────────────────────────────────────────────────────────

    private uint PaletteColor(int index) => _paletteOverrides is { } o && o[index] != 0 ? o[index] : _defaultPalette[index];

    private void PaletteCommand(string rest)
    {
        var parts = rest.Split(';');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index > 255) continue;
            if (parts[i + 1] == "?") Respond($"\u001b]4;{index};{FormatColor(PaletteColor(index))}\u001b\\");
            else if (ParseColor(parts[i + 1]) is { } color)
            {
                _paletteOverrides = _paletteOverrides is null ? new uint[256] : (uint[])_paletteOverrides.Clone();
                _paletteOverrides[index] = color;
            }
        }
    }

    private void ResetPalette(string rest)
    {
        if (rest.Length == 0)
        {
            _paletteOverrides = null;
            return;
        }

        foreach (var part in rest.Split(';'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index > 255 || _paletteOverrides is null) continue;
            _paletteOverrides = (uint[])_paletteOverrides.Clone();
            _paletteOverrides[index] = 0;
        }
    }

    private void DynamicColor(string rest, int which)
    {
        var spec = rest.Split(';')[0];
        var current = which switch
        {
            10 => _fgOverride != 0 ? _fgOverride : _options.Foreground,
            11 => _bgOverride != 0 ? _bgOverride : _options.Background,
            _ => _cursorOverride != 0 ? _cursorOverride : _options.Foreground,
        };
        if (spec == "?")
        {
            Respond($"\u001b]{which};{FormatColor(current)}\u001b\\");
            return;
        }

        if (ParseColor(spec) is not { } color) return;
        if (which == 10) _fgOverride = color;
        else if (which == 11) _bgOverride = color;
        else _cursorOverride = color;
    }

    /// <summary>The xterm form of a colour: <c>rgb:rrrr/gggg/bbbb</c>, 16 bits a channel.</summary>
    private static string FormatColor(uint argb)
    {
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
    }

    /// <summary>Reads <c>rgb:r/g/b</c> (1 to 4 hex digits a channel) or <c>#rrggbb</c>; null when it is neither.</summary>
    internal static uint? ParseColor(string spec)
    {
        if (spec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = spec[4..].Split('/'); // not a path
            if (parts.Length != 3) return null;
            var channels = new uint[3];
            for (var i = 0; i < 3; i++)
            {
                if (parts[i].Length is < 1 or > 4 || !uint.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
                // Scale whatever width was given to 8 bits.
                var max = (1u << (4 * parts[i].Length)) - 1;
                channels[i] = (uint)Math.Round(v * 255.0 / max);
            }

            return 0xFF000000 | (channels[0] << 16) | (channels[1] << 8) | channels[2];
        }

        if (spec.Length == 7 && spec[0] == '#' && uint.TryParse(spec.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return 0xFF000000 | rgb;
        return null;
    }

    // ── The shell's markers (OSC 133 and VS Code's 633) ──────────────────────────────────────────

    private void ShellMarker(string rest, bool vsCode)
    {
        if (rest.Length == 0) return;
        var kind = rest[0];
        var argument = rest.Length > 2 && rest[1] == ';' ? rest[2..] : "";
        switch (kind)
        {
            case 'A': PromptStart(argument); break;
            case 'B': PromptEnd(); break;
            case 'C': OutputStart(); break;
            case 'D': CommandEnd(argument); break;
            case 'E' when vsCode:
                if (_current is { Finished: false } running) running.CommandLine = UnescapeCommand(argument);
                break;
            case 'P' when vsCode:
                if (argument.StartsWith("Cwd=", StringComparison.Ordinal)) SetDirectory(argument[4..]);
                break;
        }
    }

    private static string UnescapeCommand(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal)) return text;
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                if (text[i + 1] == '\\')
                {
                    result.Append('\\');
                    i++;
                    continue;
                }

                if (text[i + 1] == 'x' && i + 3 < text.Length && byte.TryParse(text.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    result.Append((char)b);
                    i += 3;
                    continue;
                }
            }

            result.Append(text[i]);
        }

        return result.ToString();
    }

    private void PromptStart(string options)
    {
        // A continuation prompt (k=c, k=s) belongs to the command being typed.
        if (options.Contains("k=c", StringComparison.Ordinal) || options.Contains("k=s", StringComparison.Ordinal)) return;
        if (_alt) return;

        // A command still marked as running when the next prompt appears ended without saying how (the shell could not tell).
        if (_current is { OutputStarted: true, Finished: false } unfinished)
        {
            unfinished.Finished = true;
            CommandFinished?.Invoke(unfinished);
        }

        // A prompt that was drawn and never used (Enter on an empty line) leaves no mark behind.
        if (_current is { OutputStarted: false } abandoned && abandoned.PromptLine is { } line && ReferenceEquals(line.Command, abandoned)) line.Command = null;

        var record = new CommandRecord { Sequence = ++_commandSequence, Directory = WorkingDirectory };
        var row = _rows[_row];
        record.PromptLine = row;
        row.Command = record;
        _current = record;
        PromptStarted?.Invoke(record);
    }

    private void PromptEnd()
    {
        if (_current is not { OutputStarted: false } record || _alt) return;
        record.InputLine = _rows[_row];
        record.InputColumn = _col;
    }

    private void OutputStart()
    {
        if (_current is not { OutputStarted: false } record || _alt) return;
        record.OutputStarted = true;
        record.OutputLine = _rows[_row];
        record.CommandLine ??= ReadCommandText(record);
        CommandStarted?.Invoke(record);
    }

    private void CommandEnd(string argument)
    {
        if (_current is not { OutputStarted: true, Finished: false } record) return;
        record.Finished = true;
        if (int.TryParse(argument.Split(';')[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)) record.ExitCode = code;
        CommandFinished?.Invoke(record);
    }

    /// <summary>The text between the end of the prompt and the cursor: what was typed, wrapped rows joined.</summary>
    private string? ReadCommandText(CommandRecord record)
    {
        if (record.InputLine is not { } input) return null;
        var start = Array.IndexOf(_rows, input);
        if (start < 0) return null;
        var text = new StringBuilder();
        for (var r = start; r <= _row && r < _rowCount; r++)
        {
            var cells = _rows[r].Cells;
            var from = r == start ? record.InputColumn : 0;
            var to = r == _row ? Math.Min(_col, cells.Length) : cells.Length;
            var line = RowText(cells, from, to);
            text.Append(_rows[r].Wrapped ? line : line.TrimEnd());
            if (!_rows[r].Wrapped && r < _row) text.Append('\n');
        }

        var result = text.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    // ── Device control strings ───────────────────────────────────────────────────────────────────

    private void DispatchDcs()
    {
        // DECRQSS: a program asking how the terminal is set up; answered for the few settings it reads back.
        if (_intermediate == '$' && _dcsFinal == 'q')
        {
            var request = Encoding.UTF8.GetString(_string, 0, _stringLength);
            switch (request)
            {
                case "m": Respond($"\u001bP1$r{SgrState()}m\u001b\\"); break;
                case "r": Respond($"\u001bP1$r{_top + 1};{_bottom + 1}r\u001b\\"); break;
                case " q":
                    var style = (_modes.CursorShape, _modes.CursorBlink) switch
                    {
                        (CursorShape.Block, false) => 2,
                        (CursorShape.Underline, true) => 3,
                        (CursorShape.Underline, false) => 4,
                        (CursorShape.Bar, true) => 5,
                        (CursorShape.Bar, false) => 6,
                        _ => 1,
                    };
                    Respond($"\u001bP1$r{style} q\u001b\\");
                    break;
                default: Respond("\u001bP0$r\u001b\\"); break;
            }
        }
    }

    private string SgrState()
    {
        var parts = new List<string> { "0" };
        if ((_flags & CellFlags.Bold) != 0) parts.Add("1");
        if ((_flags & CellFlags.Dim) != 0) parts.Add("2");
        if ((_flags & CellFlags.Italic) != 0) parts.Add("3");
        if ((_flags & CellFlags.Underline) != 0) parts.Add("4");
        if ((_flags & CellFlags.Blink) != 0) parts.Add("5");
        if ((_flags & CellFlags.Inverse) != 0) parts.Add("7");
        if ((_flags & CellFlags.Invisible) != 0) parts.Add("8");
        if ((_flags & CellFlags.Strike) != 0) parts.Add("9");
        AddColor(parts, _fg, 30, 90, 38);
        AddColor(parts, _bg, 40, 100, 48);
        return string.Join(';', parts);
    }

    private static void AddColor(List<string> parts, uint color, int low, int high, int extended)
    {
        if (CellColor.IsPalette(color))
        {
            var i = CellColor.PaletteIndex(color);
            if (i < 8) parts.Add((low + i).ToString(CultureInfo.InvariantCulture));
            else if (i < 16) parts.Add((high + i - 8).ToString(CultureInfo.InvariantCulture));
            else parts.Add($"{extended};5;{i}");
        }
        else if (CellColor.IsRgb(color)) parts.Add($"{extended};2;{(color >> 16) & 0xFF};{(color >> 8) & 0xFF};{color & 0xFF}");
    }
}
