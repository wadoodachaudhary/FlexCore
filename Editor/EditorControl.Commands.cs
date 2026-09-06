using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Editor;

public partial class EditorControl
{
    private EditorHistoryState _history = new(false, false, 0, 0);
    private IReadOnlyList<EditorSearchMatch> _matches = Array.Empty<EditorSearchMatch>();
    private string _query = string.Empty, _replacement = string.Empty;
    private bool _matchCase, _wholeWord, _searchOpen, _focusSearch;
    private int _matchIndex = -1;
    private TextBoxControl? _findInput;

    public async Task<EditorHistoryState> GetHistoryStateAsync() =>
        await JS.InvokeAsync<EditorHistoryState>("fxEditor.historyState", EditorId);

    public async Task UndoAsync()
    {
        if (ReadOnly || !EnableHistory) return;
        await JS.InvokeVoidAsync("fxEditor.undo", EditorId);
        await RefreshStateAsync();
        await InvokeAsync(StateHasChanged);
    }

    public async Task RedoAsync()
    {
        if (ReadOnly || !EnableHistory) return;
        await JS.InvokeVoidAsync("fxEditor.redo", EditorId);
        await RefreshStateAsync();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Finds literal text within logical blocks, including text split by inline formatting.</summary>
    public async Task<IReadOnlyList<EditorSearchMatch>> FindAllAsync(string text, EditorSearchOptions? options = null)
    {
        _query = text ?? string.Empty;
        options ??= new();
        _matchCase = options.MatchCase;
        _wholeWord = options.WholeWord;
        _matches = await JS.InvokeAsync<List<EditorSearchMatch>>("fxEditor.findAll", EditorId, _query, options);
        _matchIndex = -1;
        await InvokeAsync(StateHasChanged);
        return _matches;
    }

    public async Task<bool> SelectSearchResultAsync(int index)
    {
        var selected = await JS.InvokeAsync<bool>("fxEditor.selectSearchResult", EditorId, index);
        if (selected) _matchIndex = index;
        await InvokeAsync(StateHasChanged);
        return selected;
    }

    /// <summary>Replaces the selected search result with literal text; formatting outside the match is retained.</summary>
    public Task<int> ReplaceCurrentAsync(string replacement) => ReplaceMatchesAsync(replacement, false);

    /// <summary>Replaces every current match as one undoable edit.</summary>
    public Task<int> ReplaceAllAsync(string replacement) => ReplaceMatchesAsync(replacement, true);

    private async Task<int> ReplaceMatchesAsync(string replacement, bool all)
    {
        if (ReadOnly) return 0;
        var count = await JS.InvokeAsync<int>("fxEditor.replaceSearch", EditorId, _matchIndex, replacement ?? string.Empty, all);
        await RefreshStateAsync();
        _matchIndex = -1;
        await InvokeAsync(StateHasChanged);
        return count;
    }

    public async Task ClearSearchAsync()
    {
        await JS.InvokeVoidAsync("fxEditor.clearSearch", EditorId);
        _matches = Array.Empty<EditorSearchMatch>();
        _matchIndex = -1;
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task OpenSearchAsync()
    {
        _searchOpen = true;
        _focusSearch = true;
        return InvokeAsync(StateHasChanged);
    }

    private async Task CloseSearchAsync()
    {
        _searchOpen = false;
        await ClearSearchAsync();
        await JS.InvokeVoidAsync("fxEditor.focus", EditorId);
    }

    private async Task SearchAsync()
    {
        await FindAllAsync(_query, new(_matchCase, _wholeWord));
        if (_matches.Count > 0) await SelectSearchResultAsync(0);
    }

    private Task MoveSearchAsync(int direction) => _matches.Count == 0 ? Task.CompletedTask :
        SelectSearchResultAsync(_matchIndex < 0 ? (direction < 0 ? _matches.Count - 1 : 0) :
            (_matchIndex + direction + _matches.Count) % _matches.Count);

    private async Task SearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await SearchAsync();
        else if (e.Key == "Escape") await CloseSearchAsync();
    }

    private async Task RefreshStateAsync()
    {
        var state = await GetHistoryStateAsync();
        await UpdateHistoryAsync(state);
        _matches = await JS.InvokeAsync<List<EditorSearchMatch>>("fxEditor.searchResults", EditorId);
        if (_matchIndex >= _matches.Count) _matchIndex = -1;
    }

    private async Task<bool> UpdateHistoryAsync(EditorHistoryState state)
    {
        if (state == _history) return false;
        _history = state;
        await HistoryChanged.InvokeAsync(state);
        return true;
    }
}
