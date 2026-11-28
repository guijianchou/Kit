// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;

namespace Kit.WingetLib;

internal sealed class WingetOutputBuffer
{
    internal const int MaximumCharacters = 16 * 1024;
    private const int TerminalColumns = 160;
    private const int TerminalRows = 40;
    internal static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private readonly LinkedList<OutputLine> _lines = new();
    private readonly StreamState _standardOutput = new();
    private readonly StreamState _standardError = new();
    private readonly bool _terminal;
    private readonly List<LinkedListNode<OutputLine>> _screen = new();
    private int _characters;
    private string _published = string.Empty;
    private TimeSpan _lastPublished;

    internal WingetOutputBuffer(bool terminal = false)
    {
        _terminal = terminal;
    }

    private enum EscapeState
    {
        None,
        Escape,
        EscapeIntermediate,
        ControlSequence,
        String,
        StringEscape,
    }

    internal void Append(ReadOnlySpan<char> text, bool standardError = false)
    {
        lock (_gate)
        {
            var state = standardError && !_terminal ? _standardError : _standardOutput;
            foreach (char character in text)
            {
                if (ConsumeEscape(state, character))
                {
                    continue;
                }

                if (_terminal)
                {
                    AppendTerminal(state, character);
                    if (_characters > MaximumCharacters + 4096)
                    {
                        Trim();
                    }

                    continue;
                }

                switch (character)
                {
                    case '\r':
                        state.CarriageReturn = true;
                        break;
                    case '\n':
                        CurrentLine(state).Value.Terminated = true;
                        state.Line = null;
                        state.CarriageReturn = false;
                        break;
                    case '\b':
                        if (state.Line is { Value.Text.Length: > 0 } line)
                        {
                            int length = line.Value.Text.Length;
                            int removed = char.IsLowSurrogate(line.Value.Text[length - 1]) && length > 1 && char.IsHighSurrogate(line.Value.Text[length - 2]) ? 2 : 1;
                            line.Value.Text.Length -= removed;
                            _characters -= removed;
                        }

                        break;
                    case '\t':
                        AppendCharacter(state, ' ');
                        AppendCharacter(state, ' ');
                        AppendCharacter(state, ' ');
                        AppendCharacter(state, ' ');
                        break;
                    default:
                        if (!char.IsControl(character) && character is not ('\u2028' or '\u2029'))
                        {
                            AppendCharacter(state, character);
                        }

                        break;
                }

                if (_characters > MaximumCharacters + 4096)
                {
                    Trim();
                }
            }

            Trim();
        }
    }

    internal string Snapshot()
    {
        lock (_gate)
        {
            return SnapshotCore();
        }
    }

    internal string? TakeChangedSnapshot(TimeSpan elapsed, bool force = false)
    {
        lock (_gate)
        {
            if (!force && elapsed - _lastPublished < PublishInterval)
            {
                return null;
            }

            string snapshot = SnapshotCore();
            if (string.Equals(snapshot, _published, StringComparison.Ordinal))
            {
                return null;
            }

            _published = snapshot;
            _lastPublished = elapsed;
            return snapshot;
        }
    }

    private static int TerminalWidth(Rune rune)
    {
        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        int value = rune.Value;
        return value is >= 0x1100 and <= 0x115f or 0x2329 or 0x232a
            or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3
            or >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe19
            or >= 0xfe30 and <= 0xfe6f or >= 0xff00 and <= 0xff60
            or >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1faff
            or >= 0x20000 and <= 0x3fffd ? 2 : 1;
    }

    private bool ConsumeEscape(StreamState state, char character)
    {
        if (state.Escape != EscapeState.None)
        {
            switch (state.Escape)
            {
                case EscapeState.Escape:
                    state.Control.Clear();
                    state.InvalidControl = false;
                    state.Escape = character switch
                    {
                        '[' => EscapeState.ControlSequence,
                        ']' or 'P' or '^' or '_' or 'X' => EscapeState.String,
                        >= ' ' and <= '/' => EscapeState.EscapeIntermediate,
                        _ => EscapeState.None,
                    };
                    break;
                case EscapeState.EscapeIntermediate:
                    if (character is >= '0' and <= '~')
                    {
                        state.Escape = EscapeState.None;
                    }

                    break;
                case EscapeState.ControlSequence:
                    if (character is >= '@' and <= '~')
                    {
                        state.Escape = EscapeState.None;
                        if (_terminal && !state.InvalidControl)
                        {
                            ApplyTerminalControl(state, character);
                        }
                    }
                    else if (_terminal)
                    {
                        if (state.Control.Length < 64 && (char.IsAsciiDigit(character) || character == ';'))
                        {
                            state.Control.Append(character);
                        }
                        else
                        {
                            state.InvalidControl = true;
                        }
                    }

                    break;
                case EscapeState.String:
                    if (character is '\a' or '\u009c')
                    {
                        state.Escape = EscapeState.None;
                    }
                    else if (character == '\u001b')
                    {
                        state.Escape = EscapeState.StringEscape;
                    }

                    break;
                case EscapeState.StringEscape:
                    state.Escape = character == '\\' ? EscapeState.None : EscapeState.String;
                    break;
            }

            return true;
        }

        state.Escape = character switch
        {
            '\u001b' => EscapeState.Escape,
            '\u009b' => EscapeState.ControlSequence,
            '\u0090' or '\u009d' or '\u009e' or '\u009f' or '\u0098' => EscapeState.String,
            _ => EscapeState.None,
        };
        if (state.Escape == EscapeState.ControlSequence)
        {
            state.Control.Clear();
            state.InvalidControl = false;
        }

        return state.Escape != EscapeState.None;
    }

    private void AppendTerminal(StreamState state, char character)
    {
        switch (character)
        {
            case '\r':
                state.Column = 0;
                return;
            case '\n':
                TerminalLineFeed(state);
                return;
            case '\b':
                state.Column = Math.Max(0, state.Column - 1);
                return;
            case '\t':
                state.Column = Math.Min(TerminalColumns - 1, ((state.Column / 8) + 1) * 8);
                return;
        }

        if (char.IsHighSurrogate(character))
        {
            state.HighSurrogate = character;
            return;
        }

        if (char.IsLowSurrogate(character))
        {
            if (state.HighSurrogate is { } high)
            {
                WriteTerminalRune(state, new Rune(high, character));
            }

            state.HighSurrogate = null;
            return;
        }

        state.HighSurrogate = null;
        if (!char.IsControl(character) && character is not ('\u2028' or '\u2029'))
        {
            WriteTerminalRune(state, new Rune(character));
        }
    }

    private OutputLine TerminalLine(StreamState state)
    {
        while (_screen.Count <= state.Row)
        {
            _screen.Add(_lines.AddLast(new OutputLine()));
            _characters++;
        }

        return _screen[state.Row].Value;
    }

    private void TerminalLineFeed(StreamState state)
    {
        TerminalLine(state).Terminated = true;
        if (state.Row == TerminalRows - 1)
        {
            // Retain scrolled lines as bounded history; absolute cursor positions
            // continue to address only the 40 rows visible to the ConPTY child.
            _screen.RemoveAt(0);
        }
        else
        {
            state.Row++;
        }

        state.Column = Math.Min(state.Column, TerminalColumns - 1);
        _ = TerminalLine(state);
    }

    private void WriteTerminalRune(StreamState state, Rune rune)
    {
        int width = TerminalWidth(rune);
        if (width > 0 && state.Column + width > TerminalColumns)
        {
            TerminalLineFeed(state);
            state.Column = 0;
        }

        var line = TerminalLine(state);
        if (width == 0 && (state.Column == 0 || line.Text.Length >= TerminalColumns * 2))
        {
            return;
        }

        int start = TerminalIndex(line, state.Column, pad: width > 0);
        int end = width == 0 ? start : TerminalIndex(line, state.Column + width, pad: false);
        string text = rune.ToString();
        line.Text.Remove(start, end - start).Insert(start, text);
        _characters += text.Length - (end - start);
        BoundTerminalLine(line);
        state.Column += width;
    }

    private void BoundTerminalLine(OutputLine line)
    {
        // A fixed-width row needs at most two UTF-16 units per cell. Bound runs
        // of combining marks as well, so the visible screen never evicts itself.
        for (int index = 0; line.Text.Length > TerminalColumns * 2 && index < line.Text.Length;)
        {
            var rune = char.IsHighSurrogate(line.Text[index]) && index + 1 < line.Text.Length
                ? new Rune(line.Text[index], line.Text[index + 1])
                : new Rune(line.Text[index]);
            if (TerminalWidth(rune) == 0)
            {
                line.Text.Remove(index, rune.Utf16SequenceLength);
                _characters -= rune.Utf16SequenceLength;
            }
            else
            {
                index += rune.Utf16SequenceLength;
            }
        }
    }

    private int TerminalIndex(OutputLine line, int column, bool pad)
    {
        int index = 0;
        int position = 0;
        while (index < line.Text.Length)
        {
            var rune = char.IsHighSurrogate(line.Text[index]) && index + 1 < line.Text.Length
                ? new Rune(line.Text[index], line.Text[index + 1])
                : new Rune(line.Text[index]);
            int width = TerminalWidth(rune);
            if (position >= column && width > 0)
            {
                break;
            }

            if (position + width > column)
            {
                // A cursor may land on either cell of a wide character. Erase the
                // whole glyph before addressing that cell, without half-surrogates.
                line.Text.Remove(index, rune.Utf16SequenceLength).Insert(index, new string(' ', width));
                _characters += width - rune.Utf16SequenceLength;
                return index + column - position;
            }

            index += rune.Utf16SequenceLength;
            position += width;
        }

        if (pad && position < column)
        {
            line.Text.Append(' ', column - position);
            _characters += column - position;
            index += column - position;
        }

        return index;
    }

    private void ApplyTerminalControl(StreamState state, char command)
    {
        string[] fields = state.Control.ToString().Split(';');
        var values = new int[fields.Length];
        for (int index = 0; index < fields.Length; index++)
        {
            if (fields[index].Length > 0 && (!int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out values[index]) || values[index] > 100000))
            {
                return;
            }
        }

        int first = values[0];
        int amount = Math.Max(1, first);
        switch (command)
        {
            case 'A':
                state.Row = Math.Max(0, state.Row - amount);
                break;
            case 'B':
                state.Row = Math.Min(TerminalRows - 1, state.Row + amount);
                break;
            case 'C':
                state.Column = Math.Min(TerminalColumns - 1, state.Column + amount);
                break;
            case 'D':
                state.Column = Math.Max(0, state.Column - amount);
                break;
            case 'G':
            case '`':
                state.Column = Math.Min(TerminalColumns, amount) - 1;
                break;
            case 'd':
                state.Row = Math.Min(TerminalRows, amount) - 1;
                break;
            case 'H':
            case 'f':
                state.Row = Math.Min(TerminalRows, amount) - 1;
                state.Column = Math.Clamp(values.Length > 1 ? values[1] : 1, 1, TerminalColumns) - 1;
                break;
            case 'K':
                EraseTerminalLine(state, first);
                break;
            case 'J':
                if (first is >= 0 and <= 2)
                {
                    EraseTerminalLine(state, first);
                    for (int index = 0; index < _screen.Count; index++)
                    {
                        if (first == 2 || (first == 0 && index > state.Row) || (first == 1 && index < state.Row))
                        {
                            _characters -= _screen[index].Value.Text.Length;
                            _screen[index].Value.Text.Clear();
                        }
                    }
                }

                break;
        }
    }

    private void EraseTerminalLine(StreamState state, int mode)
    {
        var line = TerminalLine(state);
        if (mode == 0)
        {
            int start = TerminalIndex(line, state.Column, pad: false);
            _characters -= line.Text.Length - start;
            line.Text.Length = start;
        }
        else if (mode == 1)
        {
            int columns = Math.Min(TerminalColumns, state.Column + 1);
            int end = TerminalIndex(line, columns, pad: true);
            line.Text.Remove(0, end).Insert(0, new string(' ', columns));
            _characters += columns - end;
        }
        else if (mode == 2)
        {
            _characters -= line.Text.Length;
            line.Text.Clear();
        }

        BoundTerminalLine(line);
    }

    private LinkedListNode<OutputLine> CurrentLine(StreamState state)
    {
        if (state.Line is null)
        {
            state.Line = _lines.AddLast(new OutputLine());
            _characters++;
        }

        return state.Line;
    }

    private void AppendCharacter(StreamState state, char character)
    {
        var line = CurrentLine(state).Value;
        if (state.CarriageReturn)
        {
            _characters -= line.Text.Length;
            line.Text.Clear();
            state.CarriageReturn = false;
        }

        line.Text.Append(character);
        _characters++;
    }

    private void Trim()
    {
        while (_characters > MaximumCharacters && _lines.First is { } first)
        {
            int excess = _characters - MaximumCharacters;
            if (excess <= first.Value.Text.Length && first.Value.Text.Length > 0)
            {
                int removed = excess;
                if (removed < first.Value.Text.Length && char.IsLowSurrogate(first.Value.Text[removed]))
                {
                    removed++;
                }

                first.Value.Text.Remove(0, removed);
                _characters -= removed;
                break;
            }

            _characters -= first.Value.Text.Length + 1;
            _lines.RemoveFirst();
            if (_standardOutput.Line == first)
            {
                _standardOutput.Line = null;
            }

            if (_standardError.Line == first)
            {
                _standardError.Line = null;
            }
        }
    }

    private string SnapshotCore()
    {
        var result = new StringBuilder(_characters);
        foreach (var line in _lines)
        {
            for (int index = 0; index < line.Text.Length; index++)
            {
                char character = line.Text[index];
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < line.Text.Length && char.IsLowSurrogate(line.Text[index + 1]))
                    {
                        result.Append(character).Append(line.Text[++index]);
                    }
                }
                else if (!char.IsLowSurrogate(character))
                {
                    result.Append(character);
                }
            }

            if (line.Terminated || !ReferenceEquals(line, _lines.Last?.Value))
            {
                result.Append('\n');
            }
        }

        return result.ToString();
    }

    private sealed class StreamState
    {
        internal LinkedListNode<OutputLine>? Line { get; set; }
        internal bool CarriageReturn { get; set; }
        internal EscapeState Escape { get; set; }
        internal StringBuilder Control { get; } = new();
        internal bool InvalidControl { get; set; }
        internal int Column { get; set; }
        internal int Row { get; set; }
        internal char? HighSurrogate { get; set; }
    }

    private sealed class OutputLine
    {
        internal StringBuilder Text { get; } = new();
        internal bool Terminated { get; set; }
    }
}
