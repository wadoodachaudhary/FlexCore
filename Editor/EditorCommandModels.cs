namespace Fx.ControlKit.Editor;

public sealed record EditorHistoryState(bool CanUndo, bool CanRedo, int UndoCount, int RedoCount);

public sealed record EditorSearchOptions(bool MatchCase = false, bool WholeWord = false);

/// <summary>Offsets are UTF-16 text positions within a logical block, not HTML offsets.</summary>
public sealed record EditorSearchMatch(int Index, string BlockId, int Start, int Length, string Text, string Preview);
