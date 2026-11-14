using System.Text.Json;
using Moltaro.Lattice.Prototypes.OperationRetry;

if (args.Length != 5)
{
    throw new ArgumentException("mode output connection-file run-id scenario");
}

if (args[0] == "unique")
{
    WriteResult(args[1], new { Checks = UniqueIndexChecks.Run() });
    return;
}

var scope = new PgRetryScope(args[2], Guid.Parse(args[3]));
object result;
switch (args[0])
{
    case "setup":
        scope.Setup();
        result = new { Created = true };
        break;
    case "cleanup":
        scope.Cleanup();
        result = new { Removed = true };
        break;
    case "checks":
        result = new RetryChecks(scope).Run();
        break;
    case "crash":
    case "recover":
        var store = new RetryStore(scope, "crash_" + args[4]);
        var token = new OperationToken(Guid.Parse("77c98c9b-fabd-498a-9a09-d107c7230fdd"), 1,
            Guid.Parse("f7a98f81-e717-4688-9cd6-d9cd451de925"));
        var request = new RetryRequest(1, 2, 3);
        if (args[0] == "crash")
        {
            store.Initialize(token.History);
            store.CreateEpoch(0);
            store.Admit(token, request, 1);
            store.Execute(token, request, 2,
                beforeCommit: args[4] == "before" ? () => Environment.Exit(91) : null,
                afterCommit: args[4] == "after" ? () => Environment.Exit(92) : null);
            throw new InvalidOperationException("Crash hook was not reached.");
        }

        var initial = store.Status(token, 3);
        var expected = args[4] == "before" ? "Admitted" : "Committed";
        if (initial.State != expected || initial.Expires != 901)
        {
            throw new InvalidOperationException("Wrong recovered admission/result.");
        }

        store.Execute(token, request, 4);
        store.Execute(token, request, 5);
        if (store.Inspect() != (6, 2, 1L, 1L, 1L))
        {
            throw new InvalidOperationException("Recovery did not produce exactly one whole effect.");
        }

        result = new { Scenario = args[4], RecoveredState = initial.State, Validated = true };
        break;
    default:
        throw new ArgumentException("Unknown mode.");
}

WriteResult(args[1], result);

static void WriteResult(string path, object result)
{
    File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
}
