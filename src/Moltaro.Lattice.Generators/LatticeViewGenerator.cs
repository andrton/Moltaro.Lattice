using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Moltaro.Lattice.Generators.Metadata;
using Moltaro.Lattice.Generators.Projection;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Generators;

/// <summary>Generates protected view members for applications that opt in to typed views.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class LatticeViewGenerator : IIncrementalGenerator
{
    private const string ViewAttribute = "Moltaro.Lattice.Access.LatticeViewAttribute";
    private const string FieldAttribute = "Moltaro.Lattice.Access.LatticeViewFieldAttribute";

    private static readonly DiagnosticDescriptor CannotProject = new(
        "LTV001", "Lattice view cannot be generated", "{0}", "Lattice", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var views = context.SyntaxProvider.ForAttributeWithMetadataName(ViewAttribute,
            static (node, _) => node is TypeDeclarationSyntax,
            static (trigger, _) => (TypeDeclarationSyntax)trigger.TargetNode);
        var inputs = context.CompilationProvider.Combine(context.AdditionalTextsProvider.Collect()).Combine(views.Collect());
        context.RegisterSourceOutput(inputs, static (output, input) => Execute(output, input.Left.Left, input.Left.Right, input.Right));
    }

    private static void Execute(SourceProductionContext output, Compilation compilation, ImmutableArray<AdditionalText> additional,
        ImmutableArray<TypeDeclarationSyntax> views)
    {
        if (views.IsEmpty)
        {
            return;
        }

        var metadata = new ModelMetadata(compilation, additional, output.CancellationToken);
        var classes = new RoslynClassMetadata(metadata);
        var schemas = ReadSchemas(output, additional);
        if (schemas is null)
        {
            return;
        }

        var emitted = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var syntax in views)
        {
            output.CancellationToken.ThrowIfCancellationRequested();
            var view = (INamedTypeSymbol)metadata.Compilation.GetSemanticModel(syntax.SyntaxTree).GetDeclaredSymbol(syntax, output.CancellationToken)!;
            if (!emitted.Add(view))
            {
                continue;
            }

            try
            {
                CheckDeclaration(view, syntax);
                var attribute = view.GetAttributes().First(item => item.AttributeClass?.ToDisplayString() == ViewAttribute);
                if (attribute.ConstructorArguments is not [{ Value: INamedTypeSymbol { TypeKind: TypeKind.Class, IsUnboundGenericType: false } entity }])
                {
                    throw Failure($"View '{view.Name}' must name a closed .NET entity class.");
                }

                var builder = new ProjectionBuilder(classes, Hints(view));
                var fields = schemas.Count == 0
                    ? builder.WithoutSchema(entity)
                    : builder.FromSchema(entity, FindEntity(schemas, classes.FullName(entity)!));
                var hintName = view.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hintName)));
                output.AddSource($"{view.Name}.{identity[..16]}.g.cs", new ViewEmitter(view, entity, fields).Emit());
            }
            catch (LatticeException error)
            {
                output.ReportDiagnostic(Diagnostic.Create(CannotProject, syntax.Identifier.GetLocation(), error.Message));
            }
        }
    }

    private static List<LatticeSchema>? ReadSchemas(SourceProductionContext output, ImmutableArray<AdditionalText> additional)
    {
        var schemas = new List<LatticeSchema>();
        var invalid = false;
        foreach (var file in additional)
        {
            if (!file.Path.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var text = file.GetText(output.CancellationToken)
                    ?? throw Failure("The schema file could not be read.");
                schemas.Add(LatticeSchema.Compile(text.ToString()));
            }
            catch (LatticeException error)
            {
                output.ReportDiagnostic(Diagnostic.Create(CannotProject, Location.Create(file.Path, default, default), error.Message));
                invalid = true;
            }
        }

        return invalid ? null : schemas;
    }

    private static LatticeEntityType FindEntity(List<LatticeSchema> schemas, string className)
    {
        var matches = schemas.SelectMany(schema => schema.EntityTypes)
            .Where(entity => entity.ClrTypeName == className).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw Failure($"The supplied schema declares no entity for CLR class '{className}'."),
            _ => throw Failure($"More than one supplied schema declares CLR class '{className}'."),
        };
    }

    private static Dictionary<string, ViewHint> Hints(INamedTypeSymbol view)
    {
        var result = new Dictionary<string, ViewHint>(StringComparer.Ordinal);
        foreach (var attribute in view.GetAttributes().Where(item => item.AttributeClass?.ToDisplayString() == FieldAttribute))
        {
            if (attribute.ConstructorArguments.IsEmpty || attribute.ConstructorArguments[0].Value is not string { Length: > 0 } path)
            {
                throw Failure($"A field hint on view '{view.Name}' needs a field path.");
            }

            var kind = attribute.ConstructorArguments.Length == 2 && attribute.ConstructorArguments[1].Value is { } value
                ? (LatticeFieldTypeEnum?)Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
                : null;
            bool? nullable = null;
            var ignore = false;
            foreach (var named in attribute.NamedArguments)
            {
                switch (named.Key)
                {
                    case "IsNullable":
                        nullable = (bool)named.Value.Value!;
                        break;
                    case "Ignore":
                        ignore = (bool)named.Value.Value!;
                        break;
                }
            }

            if (!result.TryAdd(path, new ViewHint(kind, nullable, ignore)))
            {
                throw ProjectionBuilder.Failure(path, "more than one field hint was supplied");
            }
        }

        return result;
    }

    private static void CheckDeclaration(INamedTypeSymbol view, TypeDeclarationSyntax syntax)
    {
        if (view is not { TypeKind: TypeKind.Struct, IsRefLikeType: true, IsReadOnly: true } ||
            !syntax.Modifiers.Any(SyntaxKind.PartialKeyword) || view.IsFileLocal)
        {
            throw Failure($"Declare view '{view.Name}' as a readonly ref partial struct accessible from another source file.");
        }

        for (var enclosing = view.ContainingType; enclosing is not null; enclosing = enclosing.ContainingType)
        {
            if (enclosing.IsFileLocal || enclosing.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax() is TypeDeclarationSyntax declaration && !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                throw Failure($"Containing type '{enclosing.Name}' of view '{view.Name}' must be partial and accessible from another source file.");
            }
        }
    }

    private static LatticeException Failure(string message) => new(LatticeErrorEnum.BindingFailed, message);
}
