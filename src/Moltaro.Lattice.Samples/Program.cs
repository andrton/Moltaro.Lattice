using Moltaro.Lattice.Samples;

var schema = SchemaSample.Run();
SchemaDeclarationsSample.Run();

await BasicCrudSample.RunAsync(schema);
await DynamicAccessSample.RunAsync(schema);
await MasterAndChildSample.RunAsync(schema);
await DependencyInjectionSample.RunAsync(schema);

// What follows is in the contract but not implemented yet. Each sample shows the intended
// use and runs to the end once its feature exists.
await RunPlannedAsync(() => IndexSample.RunAsync(schema), nameof(IndexSample));
await RunPlannedAsync(UniqueIndexSample.RunAsync, nameof(UniqueIndexSample));
await RunPlannedAsync(() => WriteBatchSample.RunAsync(schema), nameof(WriteBatchSample));
await RunPlannedAsync(IdentitySample.RunAsync, nameof(IdentitySample));

static async Task RunPlannedAsync(Func<Task> sample, string name)
{
    try
    {
        await sample();
    }
    catch (NotSupportedException exception)
    {
        Console.WriteLine($"Stopped: {exception.Message} See {name}.cs for the intended use.");
        Console.WriteLine();
    }
}
