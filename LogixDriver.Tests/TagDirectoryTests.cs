using Logix.Tags;
using LogixDriver.Tests.Fakes;
using static LogixDriver.Tests.Fakes.SampleController;

namespace LogixDriver.Tests
{
    public class TagDirectoryTests
    {
        private readonly FakePlc plc = SampleController.Create();
        private readonly TagDirectory directory;

        public TagDirectoryTests() => directory = new TagDirectory(plc);

        [Fact]
        public async Task MemberPathReadsOnlyTemplatesAlongThePath()
        {
            var tag = await directory.ResolveAsync("Recipes[3].Temp");

            Assert.Equal("Recipes[3].Temp", tag.NativeName);
            Assert.Equal(1, tag.ElementCount);
            Assert.Equal("REAL", tag.Type.ElementType!.Name);
            Assert.Equal(1, plc.ReadsOf(RecipeTemplate));
            Assert.Equal(0, plc.ReadsOf(StepTemplate));
        }

        [Fact]
        public async Task StructurePathReadsItsWholeClosure()
        {
            var tag = await directory.ResolveAsync("Recipes[3]");

            Assert.Equal(1, plc.ReadsOf(RecipeTemplate));
            Assert.Equal(1, plc.ReadsOf(StepTemplate));
            Assert.True(((StructType)tag.Type.ElementType!).IsClosureResolved);
        }

        [Fact]
        public async Task TemplatesAreSharedAcrossTagsAndPaths()
        {
            await directory.ResolveAsync("Recipes[3]");
            await directory.ResolveAsync("Grid[1,2].Step.Target");
            await directory.ResolveAsync("Program:Main.Mine");

            Assert.Equal(2, plc.TemplateReads);
        }

        [Fact]
        public async Task ConcurrentResolvesShareOneTemplateRead()
        {
            plc.Delay = TimeSpan.FromMilliseconds(50);

            await Task.WhenAll(Enumerable.Range(0, 10).Select(i => directory.ResolveAsync($"Recipes[{i}].Step")));

            Assert.Equal(1, plc.ReadsOf("@tags"));
            Assert.Equal(1, plc.ReadsOf(RecipeTemplate));
            Assert.Equal(1, plc.ReadsOf(StepTemplate));
        }

        [Fact]
        public async Task ResolvedPathsAreCached()
        {
            var first = await directory.ResolveAsync("Recipes[3].Temp");
            var second = await directory.ResolveAsync("recipes[3].TEMP");

            Assert.Same(first, second);
        }

        [Fact]
        public async Task FilteredLoadResolvesOnlyTheFilteredTags()
        {
            await directory.LoadAsync(new[] { "Counter", "Label" });

            var loaded = directory.GetLoadedTags();
            Assert.Equal(new[] { "Counter", "Label", "Bits", "Recipes", "Grid", "Program:Main" }, loaded.Select(t => t.Name));
            Assert.False(loaded.OfType<ProgramInfo>().Single().IsLoaded);
            var tags = loaded.OfType<TagInfo>().ToDictionary(t => t.Name);
            Assert.False(tags["Recipes"].Type.IsResolved);
            Assert.Equal(0, plc.ReadsOf(RecipeTemplate));
            Assert.IsType<StringType>(tags["Label"].Type.ElementType);
        }

        [Fact]
        public async Task ProgramLoadResolvesItsTags()
        {
            await directory.LoadAsync(new[] { "program:main" });

            var tags = directory.GetLoadedTags().OfType<ProgramInfo>().Single().Tags!;
            Assert.Equal(new[] { "Local", "Mine" }, tags.Select(t => t.Name));
            Assert.Equal(new[] { "Program:Main.Local", "Program:Main.Mine" }, tags.Select(t => t.Path));
            Assert.True(((StructType)tags[1].Type.ElementType!).IsClosureResolved);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UnfilteredLoadResolvesEverything(bool emptyFilter)
        {
            await directory.LoadAsync(emptyFilter ? Array.Empty<string>() : null);

            var loaded = directory.GetLoadedTags();
            var program = loaded.OfType<ProgramInfo>().Single();
            var allTags = loaded.OfType<TagInfo>().Concat(program.Tags!).ToList();
            Assert.All(allTags, t => Assert.True(t.Type.IsResolved));
            Assert.Equal(7, allTags.Count);
            Assert.Equal(3, plc.TemplateReads);
        }

        [Fact]
        public async Task LoadedTagsIncludeProgramsReachedByResolvingAPath()
        {
            Assert.Empty(directory.GetLoadedTags());

            await directory.ResolveAsync("Program:Main.Local");

            var program = directory.GetLoadedTags().OfType<ProgramInfo>().Single();
            Assert.True(program.IsLoaded);
            Assert.Equal("Program:Main", program.Path);
            Assert.Contains(program.Tags!, t => t.Path == "Program:Main.Mine" && !t.Type.IsResolved);
        }

        [Theory]
        [InlineData("Recipes", "Recipes[0]", 10)]
        [InlineData("Grid", "Grid[0,0]", 6)]
        [InlineData("Grid[1]", "Grid[1,0]", 3)]
        [InlineData("Grid[1][2].Temp", "Grid[1,2].Temp", 1)]
        [InlineData("Grid[1,2].Temp", "Grid[1,2].Temp", 1)]
        [InlineData("Bits", "Bits[0]", 2)]
        [InlineData("Recipes[2].Flags", "Recipes[2].Flags[0]", 1)]
        [InlineData("program:main.local", "Program:Main.Local", 1)]
        [InlineData("recipes[3].step.target", "Recipes[3].Step.Target", 1)]
        public async Task NativeNamesUseLogixSyntaxAndControllerCasing(string path, string nativeName, int elementCount)
        {
            var tag = await directory.ResolveAsync(path);

            Assert.Equal(nativeName, tag.NativeName);
            Assert.Equal(elementCount, tag.ElementCount);
        }

        [Fact]
        public async Task BoolArrayElementReadsItsWord()
        {
            var tag = await directory.ResolveAsync("Bits[37]");

            Assert.Equal("Bits[1]", tag.NativeName);
            Assert.Equal(1, tag.ElementCount);
            Assert.Equal(5, tag.BitOffset);
            Assert.True(tag.IsBitArrayElement);
            Assert.Same(PrimitiveType.Bool, tag.Type.ElementType);
        }

        [Theory]
        [InlineData("Missing")]
        [InlineData("Recipes[3].Missing")]
        [InlineData("Recipes[10]")]
        [InlineData("Counter.Member")]
        [InlineData("Program:Nope.Local")]
        public async Task UnknownPathsThrowKeyNotFound(string path)
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => directory.ResolveAsync(path));
        }

        [Theory]
        [InlineData("Recipes.Temp")]
        [InlineData("Counter[0]")]
        [InlineData("Grid[1,2,3]")]
        [InlineData("Grid[1].Temp")]
        [InlineData("Bits[3].X")]
        [InlineData("Program:Main")]
        [InlineData("Recipes[x]")]
        [InlineData("Recipes[3")]
        [InlineData("Recipes..Temp")]
        public async Task MalformedPathsThrowArgument(string path)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => directory.ResolveAsync(path));
        }

        [Fact]
        public async Task RefreshRereadsTagListsAndTemplates()
        {
            await directory.LoadAsync();
            var before = await directory.ResolveAsync("Recipes[0]");

            // the program changes: a new tag, and Step gains a member
            plc.Tags(new TagEntry("Counter", 0xC4), new TagEntry("Added", 0xC4), new TagEntry("Recipes", OneDim | Recipe, new uint[] { 10 }));
            plc.Template(StepId, "Step", 12,
                new MemberEntry("Target", 0xC4, 0),
                new MemberEntry("Time", 0xC4, 4),
                new MemberEntry("Speed", 0xCA, 8));

            await directory.LoadAsync();
            Assert.DoesNotContain(directory.GetLoadedTags(), t => t.Name == "Added");

            directory.Refresh();
            await directory.LoadAsync();

            Assert.Contains(directory.GetLoadedTags(), t => t.Name == "Added");
            var after = await directory.ResolveAsync("Recipes[0].Step.Speed");
            Assert.NotSame(before, await directory.ResolveAsync("Recipes[0]"));
            Assert.Equal("REAL", after.Type.ElementType!.Name);
            Assert.Equal(2, plc.ReadsOf(StepTemplate));
        }

        [Fact]
        public async Task FailedTemplateReadsAreRetried()
        {
            var stepTemplate = plc.TryGetResponse(StepTemplate, out var data) ? data : throw new InvalidOperationException();
            plc.Respond(StepTemplate, Array.Empty<byte>());

            await Assert.ThrowsAnyAsync<Exception>(() => directory.ResolveAsync("Recipes[3]"));

            plc.Respond(StepTemplate, stepTemplate);
            var tag = await directory.ResolveAsync("Recipes[3]");

            Assert.True(((StructType)tag.Type.ElementType!).IsClosureResolved);
            Assert.Equal(2, plc.ReadsOf(StepTemplate));
            Assert.Equal(1, plc.ReadsOf(RecipeTemplate));
        }
    }
}
