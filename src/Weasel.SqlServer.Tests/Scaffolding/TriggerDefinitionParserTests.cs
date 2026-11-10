using Shouldly;
using Weasel.Core;
using Weasel.SqlServer.Scaffolding;
using Xunit;

namespace Weasel.SqlServer.Tests.Scaffolding;

public class TriggerDefinitionParserTests
{
    [Fact]
    public void reads_for_as_after_and_keeps_the_body_verbatim()
    {
        var parsed = TriggerDefinitionParser.Parse(
            "CREATE TRIGGER [dbo].[trg] ON [dbo].[T]\r\nFOR INSERT, UPDATE\r\nAS\r\nBEGIN\r\n  SELECT 1\r\nEND\r\n", out var problem);

        problem.ShouldBeNull();
        parsed!.Timing.ShouldBe(TriggerTiming.After);
        parsed.Events.ShouldBe(TriggerEvents.Insert | TriggerEvents.Update);
        parsed.Body.ShouldBe("\r\nBEGIN\r\n  SELECT 1\r\nEND\r\n");
    }

    [Fact]
    public void skips_comments_and_reads_instead_of()
    {
        var parsed = TriggerDefinitionParser.Parse(
            "-- header\n/* block /* nested */ */\ncreate or alter trigger x.[a]]b] on \"x\".t instead of delete as select 2",
            out _);

        parsed!.Timing.ShouldBe(TriggerTiming.InsteadOf);
        parsed.Events.ShouldBe(TriggerEvents.Delete);
        parsed.Body.ShouldBe(" select 2");
    }

    [Fact]
    public void an_as_inside_an_identifier_does_not_end_the_header()
    {
        var parsed = TriggerDefinitionParser.Parse(
            "CREATE TRIGGER dbo.[as] ON dbo.[AS table] AFTER DELETE AS RETURN", out _);

        parsed!.Body.ShouldBe(" RETURN");
    }

    [Theory]
    [InlineData("CREATE TRIGGER t ON dbo.x WITH EXECUTE AS OWNER AFTER INSERT AS SELECT 1")]
    [InlineData("CREATE TRIGGER t ON dbo.x AFTER INSERT NOT FOR REPLICATION AS SELECT 1")]
    [InlineData("CREATE TRIGGER t ON DATABASE FOR CREATE_TABLE AS SELECT 1")]
    public void refuses_what_the_trigger_model_cannot_express(string definition)
    {
        TriggerDefinitionParser.Parse(definition, out var problem).ShouldBeNull();
        problem.ShouldNotBeNull();
    }
}
