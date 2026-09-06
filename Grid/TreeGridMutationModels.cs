using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components.Forms;

namespace Fx.ControlKit.Grid;

public enum TreeGridChangeKind { Add, Update, Delete, Move }
public sealed record TreeGridRowChange<T>(TreeGridChangeKind Kind, T? Original, T? Data);

/// <summary>A complete transaction, raised before any source record is replaced.</summary>
public sealed class TreeGridDataChangeEventArgs<T>(IReadOnlyList<TreeGridRowChange<T>> changes)
{
    public IReadOnlyList<TreeGridRowChange<T>> Changes { get; } = changes;
    public bool Cancel { get; set; }
    public string? Error { get; set; }
}

public sealed class TreeGridEditEventArgs<T>(T? original, T draft, EditContext context, bool isNew)
{
    public T? Original { get; } = original;
    public T Draft { get; } = draft;
    public EditContext EditContext { get; } = context;
    public bool IsNew { get; } = isNew;
    public bool Cancel { get; set; }
}

public sealed class TreeGridRowMoveEventArgs<T>(T item, T target, TreeDropPosition position)
{
    public T Item { get; } = item;
    public T Target { get; } = target;
    public TreeDropPosition Position { get; } = position;
    public bool Cancel { get; set; }
    public string? Error { get; set; }
}

internal sealed class TreeGridEditSession<T> : IDisposable
{
    public required object Id { get; init; }
    public T? Original { get; init; }
    public required T Draft { get; init; }
    public required EditContext Context { get; init; }
    public required ValidationMessageStore Messages { get; init; }
    public required IDisposable AnnotationSubscription { get; init; }
    public bool IsNew { get; init; }
    public bool Validate(Func<T, IEnumerable<ValidationResult>>? validator)
    {
        Messages.Clear();
        if (validator is not null)
            foreach (var result in validator(Draft))
                foreach (var name in result.MemberNames.DefaultIfEmpty(""))
                    Messages.Add(new FieldIdentifier(Context.Model, name), result.ErrorMessage ?? "Invalid value.");
        return Context.Validate();
    }
    public void Dispose() => AnnotationSubscription.Dispose();
}
