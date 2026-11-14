using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.AspNet.Identity;

/// <summary>
/// The part of a database schema the Identity stores keep their data in: the default
/// declaration, and the check that a schema supplied by the application has what the
/// stores need.
/// </summary>
/// <remarks>
/// <para>
/// <b>The application owns the schema.</b> A Lattice database has one schema, and the
/// application compiles it. This package does not add anything to it at run time. It
/// provides a default declaration of the Identity entity types, which an application uses
/// as it is, or takes as a file, changes and compiles together with its own entity types:
/// the file is <c>identity.schema.json</c> in the package sources, and
/// <see cref="CreateDescriptor{TUser, TRole}"/> returns the same text.
/// </para>
/// <para>
/// <b>What the stores need.</b> Seven entity types:
/// </para>
/// <list type="table">
/// <item><term>the user</term><description>The entity type bound to the application's user class, under any name. The fields of <see cref="IdentityUser{TKey}"/>; unique index <c>ByNormalizedUserName</c>, index <c>ByNormalizedEmail</c>.</description></item>
/// <item><term>the role</term><description>The entity type bound to the application's role class, under any name. The fields of <see cref="IdentityRole{TKey}"/>; unique index <c>ByNormalizedName</c>.</description></item>
/// <item><term>IdentityUserRole</term><description>One role of one user. Unique index <c>ByUserAndRole</c>, index <c>ByRole</c>.</description></item>
/// <item><term>IdentityUserClaim</term><description>One claim of one user. Indexes <c>ByUser</c> and <c>ByClaim</c>.</description></item>
/// <item><term>IdentityUserLogin</term><description>One external login. Unique index <c>ByProviderAndKey</c>, index <c>ByUser</c>.</description></item>
/// <item><term>IdentityUserToken</term><description>One named value of a user: authentication tokens, the authenticator key, recovery codes. Unique index <c>ByUserProviderAndName</c>.</description></item>
/// <item><term>IdentityRoleClaim</term><description>One claim of one role. Index <c>ByRole</c>.</description></item>
/// </list>
/// <para>
/// The default declaration is the exact list of fields and indexes. Uniqueness of user
/// names, role names, external logins and memberships is kept by the unique indexes of
/// the database, not by the stores.
/// </para>
/// <para>
/// <b>What the application may change.</b> The classes of the user and the role, and the
/// names of their entity types. Fields for the properties a class adds: a property is
/// stored only when the schema declares a field for it. More indexes, and entity types of
/// its own next to these. It may not remove or retype a field or an index listed in the
/// default declaration, or make a unique index non-unique; <see cref="Validate{TUser, TRole}"/>
/// refuses such a schema.
/// </para>
/// </remarks>
public static class LatticeIdentitySchema
{
    private const string UserEntityName = "IdentityUser";
    private const string RoleEntityName = "IdentityRole";

    // The default declaration is also the statement of what the stores need: a schema is
    // valid when it contains everything declared here.
    private static readonly string DefaultDescriptor = ReadDefaultDescriptor();
    private static readonly LatticeSchema Default = LatticeSchema.Compile(DefaultDescriptor);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Returns the default JSON schema descriptor of the Identity data, with the user and
    /// the role entity types bound to the given classes.
    /// </summary>
    /// <remarks>
    /// Compile the result with <see cref="LatticeSchema.Compile"/> for a database that holds
    /// the Identity data and nothing else, with classes that add no stored properties. For
    /// anything more, save the text as the application's schema file and edit it: add the
    /// fields of the application's classes and the application's own entity types.
    /// </remarks>
    /// <typeparam name="TUser">The application's user class.</typeparam>
    /// <typeparam name="TRole">The application's role class.</typeparam>
    public static string CreateDescriptor<TUser, TRole>()
        where TUser : IdentityUser<string>
        where TRole : IdentityRole<string>
    {
        var descriptor = JsonNode.Parse(DefaultDescriptor)!;
        foreach (var table in descriptor["EntityTypes"]!.AsArray())
        {
            var name = table!["Name"]!.GetValue<string>();
            if (name == UserEntityName)
            {
                table["ClrType"] = typeof(TUser).FullName;
            }
            else if (name == RoleEntityName)
            {
                table["ClrType"] = typeof(TRole).FullName;
            }
        }

        return descriptor.ToJsonString(Indented);
    }

    /// <summary>
    /// Checks that a schema has everything the Identity stores need for the given user and
    /// role classes. The stores make the same check when they are created; an application
    /// calls it to learn about a mistake where it compiles its schema.
    /// </summary>
    /// <typeparam name="TUser">The application's user class.</typeparam>
    /// <typeparam name="TRole">The application's role class.</typeparam>
    /// <param name="schema">The schema of the database that will hold the Identity data.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>: the schema lacks an entity type, a
    /// field or an index, or declares one differently. The message lists everything found.
    /// </exception>
    public static void Validate<TUser, TRole>(LatticeSchema schema)
        where TUser : IdentityUser<string>
        where TRole : IdentityRole<string> =>
        Validate(schema, typeof(TUser), typeof(TRole));

    internal static void Validate(LatticeSchema schema, Type userType, Type roleType)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var problems = new List<string>();
        foreach (var required in Default.EntityTypes)
        {
            if (Find(schema, required, userType, roleType, problems) is { } actual)
            {
                Compare(required, actual, problems);
            }
        }

        if (problems.Count > 0)
        {
            throw new LatticeException(
                LatticeErrorEnum.InvalidSchema,
                $"The schema cannot hold the Identity data: {string.Join("; ", problems)}. Compare it with the default declaration, identity.schema.json.");
        }
    }

    // The user and the role are found by their classes, under whatever name the
    // application gave them; the other entity types by their names.
    private static LatticeEntityType? Find(LatticeSchema schema, LatticeEntityType required, Type userType, Type roleType, List<string> problems)
    {
        if (required.Name is UserEntityName or RoleEntityName)
        {
            var clrType = required.Name == UserEntityName ? userType : roleType;
            if (schema.TryGetEntityType(clrType, out var bound))
            {
                return bound;
            }

            problems.Add($"no entity type is declared for the class {clrType.FullName} (\"ClrType\")");
            return null;
        }

        if (schema.TryGetEntityType(required.Name, out var named))
        {
            return named;
        }

        problems.Add($"entity type '{required.Name}' is missing");
        return null;
    }

    private static void Compare(LatticeEntityType required, LatticeEntityType actual, List<string> problems)
    {
        if (actual.Key.Name != required.Key.Name)
        {
            problems.Add($"the primary key of '{actual.Name}' must be '{required.Key.Name}'");
        }

        foreach (var field in required.Fields)
        {
            if (!actual.TryGetField(field.Name, out var found))
            {
                problems.Add($"field '{actual.Name}.{field.Name}' is missing");
            }
            else if (found.Declaration.Type != field.Declaration.Type || found.Declaration.IsNullable != field.Declaration.IsNullable)
            {
                var nullable = field.Declaration.IsNullable ? "nullable " : "";
                problems.Add($"field '{actual.Name}.{field.Name}' must be a {nullable}{field.Declaration.Type}");
            }
        }

        foreach (var index in required.Indexes)
        {
            var fields = string.Join(", ", index.Fields.Select(field => field.Name));
            if (!actual.TryGetIndex(index.Name, out var found))
            {
                problems.Add($"index '{actual.Name}.{index.Name}' on ({fields}) is missing");
            }
            else if (!found.Fields.Select(field => field.Name).SequenceEqual(index.Fields.Select(field => field.Name)))
            {
                problems.Add($"index '{actual.Name}.{index.Name}' must be on ({fields})");
            }
            else if (index.IsUnique && !found.IsUnique)
            {
                problems.Add($"index '{actual.Name}.{index.Name}' must be unique");
            }
        }
    }

    private static string ReadDefaultDescriptor()
    {
        using var stream = typeof(LatticeIdentitySchema).Assembly.GetManifestResourceStream("identity.schema.json")
            ?? throw new InvalidOperationException("The Identity schema descriptor is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
