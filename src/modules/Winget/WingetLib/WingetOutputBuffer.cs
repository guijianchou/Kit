// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;

namespace Kit.WingetLib;

internal sealed class WingetOutputBuffer
{
    internal const int MaximumCharacters = 16 * 1024;
    internal static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private readonly LinkedList<OutputLine> _lines = new();
    private readonly StreamState _standardOutput = new();
    private readonly StreamState _standardError = new();
    private int _characters;
    private string _published = string.Empty;
    private TimeSpan _lastPublished;

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
            var state = standardError ? _standardError : _standardOutput;
            foreach (char character in text)
            {
                if (ConsumeEscape(state, character))
                {
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

    private static bool ConsumeEscape(StreamState state, char character)
    {
        if (state.Escape != EscapeState.None)
        {
            switch (state.Escape)
            {
                case EscapeState.Escape:
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
        return state.Escape != EscapeState.None;
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
    }

    private sealed class OutputLine
    {
        internal StringBuilder Text { get; } = new();
        internal bool Terminated { get; set; }
    }
}
