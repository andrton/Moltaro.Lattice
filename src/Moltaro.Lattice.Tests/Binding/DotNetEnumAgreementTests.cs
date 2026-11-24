namespace Moltaro.Lattice.Tests.Binding;

// A .NET enum of a bound property agrees with the enum type of its field: the same underlying
// type, the same members, every name with its value in both directions, and [Flags] exactly
// when the enum type is a Flags type. A class whose enum does not agree is refused as a class
// that does not agree with its entity type is: BindingFailed, with the property named, when
// the class is bound. Nothing is mapped or guessed. A property declared as the underlying
// integer is not compared with the members when the class is bound; a value it holds that the
// enum type does not declare is refused by a write, as EnumValidationTests states.
public sealed class DotNetEnumAgreementTests
{
    // Color declares 1 twice, as Red, its canonical name, and as Crimson.
    private enum ColorEnum
    {
        Red = 1,
        Crimson = Red,
        Green = 2,
        Blue = 4,
    }

    [Flags]
    private enum AccessEnum : byte
    {
        None = 0,
        Read = 1,
        Write = 2,
        ReadWrite = Read | Write,
    }

    // Each of the following disagrees with its enum type in one point.
    private enum ColorWithBlackEnum
    {
        Red = 1,
        Crimson = Red,
        Green = 2,
        Blue = 4,
        Black = 8,
    }

    private enum ColorWithoutBlueEnum
    {
        Red = 1,
        Crimson = Red,
        Green = 2,
    }

    private enum ColorWithBlueAtEightEnum
    {
        Red = 1,
        Crimson = Red,
        Green = 2,
        Blue = 8,
    }

    private enum ColorWithLimeEnum
    {
        Red = 1,
        Crimson = Red,
        Green = 2,
        Lime = Green,
        Blue = 4,
    }

    private enum ColorWithoutCrimsonEnum
    {
        Red = 1,
        Green = 2,
        Blue = 4,
    }

    [Flags]
    private enum FlagsColorEnum
    {
        Red = 1,
        Crimson = Red,
        Green = 2,
        Blue = 4,
    }

    private enum AccessWithoutFlagsEnum : byte
    {
        None = 0,
        Read = 1,
        Write = 2,
        ReadWrite = Read | Write,
    }

    private enum LongColorEnum : long
    {
        Red = 1,
        Crimson = Red,
        Green = 2,
        Blue = 4,
    }

    [Fact]
    public async Task Dotnet_enums_that_agree_with_their_enum_types_bind()
    {
        // An ordinary enum with the alias pair the schema declares, and a Flags enum.
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<Car>());

        Assert.Equal("Car", database.Set<Car>().EntityType.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_update_binds_the_declared_enum_and_its_nullable_form(bool byClass)
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<Car>());
        var cars = database.Set<Car>();
        await cars.InsertAsync(new Car { Id = 1, Tint = ColorEnum.Green, Rights = AccessEnum.Read });

        var update = byClass ? cars.CreateUpdate(1) : database.Set("Car").CreateUpdate(1);
        Assert.Throws<ArgumentException>(() => update.Assign<ColorEnum?>("Rights", null));
        var rights = byClass ? update.Assign<AccessEnum?>("Rights", null) : update.Assign<byte?>("Rights", null);
        var tint = byClass ? update.Assign("Tint", ColorEnum.Blue) : update.Assign("Tint", (short)4);
        Assert.True((await update.CommitAsync()).Changed);
        Assert.True(rights.Changed);
        Assert.True(tint.Changed);

        var saved = cars.Get(1)!;
        Assert.Null(saved.Rights);
        Assert.Equal(ColorEnum.Blue, saved.Tint);
    }

    [Fact]
    public async Task Property_of_the_underlying_integer_is_not_compared_with_the_members()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<CarWithIntegers>());

        Assert.Equal("Car", database.Set<CarWithIntegers>().EntityType.Name);
    }

    [Fact]
    public async Task Dotnet_enum_with_a_name_the_schema_does_not_declare_does_not_bind()
    {
        // Black = 8.
        await ExpectNotBoundAsync<CarWithBlack>("Tint");
    }

    [Fact]
    public async Task Dotnet_enum_without_a_name_the_schema_declares_does_not_bind()
    {
        // No Blue.
        await ExpectNotBoundAsync<CarWithoutBlue>("Tint");
    }

    [Fact]
    public async Task Dotnet_enum_that_gives_a_name_another_value_does_not_bind()
    {
        // Blue = 8, where the schema declares Blue = 4.
        await ExpectNotBoundAsync<CarWithBlueAtEight>("Tint");
    }

    [Fact]
    public async Task Dotnet_enum_with_an_alias_the_schema_declares_under_one_name_only_does_not_bind()
    {
        // Green and Lime name 2; the schema names 2 Green only.
        await ExpectNotBoundAsync<CarWithLime>("Tint");
    }

    [Fact]
    public async Task Dotnet_enum_with_one_name_for_a_value_the_schema_declares_twice_does_not_bind()
    {
        // Red alone names 1; the schema names 1 Red and Crimson.
        await ExpectNotBoundAsync<CarWithoutCrimson>("Tint");
    }

    [Fact]
    public async Task Dotnet_flags_enum_for_an_enum_type_that_is_not_flags_does_not_bind()
    {
        await ExpectNotBoundAsync<CarWithFlagsColor>("Tint");
    }

    [Fact]
    public async Task Dotnet_enum_without_flags_for_a_flags_enum_type_does_not_bind()
    {
        await ExpectNotBoundAsync<CarWithAccessNotFlags>("Rights");
    }

    [Fact]
    public async Task Dotnet_enum_of_another_underlying_type_does_not_bind_and_the_property_is_named()
    {
        // An Int64 enum for an enum type stored as Int32.
        await ExpectNotBoundAsync<CarWithLongColor>("Tint");
    }

    [Fact]
    public async Task Nullable_dotnet_enum_for_a_field_that_is_not_nullable_does_not_bind()
    {
        // Tint is not nullable; the property could hold a null the field cannot.
        await ExpectNotBoundAsync<CarWithOptionalColor>("Tint");
    }

    [Fact]
    public async Task Dotnet_enum_that_is_not_nullable_for_a_nullable_field_does_not_bind()
    {
        // Rights is nullable; the property could not hold a null the field has.
        await ExpectNotBoundAsync<CarWithRequiredAccess>("Rights");
    }

    // The class does not bind, and the error names the property whose enum does not agree.
    private static async Task ExpectNotBoundAsync<TCar>(string propertyName)
        where TCar : class
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<TCar>());

        var exception = Assert.Throws<LatticeException>(() => database.Set<TCar>());

        Assert.Equal(LatticeErrorEnum.BindingFailed, exception.Error);
        Assert.Contains(propertyName, exception.Message);
    }

    private static LatticeSchema SchemaFor<TCar>() => LatticeSchema.Compile($$"""
        {
          "Enums": [
            {
              "Id": 1, "Name": "Color",
              "Members": [
                { "Name": "Red", "Value": 1, "Canonical": true },
                { "Name": "Crimson", "Value": 1 },
                { "Name": "Green", "Value": 2 },
                { "Name": "Blue", "Value": 4 }
              ]
            },
            {
              "Id": 2, "Name": "Access", "UnderlyingType": "UInt8", "Flags": true,
              "Members": [
                { "Name": "None", "Value": 0 },
                { "Name": "Read", "Value": 1 },
                { "Name": "Write", "Value": 2 },
                { "Name": "ReadWrite", "Value": 3 }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Car", "ClrType": "{{typeof(TCar).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Tint", "Type": "Enum", "Enum": "Color" },
                { "Id": 3, "Name": "Rights", "Type": "Enum", "Enum": "Access", "Nullable": true }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Car
    {
        public long Id { get; set; }

        public ColorEnum Tint { get; set; }

        public AccessEnum? Rights { get; set; }
    }

    private sealed class CarWithIntegers
    {
        public long Id { get; set; }

        public int Tint { get; set; }

        public byte? Rights { get; set; }
    }

    private sealed class CarWithBlack
    {
        public long Id { get; set; }

        public ColorWithBlackEnum Tint { get; set; }

        public AccessEnum? Rights { get; set; }
    }

    private sealed class CarWithoutBlue
    {
        public long Id { get; set; }

        public ColorWithoutBlueEnum Tint { get; set; }

        public AccessEnum? Rights { get; set; }
    }

    private sealed class CarWithBlueAtEight
    {
        public long Id { get; set; }

        public ColorWithBlueAtEightEnum Tint { get; set; }

        public AccessEnum? Rights { get; set; }
    }

    private sealed class CarWithLime
    {
        public long Id { get; set; }

        public ColorWithLimeEnum Tint { get; set; }

        public AccessEnum? Rights { get; set; }
    }

    private sealed class CarWithoutCrimson
    {
        public long Id { get; set; }

        public ColorWithoutCrimsonEnum Tint { get; set; }

        public AccessEnum? Rights { get; set; }
    }

    private sealed class CarWithFlagsColor
    {
        public long Id { get; set; }

        public FlagsColorEnum Tint { get; set; }

        public AccessEnum? Rights { get; set; }
    }

    private sealed class CarWithAccessNotFlags
    {
        public long Id { get; set; }

        public ColorEnum Tint { get; set; }

        public AccessWithoutFlagsEnum? Rights { get; set; }
    }

    private sealed class CarWithLongColor
    {
        public long Id { get; set; }

        public LongColorEnum Tint { get; set; }

        public AccessEnum? Rights { get; set; }
    }

    private sealed class CarWithOptionalColor
    {
        public long Id { get; set; }

        public ColorEnum? Tint { get; set; }

        public AccessEnum? Rights { get; set; }
    }

    private sealed class CarWithRequiredAccess
    {
        public long Id { get; set; }

        public ColorEnum Tint { get; set; }

        public AccessEnum Rights { get; set; }
    }
}
