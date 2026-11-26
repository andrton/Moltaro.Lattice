namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed class WideBoundBranch<T>
    where T : class
{
    public T Part0 { get; set; } = null!;
    public T Part1 { get; set; } = null!;
    public T Part2 { get; set; } = null!;
    public T Part3 { get; set; } = null!;
    public T Part4 { get; set; } = null!;
    public T Part5 { get; set; } = null!;
    public T Part6 { get; set; } = null!;
    public T Part7 { get; set; } = null!;

    public T At(int index) => index switch
    {
        0 => Part0,
        1 => Part1,
        2 => Part2,
        3 => Part3,
        4 => Part4,
        5 => Part5,
        6 => Part6,
        7 => Part7,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static WideBoundBranch<T> Create(Func<int, T> make) => new()
    {
        Part0 = make(0),
        Part1 = make(1),
        Part2 = make(2),
        Part3 = make(3),
        Part4 = make(4),
        Part5 = make(5),
        Part6 = make(6),
        Part7 = make(7),
    };
}
