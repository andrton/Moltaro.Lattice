using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Moltaro.Lattice.Tests.Views;

public sealed class ConsumerCompilationTests
{
    private static readonly MetadataReference[] References = PlatformReferences();

    [Fact]
    public void A_consumer_can_traverse_each_view_and_return_owned_results()
    {
        Assert.Empty(ErrorsFrom("""
            public static void Read(ILatticeSet set, LatticeSession session)
            {
                set.TryView("store", static view => view["Title"].GetString(), out string? title);
                set.TryView("store", 10, static (view, extra) =>
                {
                    var list = view["Nested"].Owned["Items"].List;
                    var map = list[0].Map;
                    var sum = extra;
                    if (map.TryGetValue("stock", out var stock))
                        sum += stock.Get<int>();

                    foreach (var (key, value) in map)
                        sum += key.ByteLength + value.Get<int>();

                    foreach (var member in view["Flags"].Set)
                        sum += member.Get<int>();

                    foreach (var item in list)
                        sum += item.Map.Count;

                    var part = view["Title"].GetPart(0);
                    return sum + part.Length;
                }, out int total);
                _ = set.ViewAsync("store", session, static view => view["Title"].GetString());
                _ = set.ViewAsync("store", session, "Title", static (view, field) => view[field].GetPart(0).ToArray());
            }
            """));
    }

    [Theory]
    [InlineData("private LatticeRecordView _stored;", "CS8345")]
    [InlineData("private ReadOnlySpan<byte> _stored;", "CS8345")]
    [InlineData("public static void ReturnView(ILatticeSet set) => set.TryView(\"store\", static view => view, out LatticeRecordView result);", "CS9244")]
    [InlineData("public static void UseState(ILatticeSet set, scoped LatticeRecordView state) => set.TryView(\"store\", state, static (_, saved) => 0, out int result);", "CS9244")]
    [InlineData("public static void ReturnViewAsync(ILatticeSet set, LatticeSession session) => _ = set.ViewAsync(\"store\", session, static view => view[\"Title\"].GetPart(0));", "CS9244")]
    [InlineData("public static LatticeValueView Escape(scoped LatticeRecordView view) => view[\"Nested\"].Owned[\"Items\"].List[0];", "CS8352")]
    [InlineData("public static LatticeMapView.Enumerator Escape(scoped LatticeRecordView view) => view[\"Map\"].Map.GetEnumerator();", "CS8352")]
    [InlineData("public static LatticeValueView Escape(scoped LatticeRecordView view) => view[\"Map\"].Map.GetEnumerator().Current.Value;", "CS8352")]
    [InlineData("public static ReadOnlySpan<byte> Escape(scoped LatticeRecordView view) => view[\"Title\"].GetPart(0);", "CS8352")]
    [InlineData("public static void Escape(scoped LatticeRecordView view, out LatticeValueView result) => view[\"Map\"].Map.TryGetValue(\"stock\", out result);", "CS8352")]
    [InlineData("public static void Escape(scoped LatticeRecordView view, out LatticeValueView result) => view[\"Map\"].Map.GetEnumerator().Current.Deconstruct(out _, out result);", "CS8352")]
    [InlineData("public static void Escape(scoped LatticeRecordView view, ref LatticeValueView result) => Assign(ref result, view[\"Title\"]);", "CS8352")]
    [InlineData("public static void Escape(scoped LatticeRecordView view, out ReadOnlySpan<byte> result) => Assign(out result, view[\"Title\"].GetPart(0));", "CS8352")]
    [InlineData("public static Func<int> Capture(scoped LatticeRecordView view) => () => view[\"Title\"].ByteLength;", "CS9108")]
    [InlineData("public static void Read(ILatticeSet set) => set.TryView(\"store\", async (scoped LatticeRecordView view) => await Task.FromResult(1), out Task<int> result);", "CS4012")]
    [InlineData("public static IEnumerable<int> Read(scoped LatticeRecordView view) { yield return view[\"Title\"].ByteLength; }", "CS4007")]
    public void A_consumer_cannot_make_a_view_escape_its_scope(string member, string diagnostic)
    {
        var errors = ErrorsFrom(member);

        Assert.Contains(errors, error => error.Id == diagnostic && error.Location.GetLineSpan().StartLinePosition.Line == 8);
    }

    [Theory]
    [InlineData("Moltaro.Lattice.Access.LatticeViewFunc`2")]
    [InlineData("Moltaro.Lattice.Access.LatticeViewFunc`3")]
    public void Consumer_metadata_marks_only_the_view_delegate_parameter_scoped(string metadataName)
    {
        var compilation = Compile("");
        var invoke = compilation.GetTypeByMetadataName(metadataName)!.DelegateInvokeMethod!;

        Assert.Equal(ScopedKind.ScopedValue, invoke.Parameters[0].ScopedKind);
        Assert.True(invoke.ContainingType.TypeParameters[0].AllowsRefLikeType);
        Assert.All(invoke.ContainingType.TypeParameters.Skip(1), parameter => Assert.False(parameter.AllowsRefLikeType));
        Assert.All(invoke.Parameters.Skip(1), parameter => Assert.Equal(ScopedKind.None, parameter.ScopedKind));
    }

    private static Diagnostic[] ErrorsFrom(string member)
    {
        using var output = new MemoryStream();
        return Compile(member).Emit(output).Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
    }

    private static CSharpCompilation Compile(string member)
    {
        var source = $$"""
            #nullable enable
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Moltaro.Lattice;
            using Moltaro.Lattice.Access;
            public sealed class Consumer
            {
            {{member}}
                private static void Assign(ref LatticeValueView target, LatticeValueView value) => target = value;
                private static void Assign(out ReadOnlySpan<byte> target, ReadOnlySpan<byte> value) => target = value;
            }
            """;
        var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        return CSharpCompilation.Create(
            "ProtectedViewConsumer",
            [syntax],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static MetadataReference[] PlatformReferences()
    {
        var framework = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

        return platform.Split(Path.PathSeparator)
            .Where(path => Path.GetDirectoryName(path) == framework)
            .Append(typeof(ILatticeSet).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
