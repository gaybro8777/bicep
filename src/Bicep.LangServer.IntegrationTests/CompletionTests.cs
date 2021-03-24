// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Bicep.Core.Extensions;
using Bicep.Core.FileSystem;
using Bicep.Core.Parsing;
using Bicep.Core.Samples;
using Bicep.Core.Syntax;
using Bicep.Core.Text;
using Bicep.Core.TypeSystem.Az;
using Bicep.Core.UnitTests.Assertions;
using Bicep.Core.UnitTests.Utils;
using Bicep.LangServer.IntegrationTests.Completions;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Bicep.LangServer.IntegrationTests
{
    [TestClass]
    [SuppressMessage("Style", "VSTHRD200:Use \"Async\" suffix for async methods", Justification = "Test methods do not need to follow this convention.")]
    public class CompletionTests
    {
        public static readonly AzResourceTypeProvider TypeProvider = new AzResourceTypeProvider();

        [NotNull]
        public TestContext? TestContext { get; set; }

        [TestMethod]
        public async Task EmptyFileShouldProduceDeclarationCompletions()
        {
            const string expectedSetName = "declarations";
            var uri = DocumentUri.From($"/{this.TestContext.TestName}");

            using var client = await IntegrationTestHelper.StartServerWithTextAsync(string.Empty, uri);

            var actual = await GetActualCompletions(client, uri, new Position(0, 0));
            var actualLocation = FileHelper.SaveResultFile(this.TestContext, $"{this.TestContext.TestName}_{expectedSetName}", actual.ToString(Formatting.Indented));
            
            var expectedStr = DataSets.Completions.TryGetValue(GetFullSetName(expectedSetName));
            if (expectedStr == null)
            {
                throw new AssertFailedException($"The completion set '{expectedSetName}' does not exist.");
            }
            
            var expected = JToken.Parse(expectedStr);

            actual.Should().EqualWithJsonDiffOutput(TestContext, expected, GetGlobalCompletionSetPath(expectedSetName), actualLocation);
        }

        [DataTestMethod]
        [DynamicData(nameof(GetData), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(GetDisplayName))]
        public async Task CompletionRequestShouldProduceExpectedCompletions(DataSet dataSet, string setName, IList<Position> positions)
        {
            // ensure all files are present locally
            string basePath = dataSet.SaveFilesToTestDirectory(this.TestContext);

            var entryPoint = Path.Combine(basePath, "main.bicep");
            
            var uri = DocumentUri.FromFileSystemPath(entryPoint);
            
            using var client = await IntegrationTestHelper.StartServerWithTextAsync(dataSet.Bicep, uri, resourceTypeProvider: TypeProvider, fileResolver: new FileResolver());

            var intermediate = new List<(Position position, JToken actual)>();

            foreach (var position in positions)
            {
                var actual = await GetActualCompletions(client, uri, position);

                intermediate.Add((position, actual));
            }

            ValidateCompletions(dataSet, setName, intermediate);
        }

        [TestMethod]
        public async Task String_segments_do_not_return_completions()
        {
            var fileWithCursors = @"
var completeString = |'he|llo'|
var interpolatedString = |'abc${|true}|de|f${|false}|gh|i'|
var multilineString = |'''|
hel|lo
'''|
";
            var bicepFile = fileWithCursors.Replace("|", "");
            var syntaxTree = SyntaxTree.Create(new Uri("file:///main.bicep"), bicepFile);

            var cursors = new List<int>();
            for (var i = 0; i < fileWithCursors.Length; i++)
            {
                if (fileWithCursors[i] == '|')
                {
                    cursors.Add(i - cursors.Count);
                }
            }

            using var client = await IntegrationTestHelper.StartServerWithTextAsync(bicepFile, syntaxTree.FileUri, resourceTypeProvider: TypeProvider);

            foreach (var cursor in cursors)
            {
                using var assertionScope = new AssertionScope();
                assertionScope.AddReportable(
                    "completion context",
                    PrintHelper.PrintWithAnnotations(syntaxTree, new [] { 
                        new PrintHelper.Annotation(new TextSpan(cursor, 0), "cursor position"),
                    }, 1, true));

                var completions = await client.RequestCompletion(new CompletionParams
                {
                    TextDocument = new TextDocumentIdentifier(syntaxTree.FileUri),
                    Position = TextCoordinateConverter.GetPosition(syntaxTree.LineStarts, cursor),
                });

                completions.Should().BeEmpty();
            }
        }

        private void ValidateCompletions(DataSet dataSet, string setName, List<(Position position, JToken actual)> intermediate)
        {
            // if the test author asserts on the wrong completion set in their tests
            // it will not be possible to update the expected completions in a way that makes the test pass
            // to save investigation time, we will produce an error message when this condition is detected
            var groupsByActual = intermediate.GroupBy(g => g.actual.ToString(Formatting.None)).ToList();

            if (groupsByActual.Count != 1)
            {
                var buffer = new StringBuilder();
                buffer.AppendLine($"The following groups completion trigger positions asserted the same expected completion set '{setName}' but produced different actual completions:");

                foreach (var group in groupsByActual)
                {
                    buffer.Append("- ");
                    buffer.Append(group.Select(tuple => FormatPosition(tuple.position)).ConcatString(", "));
                    buffer.AppendLine();
                }

                // we encountered multiple different completions sets
                throw new AssertFailedException(buffer.ToString());
            }

            var single = groupsByActual.Single();

            var expectedInfo = ResolveExpectedCompletions(dataSet, setName);
            if (expectedInfo.HasValue == false)
            {
                throw new AssertFailedException($"The completion set with the name '{setName}' does not exist. The following completion trigger positions assert this set: {single.Select(item => FormatPosition(item.position)).ConcatString(", ")}");
            }

            var actual = JToken.Parse(single.Key);
            var actualLocation = FileHelper.SaveResultFile(this.TestContext, $"{dataSet.Name}_{setName}_Actual.json", actual.ToString(Formatting.Indented));

            var expected = expectedInfo.Value.content;
            var expectedLocation = expectedInfo.Value.scope switch
            {
                ExpectedCompletionsScope.DataSet => Path.Combine("src", "Bicep.Core.Samples", "Files", dataSet.Name, DataSet.TestCompletionsDirectory, GetFullSetName(setName)),
                _ => GetGlobalCompletionSetPath(setName)
            };

            actual.Should().EqualWithJsonDiffOutput(TestContext, expected, expectedLocation, actualLocation, "because ");
        }

        private static string GetGlobalCompletionSetPath(string setName) => Path.Combine("src", "Bicep.Core.Samples", "Files", DataSet.TestCompletionsDirectory, GetFullSetName(setName));

        public static string GetDisplayName(MethodInfo info, object[] row)
        {
            row.Should().HaveCount(3);
            row[0].Should().BeOfType<DataSet>();
            row[1].Should().BeOfType<string>();
            row[2].Should().BeAssignableTo<IList<Position>>();

            return $"{info.Name}_{((DataSet) row[0]).Name}_{row[1]}";
        }

        private static string FormatPosition(Position position) => $"({position.Line}, {position.Character})";

        private static async Task<JToken> GetActualCompletions(ILanguageClient client, DocumentUri uri, Position position)
        {
            // local function
            string? NormalizeLineEndings(string? value) => value is null
                ? null
                : value.Replace("\r", string.Empty);

            StringOrMarkupContent? NormalizeDocumentation(StringOrMarkupContent? value) =>
                value?.MarkupContent?.Value is null
                    ? null
                    : new(value.MarkupContent with
                    {
                        Value = NormalizeLineEndings(value.MarkupContent.Value)!
                    });

            TextEditOrInsertReplaceEdit? NormalizeTextEdit(TextEditOrInsertReplaceEdit? value) =>
                value is null
                    ? null
                    : new TextEditOrInsertReplaceEdit(value.TextEdit! with
                    {
                        NewText = NormalizeLineEndings(value.TextEdit.NewText)!,

                        // ranges in these text edits will vary by completion trigger position
                        // if we try to assert on these, we will have an explosion of assert files
                        // let's ignore it for now until we come up with a better solution
                        Range = new Range()
                    });

            TextEditContainer? NormalizeAdditionalTextEdits(TextEditContainer? value) =>
                value is null
                    ? null
                    : new TextEditContainer(value.Select(edit => edit with
                    {
                        Range = new Range()
                    }));

            // OmniSharp sometimes will add a $$__handler_id__$$ property to the Data dictionary of a completion
            // (likely is needed to somehow help with routing of the ResolveCompletion requests)
            // the value is different every time you run the language server
            // to make our test asserts work, we will remove it and set the Data property null if nothing else remains in the object
            JToken? NormalizeData(JToken? value)
            {
                // LSP protocol dictates that the Data property is of "any" type, so we need to check
                if (value is JObject @object)
                {
                    const string omnisharpHandlerIdPropertyName = "$$__handler_id__$$";
                    if (@object.Property(omnisharpHandlerIdPropertyName) != null)
                    {
                        @object.Remove(omnisharpHandlerIdPropertyName);

                        if (!@object.HasValues)
                        {
                            return null;
                        }

                        return @object;
                    }
                }

                return value;
            }

            var completions = await client.RequestCompletion(new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = position
            });

            // normalize the completions to remove OS-specific mismatches (line endings) as well as any randomness
            var normalized = completions.Select(completion => completion with
            {
                InsertText = NormalizeLineEndings(completion.InsertText),
                Detail = NormalizeLineEndings(completion.Detail),
                Documentation = NormalizeDocumentation(completion.Documentation),
                TextEdit = NormalizeTextEdit(completion.TextEdit),
                AdditionalTextEdits = NormalizeAdditionalTextEdits(completion.AdditionalTextEdits),
                Data = NormalizeData(completion.Data)
            });

            return JToken.FromObject(normalized.OrderBy(c => c.Label, StringComparer.Ordinal), DataSetSerialization.CreateSerializer());
        }

        private static (JToken content, ExpectedCompletionsScope scope)? ResolveExpectedCompletions(DataSet dataSet, string setName)
        {
            var fullSetName = GetFullSetName(setName);
            var str = dataSet.Completions.TryGetValue(fullSetName);
            if (str != null)
            {
                return (JToken.Parse(str), ExpectedCompletionsScope.DataSet);
            }

            str = DataSets.Completions.TryGetValue(fullSetName);
            if (str != null)
            {
                return (JToken.Parse(str), ExpectedCompletionsScope.Global);
            }

            return null;
        }

        private static string GetFullSetName(string setName) => setName + ".json";

        private static IEnumerable<object[]> GetData() =>
            DataSets.NonStressDataSets
                .Select(ds => (dataset: ds, triggers: CompletionTestDirectiveParser.GetTriggers(ds.Bicep)))
                .Where(tuple => tuple.triggers.Any())
                .SelectMany(tuple => tuple.triggers.Select(trigger => (tuple.dataset, trigger.Position, trigger.SetName)))
                .GroupBy(tuple => (tuple.dataset, tuple.SetName), tuple => tuple.Position)
                .Select(group => new object[] {group.Key.dataset, group.Key.SetName, group.ToList()});

        private enum ExpectedCompletionsScope
        {
            Global,
            DataSet
        }
    }
}
