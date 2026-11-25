using Moltaro.Lattice.TestSupport.Views;

namespace Moltaro.Lattice.Tests.Views;

public sealed class SdkTypedViewTests
{
    private const string CorePackage = "Moltaro.Lattice";
    private const string EnginePackage = "Moltaro.Lattice.Engine";
    private const string AnalyzerPackage = "Moltaro.Lattice.Generators";
    private const string ModelPackage = "ViewModels";

    private const string ModelProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ProduceReferenceAssembly>true</ProduceReferenceAssembly>
            <NoWarn>$(NoWarn);NU5131;CA2255</NoWarn>
            <TargetsForTfmSpecificContentInPackage>$(TargetsForTfmSpecificContentInPackage);PackReferenceAssembly</TargetsForTfmSpecificContentInPackage>
          </PropertyGroup>
          <Target Name="PackReferenceAssembly">
            <ItemGroup>
              <TfmSpecificPackageFile Include="$(TargetRefPath)" PackagePath="ref/$(TargetFramework)/" />
            </ItemGroup>
          </Target>
        </Project>
        """;

    private const string ModelSource = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Runtime.CompilerServices;

        namespace ViewModels;

        public class EntryBase
        {
            public int Age { get; private set; }

            public int GetterOnly => 81;

            public void ChangeAge(int age) => Age = age;
        }

        public sealed class Entry : EntryBase
        {
            public int Id { get; set; }

            public byte[] Data { get; set; } = [];

            public List<int>? Weights { get; set; }

            public Dictionary<string, int> Mappings { get; set; } = [];
        }

        internal static class ModelGuard
        {
            [ModuleInitializer]
            internal static void MarkCodeExecution()
            {
                if (Environment.GetEnvironmentVariable("LATTICE_BUILD_MARKER") is { } path)
                {
                    File.WriteAllText(path, "The consumer model executed.");
                }
            }
        }
        """;

    private const string ModelSchema = """
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Entry", "ClrType": "ViewModels.Entry",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int32" },
                { "Id": 2, "Name": "Age", "Type": "Int32" },
                { "Id": 3, "Name": "Data", "Type": "Binary" },
                { "Id": 4, "Name": "Weights", "Type": "List", "Element": { "Type": "Int32" } },
                { "Id": 5, "Name": "Mappings", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "Int32" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    private const string ViewDeclaration = """
        using Moltaro.Lattice.Access;
        using ViewModels;

        [LatticeView(typeof(Entry))]
        public readonly ref partial struct EntryView;
        """;

    private const string ConsumerSource = """
        using System;
        using System.IO;
        using Moltaro.Lattice;
        using Moltaro.Lattice.Access;
        using Moltaro.Lattice.Schema;
        using ViewModels;

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            Schema = LatticeSchema.Compile(File.ReadAllText("entry.schema.json")),
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        });
        var items = database.Set<Entry>();
        var records = database.Set("Entry");
        var item = new Entry { Id = 1, Data = [42], Weights = [17, 23] };
        item.ChangeAge(41);
        await items.InsertAsync(item);
        if (!items.TryView(1, static (EntryView view) => (view.Age, view.Data.GetPart(0)[0], view.Weights.Count), out var byClass) ||
            !records.TryView(1, static view => (view["Age"].Get<int>(), view["Data"].GetPart(0)[0], view["Weights"].List.Count), out var byName) ||
            byClass != byName || byClass != (41, (byte)42, 2))
        {
            throw new InvalidOperationException("The inherited private setter did not produce the expected protected value.");
        }

        Console.WriteLine("Protected values: 41 42 2");
        """;

    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task An_SDK_consumer_uses_the_package_analyzer_and_binds_inherited_private_accessors_against_implementation_metadata()
    {
        using var sdk = new SdkViewProject();
        await PackViewPackagesAsync(sdk);
        sdk.Write(ModelPackage, ModelPackage + ".csproj", ModelProject);
        sdk.Write(ModelPackage, "Entry.cs", ModelSource);
        await sdk.PackModelsAsync(ModelPackage);

        const string library = "ViewLibrary";
        sdk.Write(library, library + ".csproj", ConsumerProject(executable: false, analyzer: true, schema: true));
        sdk.Write(library, "EntryView.cs", ViewDeclaration);
        sdk.Write(library, "entry.schema.json", ModelSchema);

        const string application = "ViewConsumer";
        sdk.Write(application, application + ".csproj", ConsumerProject(executable: true, analyzer: false, schema: false,
            """<ProjectReference Include="../ViewLibrary/ViewLibrary.csproj" />"""));
        sdk.Write(application, "Program.cs", ConsumerSource);
        sdk.Write(application, "entry.schema.json", ModelSchema);
        var marker = sdk.FileOf("", "model-executed");
        var built = await sdk.BuildAsync(application);
        Assert.True(built.ExitCode == 0, built.Output);
        Assert.False(File.Exists(marker));

        var ran = await sdk.RunAsync(application);
        Assert.True(ran.ExitCode == 0, ran.Output);
        Assert.Contains("Protected values: 41 42 2", ran.Output, StringComparison.Ordinal);
        Assert.Equal("The consumer model executed.", File.ReadAllText(marker));
        File.Delete(marker);

        foreach (var (oldText, newText, field) in new[]
        {
            ("\"Age\", \"Type\": \"Int32\"", "\"Age\", \"Type\": \"String\"", "Age"),
            ("\"Age\", \"Type\": \"Int32\"", "\"GetterOnly\", \"Type\": \"Int32\"", "GetterOnly"),
            ("\"Element\": { \"Type\": \"Int32\" }", "\"Element\": { \"Type\": \"Int64\" }", "Weights"),
        })
        {
            sdk.Write(library, "entry.schema.json", ModelSchema.Replace(oldText, newText, StringComparison.Ordinal));
            var rejected = await sdk.BuildAsync(library);
            AssertNamedDiagnostic(rejected, field);
            Assert.False(File.Exists(marker));
        }

        sdk.Write(library, "entry.schema.json", ModelSchema);
        sdk.Write(library, "EntryView.cs", ViewDeclaration.Replace("[LatticeView(typeof(Entry))]", """
            [LatticeView(typeof(Entry))]
            [LatticeViewField(nameof(Entry.Age), Ignore = true)]
            """, StringComparison.Ordinal));
        AssertNamedDiagnostic(await sdk.BuildAsync(library), "Age");

        sdk.Write(library, library + ".csproj", ConsumerProject(executable: false, analyzer: true, schema: false));
        sdk.Write(library, "EntryView.cs", ViewDeclaration);
        AssertNamedDiagnostic(await sdk.BuildAsync(library), "Data");

        sdk.Write(library, "EntryView.cs", ViewDeclaration.Replace("[LatticeView(typeof(Entry))]", """
            [LatticeView(typeof(Entry))]
            [LatticeViewField(nameof(Entry.Data), Moltaro.Lattice.Schema.LatticeFieldTypeEnum.Map)]
            """, StringComparison.Ordinal));
        AssertNamedDiagnostic(await sdk.BuildAsync(library), "Data");

        sdk.Write(library, "EntryView.cs", """
            using System.Collections.Generic;
            using Moltaro.Lattice.Access;

            [LatticeView(typeof(ObliviousEntry))]
            public readonly ref partial struct ObliviousView;

            #nullable disable
            public sealed class ObliviousEntry
            {
                public List<int> Data { get; set; }
            }
            """);
        AssertNamedDiagnostic(await sdk.BuildAsync(library), "Data");
    }

    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task The_compiler_refuses_the_same_lifetime_escapes_for_sdk_generated_views()
    {
        using var sdk = new SdkViewProject();
        await PackViewPackagesAsync(sdk);
        sdk.Write(ModelPackage, ModelPackage + ".csproj", ModelProject);
        sdk.Write(ModelPackage, "Entry.cs", ModelSource);
        await sdk.PackModelsAsync(ModelPackage);

        const string project = "BorrowingConsumer";
        sdk.Write(project, project + ".csproj", ConsumerProject(executable: false, analyzer: true, schema: true));
        sdk.Write(project, "entry.schema.json", ModelSchema);
        sdk.Write(project, "EntryView.cs", ViewDeclaration);
        var compiled = await sdk.BuildAsync(project);
        Assert.True(compiled.ExitCode == 0, compiled.Output);

        // A failed member declaration can stop the compiler before it checks method bodies.
        await ExpectMisusesAsync(sdk, project,
        [
            ("private EntryView _stored;", "CS8345"),
            ("private ReadOnlySpan<byte> _storedBytes;", "CS8345"),
        ]);

        await ExpectMisusesAsync(sdk, project,
        [
            ("public static void Return(ILatticeSet<Entry> rows) => rows.TryView(1, static (EntryView v) => v, out EntryView result);", "CS9244"),
            ("public static void State(ILatticeSet<Entry> rows, scoped EntryView v) => rows.TryView(1, v, static (EntryView _, EntryView saved) => 0, out int result);", "CS9244"),
            ("public static void ReturnAsync(ILatticeSet<Entry> rows, LatticeSession session) => _ = rows.ViewAsync(1, session, static (EntryView v) => v.Data.GetPart(0));", "CS9244"),
            ("public static Func<int> Capture(scoped EntryView v) => () => v.Age;", "CS9108"),
            ("public static ReadOnlySpan<byte> Escape(scoped EntryView v) => v.Data.GetPart(0);", "CS8352"),
            ("public static LatticeListView Collection(scoped EntryView v) => v.Weights;", "CS8352"),
            ("public static LatticeValueView ListItem(scoped EntryView v) => v.Weights[0];", "CS8352"),
            ("public static LatticeListView.Enumerator ListCursor(scoped EntryView v) => v.Weights.GetEnumerator();", "CS8352"),
            ("public static LatticeValueView ListCurrent(scoped EntryView v) => v.Weights.GetEnumerator().Current;", "CS8352"),
            ("public static LatticeMapView.Enumerator MapCursor(scoped EntryView v) => v.Mappings.GetEnumerator();", "CS8352"),
            ("public static LatticeValueView MapCurrent(scoped EntryView v) => v.Mappings.GetEnumerator().Current.Value;", "CS8352"),
            ("public static void MapLookup(scoped EntryView v, out LatticeValueView result) => v.Mappings.TryGetValue(\"Ada\", out result);", "CS8352"),
            ("public static void MapEntry(scoped EntryView v, out LatticeValueView result) => v.Mappings.GetEnumerator().Current.Deconstruct(out _, out result);", "CS8352"),
            ("public static void StoreThroughRef(scoped EntryView v, ref LatticeValueView result) => Save(ref result, v.Data);", "CS8352"),
            ("public static void StoreThroughOut(scoped EntryView v, out ReadOnlySpan<byte> result) => Save(out result, v.Data.GetPart(0));", "CS8352"),
            ("public static void Change(ILatticeSet<Entry> rows) => rows.TryView(1, static (EntryView v) => v.Data.GetPart(0)[0] = 42, out byte result);", "CS8331"),
            ("public static void Await(ILatticeSet<Entry> rows) => rows.TryView(1, async (scoped EntryView v) => await Task.FromResult(1), out Task<int> result);", "CS4012"),
            ("public static IEnumerable<int> Yield(scoped EntryView v) { yield return v.Age; }", "CS4007"),
            ("public static LatticeOptionalView<LatticeValueView> EscapeOptional(scoped LatticeOptionalView<LatticeValueView> v) => v;", "CS8352"),
        ]);
    }

    private static async Task ExpectMisusesAsync(SdkViewProject sdk, string project, (string Code, string Diagnostic)[] misuses)
    {
        // Each declaration occupies a known line so a common failure cannot satisfy
        // a second case with the same compiler diagnostic.
        string[] header =
        [
            "using System;", "using System.Collections.Generic;", "using System.Threading.Tasks;",
            "using Moltaro.Lattice;", "using Moltaro.Lattice.Access;", "using ViewModels;",
            "public sealed class Misuse", "{",
            "private static void Save(ref LatticeValueView target, LatticeValueView value) => target = value;",
            "private static void Save(out ReadOnlySpan<byte> target, ReadOnlySpan<byte> value) => target = value;",
        ];
        sdk.Write(project, "Misuse.cs", string.Join("\n", header.Concat(misuses.Select(misuse => misuse.Code)).Append("}")));
        var rejected = await sdk.BuildAsync(project);
        Assert.NotEqual(0, rejected.ExitCode);

        for (var index = 0; index < misuses.Length; index++)
        {
            var line = header.Length + index + 1;
            var diagnostic = misuses[index].Diagnostic;
            Assert.Matches($@"Misuse\.cs\({line},\d+\): error {diagnostic}\b", rejected.Output);
        }
    }

    [Fact]
    public async Task The_sdk_builds_and_runs_an_ordinary_consumer_without_the_analyzer()
    {
        using var sdk = new SdkViewProject();
        await sdk.PackBuiltAsync(CorePackage);
        await sdk.PackBuiltAsync(EnginePackage);

        const string project = "OrdinaryConsumer";
        sdk.Write(project, project + ".csproj", ConsumerProject(executable: true, analyzer: false, schema: false));
        sdk.Write(project, "Program.cs", """"
            using System;
            using Moltaro.Lattice;
            using Moltaro.Lattice.Access;
            using Moltaro.Lattice.Schema;

            var schema = LatticeSchema.Compile("""
                { "EntityTypes": [ { "Id": 1, "Name": "Item",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int32" } ], "PrimaryKey": [1] } ] }
                """);
            await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
            {
                Schema = schema,
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            });
            var items = database.Set("Item");
            await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 7 });
            if (!items.TryView(7, static view => view["Id"].Get<int>(), out var id) || id != 7)
            {
                throw new InvalidOperationException("The ordinary consumer did not read the record.");
            }

            Console.WriteLine("Ordinary protected view: 7");
            """");
        var built = await sdk.BuildAsync(project);
        Assert.True(built.ExitCode == 0, built.Output);
        var ran = await sdk.RunAsync(project);
        Assert.True(ran.ExitCode == 0, ran.Output);
        Assert.Contains("Ordinary protected view: 7", ran.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(AnalyzerPackage, File.ReadAllText(sdk.FileOf(project, "obj/project.assets.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public void Of_the_shipped_assemblies_only_the_source_generator_references_the_compiler()
    {
        using var sdk = new SdkViewProject();
        foreach (var assembly in new[] { CorePackage, EnginePackage, "Moltaro.Lattice.DependencyInjection", "Moltaro.Lattice.AspNet.Identity" })
        {
            Assert.DoesNotContain(sdk.References(assembly), IsCompilerReference);
        }

        Assert.Contains(sdk.References(AnalyzerPackage), IsCompilerReference);
    }

    private static string ConsumerProject(bool executable, bool analyzer, bool schema, string reference = "") => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <OutputType>{{(executable ? "Exe" : "Library")}}</OutputType>
            <Nullable>enable</Nullable>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <UseSharedCompilation>false</UseSharedCompilation>
            <NuGetAudit>false</NuGetAudit>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="{{EnginePackage}}" Version="{{SdkViewProject.Version}}" />
            {{(analyzer ? Package(AnalyzerPackage) : "")}}
            {{(analyzer ? Package(ModelPackage) : "")}}
            {{(schema ? """<AdditionalFiles Include="entry.schema.json" />""" : "")}}
            {{reference}}
          </ItemGroup>
        </Project>
        """;

    private static string Package(string name) => $"""<PackageReference Include="{name}" Version="{SdkViewProject.Version}" />""";

    private static async Task PackViewPackagesAsync(SdkViewProject sdk)
    {
        await sdk.PackBuiltAsync(CorePackage);
        await sdk.PackBuiltAsync(EnginePackage);
        await sdk.PackBuiltAsync(AnalyzerPackage);
    }

    private static void AssertNamedDiagnostic((int ExitCode, string Output) built, string field)
    {
        Assert.NotEqual(0, built.ExitCode);
        var diagnostics = built.Output.Split('\n');
        Assert.Contains(diagnostics, message => message.Contains("error LTV", StringComparison.Ordinal) &&
            message.Contains($"'{field}'", StringComparison.Ordinal));
    }

    private static bool IsCompilerReference(string name) =>
        name == "Microsoft.CodeAnalysis" || name.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal);
}
