namespace Wicked.App.Workbench;

/// <summary>A place to go back to: a file (or an untitled tab) and a caret offset in it.</summary>
public sealed record NavigationPlace(string? Path, EditorTab? Tab, int Offset)
{
    public bool SameFile(NavigationPlace other) => Path is not null ? Path == other.Path : Tab == other.Tab;
}

/// <summary>
/// Back and Forward (Ctrl+Alt+- / Ctrl+Shift+-, the mouse's side buttons), as VS Code keeps them: a place is remembered
/// when the user leaves it for another tab or jumps more than <see cref="JumpLines"/> lines in the same file (Go to
/// Definition, Go to Line, a search result). Going back from the present remembers the present, so Forward returns to it;
/// a new jump after going back drops what was ahead, as a browser does. Lives on the UI thread.
/// </summary>
public sealed class NavigationHistory
{
    public const int JumpLines = 10;
    private const int Limit = 50;

    private readonly List<NavigationPlace> _places = [];
    private int _position;

    public bool CanGoBack => _position > 0;

    public bool CanGoForward => _position < _places.Count - 1;

    /// <summary>The user left <paramref name="place"/> (another tab, or a jump in the same file).</summary>
    public void Leaving(NavigationPlace place)
    {
        if (_position < _places.Count) _places.RemoveRange(_position, _places.Count - _position);
        if (_places.Count > 0 && _places[^1].SameFile(place) && _places[^1].Offset == place.Offset) return;
        _places.Add(place);
        if (_places.Count > Limit) _places.RemoveAt(0);
        _position = _places.Count;
    }

    /// <summary>Where Back goes from <paramref name="here"/>; null when there is nothing behind.</summary>
    public NavigationPlace? Back(NavigationPlace here)
    {
        if (_position == 0) return null;
        if (_position == _places.Count)
        {
            _places.Add(here);
            if (_places.Count > Limit) _places.RemoveAt(0);
            _position = _places.Count - 1;
        }

        return _places[--_position];
    }

    /// <summary>Where Forward goes; null at the present.</summary>
    public NavigationPlace? Forward()
    {
        if (_position >= _places.Count - 1) return null;
        return _places[++_position];
    }

    /// <summary>A closed untitled tab cannot be gone back to; its places go.</summary>
    public void Forget(EditorTab tab)
    {
        for (var i = _places.Count - 1; i >= 0; i--)
        {
            if (_places[i].Path is null && _places[i].Tab == tab)
            {
                _places.RemoveAt(i);
                if (i < _position) _position--;
            }
        }

        _position = Math.Min(_position, _places.Count);
    }
}
