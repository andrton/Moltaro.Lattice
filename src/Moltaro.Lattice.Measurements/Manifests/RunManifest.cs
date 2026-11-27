namespace Moltaro.Lattice.Measurements.Manifests;

// The manifest a run of the measurement program is made under (docs/work/epic-1/acceptance.md,
// rules for every measurement): a committed file of this folder, named on the command line, which
// says on what machine and with what software the figures were produced. A result without its
// manifest is not evidence, so the program puts the whole manifest into the report of every run.
// The files are copied next to the program when it is built, and found there by their name.
internal sealed class RunManifest
{
    private RunManifest(string name, string text)
    {
        Name = name;
        Text = text;
    }

    public string Name { get; }

    // The manifest as it is committed.
    public string Text { get; }

    // The manifest of this name, or null when there is none.
    public static RunManifest? Find(string name)
    {
        if (!Available().Contains(name, StringComparer.Ordinal))
        {
            return null;
        }

        return new RunManifest(name, File.ReadAllText(PathOf(name)));
    }

    // The names of the manifests the program has.
    public static IReadOnlyList<string> Available() =>
        [.. Directory.EnumerateFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)];

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Manifests");

    private static string PathOf(string name) => Path.Combine(Folder, name + ".json");
}
