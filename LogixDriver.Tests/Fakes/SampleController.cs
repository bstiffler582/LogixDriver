using static Logix.Tags.TagMetaHelpers;

namespace LogixDriver.Tests.Fakes
{
    /// <summary>
    /// A small controller used across tests:
    /// <code>
    /// Counter  : DINT
    /// Label    : STRING                       (template 0xFCE)
    /// Bits     : BOOL[64]                     (reported as 2 words of 0xD3)
    /// Recipes  : Recipe[10]
    /// Grid     : Recipe[2,3]
    /// Program:Main
    ///     Local : DINT
    ///     Mine  : Recipe
    ///
    /// Recipe (0x123, 20 bytes)                Step (0x124, 8 bytes)
    ///     [hidden BOOL host SINT @0]              Target : DINT @0
    ///     Enable : BOOL @0.0                      Time   : DINT @4
    ///     Done   : BOOL @0.1
    ///     Temp   : REAL @4
    ///     Step   : Step @8
    ///     Flags  : BOOL[32] @16 (1 word)
    /// </code>
    /// </summary>
    internal static class SampleController
    {
        public const ushort RecipeId = 0x123;
        public const ushort StepId = 0x124;
        public const ushort StringId = 0xFCE;

        public const ushort Recipe = 0x8000 | RecipeId;
        public const ushort Step = 0x8000 | StepId;
        public const ushort String = 0x8000 | StringId;
        public const ushort OneDim = 0x2000, TwoDim = 0x4000;
        public const ushort BoolWords = 0xD3;

        public const int RecipeSize = 20;
        public const int StringSize = 88;

        public static FakePlc Create() => new FakePlc()
            .Tags(
                new TagEntry("Counter", (ushort)Code.DINT),
                new TagEntry("Label", String),
                new TagEntry("Bits", OneDim | BoolWords, new uint[] { 2 }),
                new TagEntry("Recipes", OneDim | Recipe, new uint[] { 10 }),
                new TagEntry("Grid", TwoDim | Recipe, new uint[] { 2, 3 }),
                new TagEntry("Program:Main", 0x1068),
                new TagEntry("Map:Local", 0x1069))
            .ProgramTags("Program:Main",
                new TagEntry("Local", (ushort)Code.DINT),
                new TagEntry("Mine", Recipe))
            .Template(RecipeId, "Recipe", RecipeSize,
                new MemberEntry("ZZZZZZZZZZRecipe0", (ushort)Code.SINT, 0),
                new MemberEntry("Enable", (ushort)Code.BOOL, 0, Info: 0),
                new MemberEntry("Done", (ushort)Code.BOOL, 0, Info: 1),
                new MemberEntry("Temp", (ushort)Code.REAL, 4),
                new MemberEntry("Step", Step, 8),
                new MemberEntry("Flags", OneDim | BoolWords, 16, Info: 1))
            .Template(StepId, "Step", 8,
                new MemberEntry("Target", (ushort)Code.DINT, 0),
                new MemberEntry("Time", (ushort)Code.DINT, 4))
            .Template(StringId, "STRING", StringSize,
                new MemberEntry("LEN", (ushort)Code.DINT, 0),
                new MemberEntry("DATA", OneDim | (ushort)Code.SINT, 4, Info: 82));

        public static string RecipeTemplate => $"@udt/{RecipeId}";
        public static string StepTemplate => $"@udt/{StepId}";
    }
}
