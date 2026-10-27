using System.Globalization;
using System.Text;

namespace DevUp.Core;

/// <summary>还原伪终端屏幕行，处理回车、分片转义序列和原地刷新的下载计数。</summary>
internal sealed class TerminalOutput
{
    private readonly char[][] _rows;
    private readonly string[] _reported;
    private readonly HashSet<int> _changed = new();
    private readonly StringBuilder _sequence = new();
    private readonly Action<string> _onLine;
    private int _row, _column, _savedRow, _savedColumn;
    private char _lastCharacter = ' ';
    private State _state;

    public TerminalOutput(int width, int height, Action<string> onLine)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _rows = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat(' ', width).ToArray()).ToArray();
        _reported = Enumerable.Repeat("", height).ToArray();
        _onLine = onLine;
    }

    public void Append(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            switch (_state)
            {
                case State.Escape:
                    _state = character switch { '[' => State.Control, ']' => State.Title, _ => State.Text };
                    _sequence.Clear();
                    if (character == '7') { _savedRow = _row; _savedColumn = _column; }
                    if (character == '8') { _row = _savedRow; _column = _savedColumn; }
                    break;
                case State.Control:
                    if (character is >= '@' and <= '~')
                    {
                        ApplyControl(character);
                        _state = State.Text;
                    }
                    else if (_sequence.Length < 128) _sequence.Append(character);
                    else _state = State.Text;
                    break;
                case State.Title:
                    if (character == '\a') _state = State.Text;
                    else if (character == '\x1b') _state = State.TitleEscape;
                    break;
                case State.TitleEscape:
                    _state = character == '\\' ? State.Text : State.Title;
                    break;
                default:
                    switch (character)
                    {
                        case '\x1b': _state = State.Escape; break;
                        case '\r': ReportChanged(); _column = 0; break;
                        case '\n': ReportChanged(); NextRow(); break;
                        case '\b': _column = Math.Max(0, _column - 1); break;
                        case '\t': _column = Math.Min(_rows[0].Length - 1, (_column / 8 + 1) * 8); break;
                        default:
                            if (character >= ' ' && character != '\x7f' &&
                                CharUnicodeInfo.GetUnicodeCategory(character) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.Format))
                                Write(character);
                            break;
                    }
                    break;
            }
        }

        ReportChanged();
    }

    private void ReportChanged()
    {
        foreach (var row in _changed.Order())
        {
            var line = new string(_rows[row].Where(character => character != '\0').ToArray()).TrimEnd();
            if (line == _reported[row]) continue;
            _reported[row] = line;
            if (!string.IsNullOrWhiteSpace(line)) _onLine(line);
        }
        _changed.Clear();
    }

    private void Write(char character)
    {
        var wide = character is >= '\u1100' and <= '\u115f' or >= '\u2e80' and <= '\ua4cf'
            or >= '\uac00' and <= '\ud7a3' or >= '\uf900' and <= '\ufaff'
            or >= '\ufe10' and <= '\ufe6f' or >= '\uff00' and <= '\uff60' or >= '\uffe0' and <= '\uffe6';
        if (_column >= _rows[0].Length || (wide && _column + 1 >= _rows[0].Length))
        {
            _column = 0;
            NextRow();
        }
        _rows[_row][_column++] = character;
        if (wide) _rows[_row][_column++] = '\0';
        _lastCharacter = character;
        _changed.Add(_row);
    }

    private void NextRow()
    {
        if (_row < _rows.Length - 1) { _row++; return; }
        var first = _rows[0];
        Array.Copy(_rows, 1, _rows, 0, _rows.Length - 1);
        _rows[^1] = first;
        Array.Fill(first, ' ');
        for (var row = 0; row < _rows.Length; row++) _changed.Add(row);
    }

    private void Clear(int row, int start, int end)
    {
        ReportChanged();
        Array.Fill(_rows[row], ' ', Math.Clamp(start, 0, _rows[row].Length),
            Math.Max(0, Math.Min(end, _rows[row].Length) - Math.Clamp(start, 0, _rows[row].Length)));
        _changed.Add(row);
    }

    private void ApplyControl(char command)
    {
        var parameters = _sequence.ToString().TrimStart('?', '>').Split(';')
            .Select(value => int.TryParse(value, out var number) ? Math.Clamp(number, 0, 10000) : 0).ToArray();
        var first = parameters[0];
        var count = Math.Max(1, first);
        switch (command)
        {
            case 'H': case 'f':
                _row = Math.Clamp(count - 1, 0, _rows.Length - 1);
                _column = Math.Clamp((parameters.Length > 1 ? Math.Max(1, parameters[1]) : 1) - 1, 0, _rows[0].Length - 1);
                break;
            case 'A': _row = Math.Max(0, _row - count); break;
            case 'B': _row = Math.Min(_rows.Length - 1, _row + count); break;
            case 'C': _column = Math.Min(_rows[0].Length - 1, _column + count); break;
            case 'D': _column = Math.Max(0, _column - count); break;
            case 'G': _column = Math.Min(_rows[0].Length - 1, count - 1); break;
            case 'd': _row = Math.Min(_rows.Length - 1, count - 1); break;
            case 'K': Clear(_row, first == 0 ? _column : 0, first == 1 ? _column + 1 : _rows[0].Length); break;
            case 'J':
                for (var row = 0; row < _rows.Length; row++)
                    if (first == 2 || (first == 0 && row >= _row) || (first == 1 && row <= _row))
                        Clear(row, row == _row && first == 0 ? _column : 0,
                            row == _row && first == 1 ? _column + 1 : _rows[0].Length);
                break;
            case 'X': Clear(_row, _column, _column + count); break;
            case 's': _savedRow = _row; _savedColumn = _column; break;
            case 'u': _row = _savedRow; _column = _savedColumn; break;
            case 'b': for (var index = 0; index < count; index++) Write(_lastCharacter); break;
        }
    }

    private enum State { Text, Escape, Control, Title, TitleEscape }
}
