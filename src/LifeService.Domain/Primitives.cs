namespace LifeService.Domain;

/// <summary>
/// Strongly-typed identifier for a board (a single Game of Life simulation).
/// </summary>
public readonly record struct BoardId(Guid Value)
{
    public static BoardId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// A live cell coordinate on the (theoretically infinite) board.
/// Equality is value based, so instances are safe to use as <see cref="HashSet{T}"/> keys.
/// </summary>
public readonly record struct LifeCell(int X, int Y);

/// <summary>
/// Monotonically increasing generation label for a board. Label 0 is the uploaded initial state.
/// </summary>
public readonly record struct LifeStateLabel(long Value)
{
    public static readonly LifeStateLabel Initial = new(0);

    public LifeStateLabel Next() => new(Value + 1);

    public override string ToString() => Value.ToString();
}
