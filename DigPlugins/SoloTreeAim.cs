namespace AutoTreasureHunt;

// Narrow the possible target positions using the server's hit quality and aim
// for the position with the highest expected damage for this tree.
internal sealed class SoloTreeAim
{
    private List<int> candidates = Enumerable.Range(0, 101).ToList();
    private readonly HashSet<int> tried = [];
    public uint Position { get; private set; } = 50;

    public void Observe(int quality)
    {
        var previous = (int)Position;
        tried.Add(previous);
        var narrowed = candidates.Where(target => Predict(previous, target) == quality).ToList();
        candidates = narrowed.Count > 0 ? narrowed : Enumerable.Range(0, 101).ToList();
        Position = (uint)Enumerable.Range(0, 101)
            .Where(guess => !tried.Contains(guess))
            .OrderByDescending(guess => candidates.Sum(target => Damage(Predict(guess, target))))
            .ThenByDescending(guess => candidates.Count(target => Predict(guess, target) == 3))
            .ThenBy(guess => candidates.GroupBy(target => Predict(guess, target)).Sum(group => group.Count() * group.Count()))
            .ThenBy(guess => Math.Abs(guess - candidates.Average()))
            .DefaultIfEmpty(previous)
            .First();
    }

    private static int Damage(int quality) => quality switch
    {
        3 => 10,
        2 => 4,
        1 => 1,
        _ => 0,
    };

    // Fitted against 41 live server trees: the old 5/10/25 bands contradicted
    // feedback often and discarded useful target estimates. The captured
    // ratings consistently fit 2/10/19 position-distance bands.
    private static int Predict(int guess, int target) => Math.Abs(guess - target) switch
    {
        <= 2 => 3,
        <= 10 => 2,
        <= 19 => 1,
        _ => 0,
    };
}
