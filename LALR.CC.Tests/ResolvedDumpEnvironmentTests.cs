using System;
using System.IO;
using LALR.CC;
using Xunit;

namespace LALR.CC.Tests;

/// <summary>
/// Runs alone: the tests below set the process-wide <c>LALRCC_RESOLVED_DUMP</c> variable, which every
/// <see cref="ParserTableBuilder"/> (and so every <see cref="Parser"/>) reads when it builds a table. A
/// builder in a concurrent test would otherwise write into this collection's temp directory or race
/// its delete.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ResolvedDumpEnvironmentCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "LALRCC_RESOLVED_DUMP";
}

/// <summary>
/// The resolved-conflict dump's fallback: an empty prefix reads <c>LALRCC_RESOLVED_DUMP</c>, and a
/// prefix given to the builder wins over the variable. A prefix in a folder that does not exist yet
/// gets that folder made.
/// </summary>
[Collection(ResolvedDumpEnvironmentCollection.Name)]
public class ResolvedDumpEnvironmentTests
{
    private const string Variable = "LALRCC_RESOLVED_DUMP";

    // The source generator tests' SampleArithmetic, built directly:
    //   S' -> E, E -> i           (derivation none)
    //   E  -> E + E               (derivation leftmost)
    // The leftmost group settles the S/R conflict on '+', so the dump holds KEEP and DROP lines.
    private static Grammar SampleArithmetic() => new(
        ["S'", "E", "+", "i"],
        new PrecedenceGroup(Derivation.None,
            new Production(0, 1),
            new Production(1, 3)),
        new PrecedenceGroup(Derivation.LeftMost,
            new Production(1, 1, 2, 1)));

    /// <summary>Sets the variable to <c>&lt;dir&gt;/env</c> around <paramref name="body"/>, then restores it and deletes the directory.</summary>
    private static void WithEnvPrefix(Action<string, string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var old = Environment.GetEnvironmentVariable(Variable);
        try
        {
            var envPrefix = Path.Combine(dir, "env");
            Environment.SetEnvironmentVariable(Variable, envPrefix);
            body(dir, envPrefix);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, old);
            Directory.Delete(dir, true);
        }
    }

    private static void AssertDumpWritten(string prefix)
    {
        // SampleArithmetic declares 4 symbols.
        var file = prefix + ".4.txt";
        Assert.True(File.Exists(file), "missing " + file);
        var text = File.ReadAllText(file);
        Assert.Contains("KEEP", text);
        Assert.Contains("DROP", text);
    }

    [Fact]
    public void EmptyPrefix_FallsBackToVariable()
    {
        WithEnvPrefix((_, envPrefix) =>
        {
            new ParserTableBuilder(SampleArithmetic(), "");
            AssertDumpWritten(envPrefix);
        });
    }

    [Fact]
    public void NullPrefix_FallsBackToVariable()
    {
        // The generator hands the builder null when the property is unset.
        WithEnvPrefix((_, envPrefix) =>
        {
            new ParserTableBuilder(SampleArithmetic(), null);
            AssertDumpWritten(envPrefix);
        });
    }

    [Fact]
    public void OneArgumentConstructor_ReadsVariable()
    {
        WithEnvPrefix((_, envPrefix) =>
        {
            new ParserTableBuilder(SampleArithmetic());
            AssertDumpWritten(envPrefix);
        });
    }

    [Fact]
    public void GivenPrefix_WinsOverVariable()
    {
        WithEnvPrefix((dir, envPrefix) =>
        {
            var propPrefix = Path.Combine(dir, "prop");
            new ParserTableBuilder(SampleArithmetic(), propPrefix);
            AssertDumpWritten(propPrefix);
            Assert.False(File.Exists(envPrefix + ".4.txt"), "the variable's dump was written although a prefix was given");
        });
    }

    [Fact]
    public void PrefixInMissingFolder_CreatesTheFolder()
    {
        WithEnvPrefix((dir, _) =>
        {
            var folder = Path.Combine(dir, "missing", "deeper");
            Assert.False(Directory.Exists(folder));
            var prefix = Path.Combine(folder, "prop");
            new ParserTableBuilder(SampleArithmetic(), prefix);
            AssertDumpWritten(prefix);
        });
    }
}
