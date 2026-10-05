using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Fx.ControlKit;

public partial class DropDownListControl<TValue, TItem>
{
    private List<TItem> _clientItems = new();
    private List<(string Value, string Text)> _clientOptions = new();
    private long _clientOptionsVersion, _clientCommand, _clientActionAck;
    private bool _clientOpenRequested, _clientDropdownRegistered, _clientDropdownRegistering;
    private readonly SemaphoreSlim _clientCommitGate = new(1, 1);

    private string ClientDropdownConfiguration => JsonSerializer.Serialize(new
    {
        optionsVersion = _clientOptionsVersion,
        command = _clientCommand,
        open = _clientOpenRequested,
        ack = _clientActionAck,
        text = EditableDisplayText,
        value = Value?.ToString() ?? "",
        enabled = Enabled && (CellHost is null || CellHost.IsEditing),
        liveText = TextChanged.HasDelegate && !DelegateVerticalArrows,
        stagesText = TextChanged.HasDelegate,
        commitOnBlur = CommitEditableTextOnBlur,
        hosted = CellHost is not null,
        forwardKeys = OnKeyDown.HasDelegate,
        gridKeys = DelegateVerticalArrows && OnKeyDown.HasDelegate,
        delegateArrows = DelegateVerticalArrows || CellHost is not null,
        initialText = CellHost?.InitialText,
        highlight = _highlightedIndex
    });

    private void UpdateClientDropdownOptions()
    {
        _clientItems = DataSource?.ToList() ?? new();
        var options = _clientItems.Select(item =>
            (GetFieldValue(item, ValueFieldName), GetFieldValue(item, TextFieldName))).ToList();
        if (!_clientOptions.SequenceEqual(options))
        {
            _clientOptions = options;
            _clientOptionsVersion++;
        }
    }

    private void RequestClientDropdown(bool open)
    {
        _clientOpenRequested = open;
        _clientCommand++;
    }

    private async Task<bool> RegisterClientDropdownAsync()
    {
        if (_clientDropdownRegistered || _clientDropdownRegistering) return true;
        if (_disposed) return false;
        _clientDropdownRegistering = true;
        try
        {
            var module = await GetDropdownJsModuleAsync();
            if (_disposed)
            {
                await DisposeLateModuleAsync();
                return false;
            }
            if (module is null) return false;
            _selfRef ??= DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("enableClientEditableDropdown", _hostRef, _selfRef);
            if (_disposed || !Editable)
            {
                await module.InvokeVoidAsync("unwatchDropdownOpening", _hostRef);
                return false;
            }
            _clientDropdownRegistered = true;
            _openingObserverRegistered = false;
            return true;
        }
        catch { return false; }
        finally { _clientDropdownRegistering = false; }
    }

    public sealed class ClientDropdownAction
    {
        public long Sequence { get; set; }
        public long OptionsVersion { get; set; }
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public string? Value { get; set; }
        public string Key { get; set; } = "";
        public bool ShiftKey { get; set; }
        public bool CtrlKey { get; set; }
        public bool AltKey { get; set; }
        public bool MetaKey { get; set; }
        public bool WasOpen { get; set; }
        public bool Edited { get; set; }
    }

    // Only commits/cancellations cross the circuit. The browser owns the popup
    // and its moving highlight, so a delayed reply cannot reopen an old list.
    [JSInvokable]
    public async Task<object> OnClientDropdownActionAsync(ClientDropdownAction action)
    {
        await _clientCommitGate.WaitAsync();
        try
        {
            if (_disposed || !Editable || !Enabled || action.Sequence <= _clientActionAck)
                return new { accepted = false, text = EditableDisplayText, value = Value?.ToString() ?? "" };
            _clientActionAck = action.Sequence;
            var key = new KeyboardEventArgs { Key = action.Key, ShiftKey = action.ShiftKey,
                CtrlKey = action.CtrlKey, AltKey = action.AltKey, MetaKey = action.MetaKey };
            var navigation = action.Kind == "navigate";
            var pick = action.Value is not null;
            var item = default(TItem);
            if (pick)
            {
                UpdateClientDropdownOptions();
                var index = _clientOptions.FindIndex(option => option.Value == action.Value && option.Text == action.Text);
                if (index < 0) return new { accepted = false, text = EditableDisplayText, value = Value?.ToString() ?? "" };
                item = _clientItems[index];
            }

            _isOpen = false;
            _editableInputFocused = action.Key != "Tab" && action.Kind != "blur";
            var commit = action.Kind is "select" or "text" or "blur" || navigation;
            if (commit)
            {
                var text = pick ? action.Value! : action.Text;
                if (!pick && MaxLength is > 0 && text.Length > MaxLength.Value)
                    text = text[..MaxLength.Value];
                _editableText = action.Text;
                _editableRenderedText = action.Text;
                // Native GridControl already commits inside its key handler.
                // Give that handler the complete draft, not a second commit.
                if (navigation && DelegateVerticalArrows && OnKeyDown.HasDelegate)
                {
                    if (action.Edited || text != (Value?.ToString() ?? ""))
                        await TextChanged.InvokeAsync(text);
                    await ForwardKeyAsync(key);
                }
                else
                {
                    var newValue = ConvertToValue(text);
                    var unchanged = object.Equals(Value, newValue);
                    Value = newValue;
                    if (!unchanged || (pick && !SuppressUnchangedValueChange))
                    {
                        await ValueChanged.InvokeAsync(newValue);
                        await ValueChange.InvokeAsync(new ChangeEventArgs<TValue, TItem> { Value = newValue, ItemData = item! });
                    }
                    if (navigation) await ForwardKeyAsync(key);
                    else if (action.WasOpen && CellHost is not null) CellHost.EndEdit();
                    // The client path's counterpart of HandleEditableBlur: a host that stages
                    // keystrokes sees no ValueChanged (the value already matches), so tell it the
                    // text edit finished — on blur, and on Enter that committed free text.
                    if (!pick && action.Kind is "text" or "blur")
                        await EditableCommitted.InvokeAsync();
                }
            }
            else if (action.Kind is "cancel" or "close")
            {
                if (action.WasOpen)
                {
                    await Closed.InvokeAsync();
                    CellHost?.EndEdit();
                }
                else if (action.Key == "Escape") await ForwardKeyAsync(key);
            }
            if (!_disposed) await InvokeAsync(StateHasChanged);
            return new { accepted = true, text = EditableDisplayText, value = Value?.ToString() ?? "" };
        }
        finally { _clientCommitGate.Release(); }
    }
}
