using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using System.ComponentModel.DataAnnotations;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Opt-in validation settings for <see cref="GridControl{TValue}"/> row and
/// batch editing. Leaving <c>ValidationSettings</c> unset preserves the
/// pre-validation editing pipeline.
/// </summary>
public sealed class GridValidationSettings<TValue>
{
    /// <summary>
    /// Enables validation attributes such as <see cref="RequiredAttribute"/>,
    /// <see cref="RangeAttribute"/>, and <see cref="IValidatableObject"/> on the
    /// row model. Enabled by default when validation settings are supplied.
    /// </summary>
    public bool EnableDataAnnotations { get; set; } = true;

    /// <summary>
    /// Optional factory for an application-owned <see cref="EditContext"/>.
    /// The returned context must use the supplied row as its Model. This is
    /// useful for attaching FluentValidation or another EditContext extension.
    /// </summary>
    public Func<TValue, EditContext>? EditContextFactory { get; set; }

    /// <summary>
    /// Optional validator invoked for a complete row before Save and for the
    /// changed property during field/batch editing. Return standard
    /// <see cref="ValidationResult"/> instances and name affected members when
    /// a message belongs beside a field.
    /// </summary>
    public Func<GridValidationRequest<TValue>, IEnumerable<ValidationResult>>? CustomValidator { get; set; }

    /// <summary>
    /// Optional validator component/template rendered inside the active
    /// EditContext cascade. Set <see cref="EnableDataAnnotations"/> to false
    /// when the supplied component already installs DataAnnotations handling.
    /// </summary>
    public RenderFragment<EditContext>? ValidatorTemplate { get; set; }

    /// <summary>Runs field and custom property validation as a value changes.</summary>
    public bool ValidateOnFieldChange { get; set; } = true;

    /// <summary>Shows validation messages beside the corresponding editor.</summary>
    public bool ShowFieldMessages { get; set; } = true;

    /// <summary>Shows the active EditContext messages as a row/dialog summary.</summary>
    public bool ShowValidationSummary { get; set; } = true;
}

/// <summary>Context supplied to a GridControl custom validator.</summary>
/// <param name="Item">The edited row.</param>
/// <param name="FieldName">
/// The changed property, or null when the complete row is being validated.
/// </param>
/// <param name="ProposedValue">
/// The converted proposed property value during batch validation; otherwise
/// the current property value or null for full-row validation.
/// </param>
public sealed record GridValidationRequest<TValue>(
    TValue Item,
    string? FieldName,
    object? ProposedValue);
