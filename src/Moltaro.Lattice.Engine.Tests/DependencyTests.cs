using System.Reflection;
using System.Xml.Linq;

namespace Moltaro.Lattice.Engine.Tests;

// What the built engine assembly depends on: the contract, and the index package at exactly the
// version the evidence was measured on (docs/decisions/index-structures.md). Three checks, because
// each sees a case the others cannot.
// - The references of the built assembly name every assembly its code uses, also one of a
//   package marked PrivateAssets="all", which no consumer receives. The compiler leaves out a
//   reference that no code uses: the assembly references the contract or Arc.Collections only
//   while code of the engine uses it, so this check allows them but cannot require them.
// - The dependency manifest that the build writes next to this test assembly lists every package
//   and project reference that a referencing project receives, used or not. It does not list a
//   package marked PrivateAssets="all".
// - Both see the version the build resolved, and a bare version or a range can resolve to 2.2.0
//   as well. Only the exact pin keeps the package from floating, also in the dependency of the
//   engine package, so the third check reads the declaration itself in the central package
//   versions (Q3 of docs/work/open-questions.md).
//
// By the owner's decision of 18 November 2026, which rides in Epic 1, sprint S13, the engine uses no
// code of Arc.Collections and the unused package leaves it: the engine then references no package at
// all, only the contract, no project of the product solution references Arc.Collections, and the central
// package versions do not declare it. The prototypes under src/Prototypes pin their own versions and are
// not projects of the product solution. The checks of what holds after that change follow the three
// above, which state the rule before it; the project files and the central package versions are read
// where the build of this project read them, above the directory of this test assembly.
public sealed class DependencyTests
{
    [Fact]
    public void The_engine_assembly_references_only_the_contract_Arc_Collections_and_assemblies_of_dotnet()
    {
        var engine = Assembly.Load("Moltaro.Lattice.Engine");
        string[] allowed = ["Moltaro.Lattice", "Arc.Collections"];

        var others = engine.GetReferencedAssemblies()
            .Where(reference => !allowed.Contains(reference.Name) && !BuiltDependencies.IsPartOfDotnet(reference))
            .Select(reference => reference.FullName);

        Assert.Empty(others);
    }

    [Fact]
    public void The_engine_depends_only_on_the_contract_and_on_Arc_Collections_2_2_0()
    {
        var dependencies = BuiltDependencies.Recorded(typeof(DependencyTests).Assembly, "Moltaro.Lattice.Engine");

        Assert.Equal(["Arc.Collections", "Moltaro.Lattice"], dependencies.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("2.2.0", dependencies["Arc.Collections"]);
    }

    [Fact]
    public void Arc_Collections_is_declared_with_the_exact_pin_of_version_2_2_0()
    {
        var declarations = XDocument.Load(AboveTheTestAssembly("Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Where(package => (string?)package.Attribute("Include") == "Arc.Collections");

        // The brackets make it the exact version; without them it would be the lowest acceptable.
        Assert.Equal("[2.2.0]", (string?)Assert.Single(declarations).Attribute("Version"));
    }

    [Fact]
    public void The_engine_assembly_references_only_the_contract_and_assemblies_of_dotnet()
    {
        var engine = Assembly.Load("Moltaro.Lattice.Engine");

        var others = engine.GetReferencedAssemblies()
            .Where(reference => reference.Name != "Moltaro.Lattice" && !BuiltDependencies.IsPartOfDotnet(reference))
            .Select(reference => reference.FullName);

        Assert.Empty(others);
    }

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public void The_engine_depends_only_on_the_contract()
    {
        var dependencies = BuiltDependencies.Recorded(typeof(DependencyTests).Assembly, "Moltaro.Lattice.Engine");

        Assert.Equal(["Moltaro.Lattice"], dependencies.Keys);
    }

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public void The_engine_project_references_no_package()
    {
        var engine = XDocument.Load(Path.Combine(Path.GetDirectoryName(AboveTheTestAssembly("Moltaro.Lattice.slnx"))!, "Moltaro.Lattice.Engine", "Moltaro.Lattice.Engine.csproj"));

        Assert.Empty(engine.Descendants("PackageReference"));
    }

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public void No_project_of_the_product_solution_references_Arc_Collections()
    {
        var solution = AboveTheTestAssembly("Moltaro.Lattice.slnx");
        var projects = XDocument.Load(solution).Descendants("Project").Select(project => (string)project.Attribute("Path")!).ToArray();

        Assert.Contains("Moltaro.Lattice.Engine/Moltaro.Lattice.Engine.csproj", projects);

        foreach (var project in projects)
        {
            var references = XDocument.Load(Path.Combine(Path.GetDirectoryName(solution)!, project))
                .Descendants("PackageReference")
                .Where(package => (string?)package.Attribute("Include") == "Arc.Collections");

            Assert.True(!references.Any(), $"The project {project} references Arc.Collections.");
        }
    }

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public void The_central_package_versions_do_not_declare_Arc_Collections()
    {
        var declarations = XDocument.Load(AboveTheTestAssembly("Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Where(package => (string?)package.Attribute("Include") == "Arc.Collections");

        Assert.Empty(declarations);
    }

    // The file of the name that the build of this project read: the nearest one above the directory of
    // this test assembly, which lies inside the project, so inside src.
    private static string AboveTheTestAssembly(string fileName)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(DependencyTests).Assembly.Location)!);

        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, fileName);

            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"No {fileName} lies above the test assembly.");
    }
}
