using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>
    /// Opts row and batch editing into EditContext validation. Null preserves
    /// the historical GridControl behavior and performs no model validation.
    /// </summary>
    [Parameter] public GridValidationSettings<TValue>? ValidationSettings { get; set; }

    /// <summary>The EditContext for the active Inline/Dialog edit, when enabled.</summary>
    public EditContext? ActiveEditContext => _rowEditContext;

    private EditContext? _rowEditContext;
    private ValidationMessageStore? _gridValidationMessages;
    private IDisposable? _dataAnnotationsValidation;
    private readonly Dictionary<string, string> _rowConversionErrors =
        new(StringComparer.OrdinalIgnoreCase);

    private void InitializeRowValidationContext()
    {
        DisposeRowValidationContext();
        if (ValidationSettings == null || _editItem == null)
            return;

        var context = ValidationSettings.EditContextFactory?.Invoke(_editItem)
            ?? new EditContext(_editItem);
        if (!ReferenceEquals(context.Model, _editItem))
        {
            throw new InvalidOperationException(
                $"{nameof(GridValidationSettings<TValue>.EditContextFactory)} must return an EditContext whose Model is the supplied edit row.");
        }

        _rowEditContext = context;
        _gridValidationMessages = new ValidationMessageStore(context);
        if (ValidationSettings.EnableDataAnnotations)
            _dataAnnotationsValidation = context.EnableDataAnnotationsValidation(Services);

        context.OnValidationRequested += HandleRowValidationRequested;
        context.OnFieldChanged += HandleRowValidationFieldChanged;
    }

    private void DisposeRowValidationContext()
    {
        if (_rowEditContext != null)
        {
            _rowEditContext.OnValidationRequested -= HandleRowValidationRequested;
            _rowEditContext.OnFieldChanged -= HandleRowValidationFieldChanged;
        }

        _dataAnnotationsValidation?.Dispose();
        _dataAnnotationsValidation = null;
        _rowConversionErrors.Clear();
        _gridValidationMessages = null;
        _rowEditContext = null;
    }

    private void HandleRowValidationRequested(object? sender, ValidationRequestedEventArgs e)
    {
        _gridValidationMessages?.Clear();
        AddCustomValidationMessages(fieldName: null, proposedValue: null);
        AddRowConversionValidationMessages();
    }

    private void HandleRowValidationFieldChanged(object? sender, FieldChangedEventArgs e)
    {
        if (_rowEditContext == null || _gridValidationMessages == null || ValidationSettings?.ValidateOnFieldChange != true)
            return;

        _gridValidationMessages.Clear(e.FieldIdentifier);
        AddCustomValidationMessages(e.FieldIdentifier.FieldName, GetPropertyValue(_editItem, e.FieldIdentifier.FieldName));
    }

    private void AddCustomValidationMessages(string? fieldName, object? proposedValue)
    {
        if (_rowEditContext == null
            || _gridValidationMessages == null
            || _editItem == null
            || ValidationSettings?.CustomValidator == null)
        {
            return;
        }

        IEnumerable<ValidationResult> results;
        try
        {
            results = ValidationSettings.CustomValidator(
                new GridValidationRequest<TValue>(_editItem, fieldName, proposedValue))
                ?? Array.Empty<ValidationResult>();
        }
        catch (Exception ex)
        {
            results = [new ValidationResult($"Custom validation failed: {ex.Message}")];
        }

        foreach (var result in results.Where(result => result != ValidationResult.Success))
        {
            var message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "The value is invalid."
                : result.ErrorMessage!;
            var memberNames = result.MemberNames?.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray()
                ?? Array.Empty<string>();

            if (memberNames.Length == 0 && !string.IsNullOrWhiteSpace(fieldName))
                memberNames = [fieldName];

            if (memberNames.Length == 0)
            {
                _gridValidationMessages.Add(new FieldIdentifier(_rowEditContext.Model, string.Empty), message);
            }
            else
            {
                foreach (var memberName in memberNames)
                    _gridValidationMessages.Add(new FieldIdentifier(_rowEditContext.Model, memberName), message);
            }
        }

        _rowEditContext.NotifyValidationStateChanged();
    }

    private void SetRowEditProperty(string field, object? value)
    {
        if (_editItem == null)
            return;

        var fieldIdentifier = _rowEditContext == null
            ? default
            : new FieldIdentifier(_rowEditContext.Model, ResolveValidationMemberName(_rowEditContext.Model, field));
        _gridValidationMessages?.Clear(fieldIdentifier);

        if (!SetPropertyObjectValue(_editItem, field, value))
        {
            var column = FindColumnByField(field);
            var name = column == null ? field : HeaderColumnDisplay(column);
            var conversionMessage = $"'{value}' is not a valid value for {name}.";
            _rowConversionErrors[field] = conversionMessage;
            if (_rowEditContext != null && _gridValidationMessages != null)
            {
                _gridValidationMessages.Add(fieldIdentifier, conversionMessage);
                _rowEditContext.NotifyValidationStateChanged();
            }
            _validationStatusMessage = conversionMessage;
            return;
        }

        _rowConversionErrors.Remove(field);
        _rowEditContext?.NotifyFieldChanged(fieldIdentifier);
        if (_rowEditContext != null && !_rowEditContext.GetValidationMessages().Any())
            _validationStatusMessage = null;
    }

    private bool ValidateRowEdit()
    {
        if (_rowConversionErrors.Count > 0)
        {
            _validationStatusMessage = _rowConversionErrors.Values.First();
            return false;
        }

        if (_rowEditContext == null)
            return true;

        var valid = _rowEditContext.Validate();
        _validationStatusMessage = valid
            ? null
            : _rowEditContext.GetValidationMessages().FirstOrDefault() ?? "Correct the validation errors before saving.";
        return valid;
    }

    private void AddRowConversionValidationMessages()
    {
        if (_rowEditContext == null || _gridValidationMessages == null)
            return;

        foreach (var (field, message) in _rowConversionErrors)
        {
            _gridValidationMessages.Add(
                new FieldIdentifier(
                    _rowEditContext.Model,
                    ResolveValidationMemberName(_rowEditContext.Model, field)),
                message);
        }

        if (_rowConversionErrors.Count > 0)
            _rowEditContext.NotifyValidationStateChanged();
    }

    private IReadOnlyList<string> GetRowValidationMessages(string? fieldName = null)
    {
        if (_rowEditContext == null)
            return Array.Empty<string>();

        var messages = string.IsNullOrEmpty(fieldName)
            ? _rowEditContext.GetValidationMessages()
            : _rowEditContext.GetValidationMessages(new FieldIdentifier(
                _rowEditContext.Model,
                ResolveValidationMemberName(_rowEditContext.Model, fieldName)));
        return messages.Distinct(StringComparer.CurrentCulture).ToArray();
    }

    private bool HasRowValidationMessages(string fieldName) =>
        _rowEditContext != null
        && _rowEditContext.GetValidationMessages(new FieldIdentifier(
            _rowEditContext.Model,
            ResolveValidationMemberName(_rowEditContext.Model, fieldName))).Any();

    private string GetRowEditorCss(string fieldName, string baseCss) =>
        HasRowValidationMessages(fieldName) ? $"{baseCss} fx-edit-input-invalid" : baseCss;

    private bool TryValidateBatchProperty(TValue item, string field, string? rawValue, out string? message)
    {
        message = null;
        object? proposedValue = rawValue;
        var accessor = item == null ? null : GetPropertyAccessor(item.GetType(), field);
        if (accessor != null)
        {
            try
            {
                proposedValue = ConvertToPropertyType(rawValue, accessor.PropertyType);
            }
            catch
            {
                message = $"'{rawValue}' is not a valid value for {field}.";
                return false;
            }
        }

        if (ValidationSettings == null)
            return true;

        var results = new List<ValidationResult>();
        if (ValidationSettings.EnableDataAnnotations && accessor != null)
        {
            var validationMemberName = ResolveValidationMemberName(item!, field);
            var validationContext = new ValidationContext(item!, Services, items: null)
            {
                MemberName = validationMemberName,
                DisplayName = FindColumnByField(field)?.DisplayHeader ?? field
            };
            Validator.TryValidateProperty(proposedValue, validationContext, results);
        }

        if (ValidationSettings.CustomValidator != null)
        {
            try
            {
                results.AddRange(ValidationSettings.CustomValidator(
                    new GridValidationRequest<TValue>(item, field, proposedValue))
                    ?? Array.Empty<ValidationResult>());
            }
            catch (Exception ex)
            {
                results.Add(new ValidationResult($"Custom validation failed: {ex.Message}"));
            }
        }

        var failure = results.FirstOrDefault(result => result != ValidationResult.Success);
        message = failure?.ErrorMessage;
        return failure == null;
    }

    private static string ResolveValidationMemberName(object item, string field) =>
        item.GetType().GetProperty(
            field,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.Name
        ?? field;

    private async Task RejectBatchValidationAsync(TValue item, string field, string? message)
    {
        _validationStatusMessage = string.IsNullOrWhiteSpace(message)
            ? "The value is invalid."
            : message;
        _pendingBatchEditFocus = true;
        _pendingBatchEditSelectAll = true;
        _pendingBatchEditClientX = null;
        _batchEditHostKeyHandoffOpen = true;

        var rowIndex = ResolveRowIndex(item, _batchEditRowIndex);
        var columnIndex = ResolveVisibleColumnIndex(field);
        if (columnIndex >= 0)
        {
            SetActiveCell(rowIndex, columnIndex);
            RememberKeyboardNavigationSource(item, rowIndex, columnIndex);
        }

        await InvokeAsync(StateHasChanged);
        if (_batchDropdownEditorRef != null)
            await _batchDropdownEditorRef.FocusAsync();
        else
            await ApplyPendingBatchEditFocusAsync();
    }
}
