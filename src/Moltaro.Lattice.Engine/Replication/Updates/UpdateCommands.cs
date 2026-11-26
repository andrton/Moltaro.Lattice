using System.Buffers;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Roots.Edits;

namespace Moltaro.Lattice.Replication.Updates;

// A bounded command body decoded in submitted or resolved form. The same
// field and operand checks apply before either form can enter the write path.
internal static class UpdateCommands
{
    private const int CommandHeadSize = 1 + sizeof(uint);
    private const byte MapInsert = 10;
    private const byte MapReplace = 11;

    public static long Length(StructureLayout layout, ReadOnlySpan<UpdateCommand> commands)
    {
        var length = (long)sizeof(uint);
        foreach (ref readonly var command in commands)
        {
            length += Length(layout, command);
        }

        return length;
    }

    public static long Length(StructureLayout layout, ReadOnlySpan<ResolvedCommand> commands)
    {
        var length = (long)sizeof(uint);
        foreach (ref readonly var resolved in commands)
        {
            length += Length(layout, resolved.Command);
        }

        return length;
    }

    public static void Write(IBufferWriter<byte> output, StructureLayout layout, ReadOnlySpan<UpdateCommand> commands)
    {
        UpdateOperands.WriteUInt32(output, (uint)commands.Length);
        foreach (ref readonly var command in commands)
        {
            Write(output, layout, command, (byte)command.Kind);
        }
    }

    public static void Write(IBufferWriter<byte> output, StructureLayout layout, ReadOnlySpan<ResolvedCommand> commands)
    {
        UpdateOperands.WriteUInt32(output, (uint)commands.Length);
        foreach (ref readonly var resolved in commands)
        {
            var command = resolved.Command;
            var opcode = (byte)command.Kind;
            if (command.Kind == UpdateCommandKindEnum.MapSet)
            {
                opcode = resolved.ReplacesMapValue ? MapReplace : MapInsert;
            }

            Write(output, layout, command, opcode);
        }
    }

    public static UpdateCommand[] ReadRequest(RootLayout root, ref SequenceReader<byte> input, MemoryLease? lease = null)
    {
        var count = Count(ref input);
        using var scalars = new BudgetSet<int>(lease?.Budget, nameof(UpdateCommands), EqualityComparer<int>.Default);
        var commands = lease?.Allocate<UpdateCommand>(count, nameof(UpdateCommands)) ?? new UpdateCommand[count];
        for (var index = 0; index < commands.Length; index++)
        {
            commands[index] = Read(root, ref input, scalars, resolved: false).Command;
        }

        ExpectEnd(input);
        return commands;
    }

    public static ResolvedCommand[] ReadEffect(RootLayout root, ref SequenceReader<byte> input, MemoryBudget? budget = null)
    {
        var count = Count(ref input);
        using var scalars = new BudgetSet<int>(budget, nameof(UpdateCommands), EqualityComparer<int>.Default);
        var commands = budget?.Allocate<ResolvedCommand>(count, nameof(UpdateCommands)) ?? new ResolvedCommand[count];
        try
        {
            for (var index = 0; index < commands.Length; index++)
            {
                commands[index] = Read(root, ref input, scalars, resolved: true);
            }

            ExpectEnd(input);
            return commands;
        }
        catch
        {
            budget?.Release(commands);
            throw;
        }
    }

    private static int Count(ref SequenceReader<byte> input)
    {
        var count = UpdateOperands.ReadUInt32(ref input);
        UpdateOperands.CheckCount(count, int.MaxValue, CommandHeadSize, input.Remaining);
        if (count == 0)
        {
            throw Protocol.Invalid("An update command body is empty.");
        }

        return (int)count;
    }

    private static void ExpectEnd(in SequenceReader<byte> input)
    {
        if (!input.End)
        {
            throw Protocol.Invalid("Bytes follow the last command of an update.");
        }
    }

    private static ResolvedCommand Read(RootLayout root, ref SequenceReader<byte> input, BudgetSet<int> scalars, bool resolved)
    {
        if (!input.TryRead(out var opcode))
        {
            throw Protocol.Invalid("An update body ends before a command opcode.");
        }

        var kind = (UpdateCommandKindEnum)opcode;
        if (resolved && opcode is MapInsert or MapReplace)
        {
            kind = UpdateCommandKindEnum.MapSet;
        }
        else if (kind is < UpdateCommandKindEnum.Assign or > UpdateCommandKindEnum.SetRemove ||
            (resolved && kind is UpdateCommandKindEnum.ListAppend or UpdateCommandKindEnum.MapSet))
        {
            throw Protocol.Invalid($"The opcode {opcode} cannot appear in this update body.");
        }

        var ordinal = UpdateOperands.ReadUInt32(ref input);
        var structure = root.Structure;
        var path = structure.RootPath(ordinal);
        if (path is null || ReferenceEquals(path[0], root.EntityType.Key))
        {
            throw Protocol.Invalid($"The target {ordinal} cannot be updated.");
        }

        var target = (int)ordinal;
        var type = structure.DeclarationOf(target).Type;
        var expected = kind switch
        {
            UpdateCommandKindEnum.Assign => type,
            UpdateCommandKindEnum.MapSet or UpdateCommandKindEnum.MapRemove => LatticeFieldTypeEnum.Map,
            UpdateCommandKindEnum.SetAdd or UpdateCommandKindEnum.SetRemove => LatticeFieldTypeEnum.Set,
            _ => LatticeFieldTypeEnum.List,
        };
        if (type == LatticeFieldTypeEnum.Owned || expected != type)
        {
            throw Protocol.Invalid($"The opcode {opcode} cannot address target {ordinal}.");
        }

        var position = 0;
        if (HasPosition(kind) && (!input.TryReadLittleEndian(out position) || (resolved && position < 0)))
        {
            throw Protocol.Invalid("An update command has no valid List position.");
        }

        object? first = null;
        object? second = null;
        if (kind == UpdateCommandKindEnum.Assign)
        {
            if (type is not (LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set or LatticeFieldTypeEnum.List) && !scalars.Add(target))
            {
                throw Protocol.Invalid($"An update assigns target {ordinal} more than once.");
            }

            first = UpdateOperands.Read(ref input, root.EntityType, structure, target);
        }
        else if (kind != UpdateCommandKindEnum.ListRemove)
        {
            var entry = structure.ElementOf(target);
            first = UpdateOperands.Read(ref input, root.EntityType, entry, 0);
            if (kind == UpdateCommandKindEnum.MapSet)
            {
                second = UpdateOperands.Read(ref input, root.EntityType, entry, 1);
            }
        }

        var name = path.Length == 1 ? path[0].Name : string.Join('.', path.Select(field => field.Name));
        return new ResolvedCommand(new UpdateCommand(kind, name, path, position, first, second, target), opcode == MapReplace);
    }

    private static long Length(StructureLayout structure, in UpdateCommand command)
    {
        var length = (long)CommandHeadSize + (HasPosition(command.Kind) ? sizeof(int) : 0);
        if (command.Kind == UpdateCommandKindEnum.Assign)
        {
            return length + UpdateOperands.Length(structure, command.Target, command.First);
        }

        if (command.Kind == UpdateCommandKindEnum.ListRemove)
        {
            return length;
        }

        var entry = structure.ElementOf(command.Target);
        length += UpdateOperands.Length(entry, 0, command.First);
        return command.Kind == UpdateCommandKindEnum.MapSet ? length + UpdateOperands.Length(entry, 1, command.Second) : length;
    }

    private static void Write(IBufferWriter<byte> output, StructureLayout structure, in UpdateCommand command, byte opcode)
    {
        output.GetSpan(1)[0] = opcode;
        output.Advance(1);
        UpdateOperands.WriteUInt32(output, (uint)command.Target);
        if (HasPosition(command.Kind))
        {
            UpdateOperands.WriteUInt32(output, unchecked((uint)command.Position));
        }

        if (command.Kind == UpdateCommandKindEnum.Assign)
        {
            UpdateOperands.Write(output, structure, command.Target, command.First);
        }
        else if (command.Kind != UpdateCommandKindEnum.ListRemove)
        {
            var entry = structure.ElementOf(command.Target);
            UpdateOperands.Write(output, entry, 0, command.First);
            if (command.Kind == UpdateCommandKindEnum.MapSet)
            {
                UpdateOperands.Write(output, entry, 1, command.Second);
            }
        }
    }

    private static bool HasPosition(UpdateCommandKindEnum kind) =>
        kind is UpdateCommandKindEnum.ListInsert or UpdateCommandKindEnum.ListReplace or UpdateCommandKindEnum.ListRemove;
}
