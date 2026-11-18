using Shouldly;
using Xunit;

namespace Weasel.SqlServer.Tests;

public class SqlServerBatchSplitterTests
{
    [Fact]
    public void two_statements_separated_by_go_give_two_batches()
    {
        var sql = "select 1\nGO\nselect 2\n";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1", "select 2"]);
    }

    [Fact]
    public void lowercase_go_is_a_separator()
    {
        var sql = "select 1\ngo\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1", "select 2"]);
    }

    [Fact]
    public void go_with_a_trailing_semicolon_is_a_separator()
    {
        var sql = "select 1\nGO;\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1", "select 2"]);
    }

    [Fact]
    public void indented_go_with_trailing_spaces_is_a_separator()
    {
        var sql = "select 1\n  GO  \nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1", "select 2"]);
    }

    [Fact]
    public void go_with_a_count_is_a_separator_and_the_batch_appears_once()
    {
        var sql = "select 1\nGO 3\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1", "select 2"]);
    }

    /// <summary>
    ///     The repeat count is part of the separator line or it is not a count at all. Whitespace in
    ///     the pattern stays horizontal so a lone digit on the next line is ordinary batch text, not
    ///     something the separator swallows.
    /// </summary>
    [Fact]
    public void a_count_on_the_line_after_go_is_not_part_of_the_separator()
    {
        var sql = "select 1\nGO\n5\nselect 2;";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1", "5\nselect 2;"]);
    }

    [Fact]
    public void a_trailing_comment_on_the_go_line_is_not_a_separator()
    {
        var sql = "select 1\nGO -- done\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1\nGO -- done\nselect 2"]);
    }

    [Fact]
    public void a_leading_and_trailing_go_produce_no_empty_batches()
    {
        var sql = "GO\nselect 1\nGO\n";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1"]);
    }

    [Fact]
    public void consecutive_go_lines_produce_no_empty_batches()
    {
        var sql = "select 1\nGO\nGO\n\nGO\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1", "select 2"]);
    }

    [Fact]
    public void crlf_input_splits_the_same_as_lf_input()
    {
        var crlf = "select 1\r\nGO\r\nselect 2\r\nselect 3\r\n";

        SqlServerBatchSplitter.Split(crlf).ShouldBe(["select 1", "select 2\r\nselect 3"]);
    }

    [Fact]
    public void batches_keep_their_internal_line_endings()
    {
        var sql = "select 1\nselect 2\nGO\nselect 3";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1\nselect 2", "select 3"]);
    }

    [Fact]
    public void blank_lines_around_a_batch_are_trimmed()
    {
        var sql = "\n\nselect 1\n\nGO\n\n  select 2  \n\n";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1", "select 2"]);
    }

    [Fact]
    public void a_go_line_inside_a_string_literal_is_not_a_separator()
    {
        var sql = "select '\nGO\n' as x\nGO\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select '\nGO\n' as x", "select 2"]);
    }

    [Fact]
    public void a_doubled_quote_does_not_end_the_string_literal()
    {
        var sql = "select 'it''s\nGO\nstill' as x\nGO\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 'it''s\nGO\nstill' as x", "select 2"]);
    }

    [Fact]
    public void a_go_line_inside_a_block_comment_is_not_a_separator()
    {
        var sql = "/*\nDROP FUNCTION dbo.f\nGO\n*/\nCREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END\nGO\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(
            ["/*\nDROP FUNCTION dbo.f\nGO\n*/\nCREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END", "select 2"]);
    }

    [Fact]
    public void block_comments_do_not_nest()
    {
        var sql = "select 1 /* a /* b */\nGO\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1 /* a /* b */", "select 2"]);
    }

    [Fact]
    public void a_block_comment_opener_inside_a_line_comment_is_ignored()
    {
        var sql = "select 1 -- not a /* comment\nGO\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1 -- not a /* comment", "select 2"]);
    }

    [Fact]
    public void a_quote_inside_a_line_comment_is_ignored()
    {
        var sql = "select 1 -- it's\nGO\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1 -- it's", "select 2"]);
    }

    [Fact]
    public void a_go_line_inside_a_bracketed_identifier_is_not_a_separator()
    {
        var sql = "select 1 as [a\nGO\nb]\nGO\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1 as [a\nGO\nb]", "select 2"]);
    }

    [Fact]
    public void a_go_line_inside_a_quoted_identifier_is_not_a_separator()
    {
        var sql = "select 1 as \"a\nGO\nb\"\nGO\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 1 as \"a\nGO\nb\"", "select 2"]);
    }

    [Fact]
    public void crlf_input_inside_a_block_comment_does_not_split()
    {
        var sql = "/*\r\nGO\r\n*/\r\nselect 1\r\nGO\r\nselect 2";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["/*\r\nGO\r\n*/\r\nselect 1", "select 2"]);
    }

    [Fact]
    public void a_go_line_inside_a_block_comment_is_not_reported_as_a_separator()
    {
        SqlServerBatchSplitter.ContainsSeparator("/*\nGO\n*/\nselect 1").ShouldBeFalse();
        SqlServerBatchSplitter.ContainsSeparator("select 1\nGO\nselect 2").ShouldBeTrue();
    }

    [Fact]
    public void goto_is_not_a_separator()
    {
        var sql = "GOTO label\nselect 1";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["GOTO label\nselect 1"]);
    }

    [Fact]
    public void go_inside_a_single_line_string_literal_is_not_a_separator()
    {
        var sql = "select 'GO' as x";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["select 'GO' as x"]);
    }

    [Fact]
    public void text_with_no_go_comes_back_as_one_batch()
    {
        var sql = "create table foo (id int);\ncreate table bar (id int);";

        SqlServerBatchSplitter.Split(sql).ShouldBe(["create table foo (id int);\ncreate table bar (id int);"]);
    }

    [Fact]
    public void empty_text_produces_no_batches()
    {
        SqlServerBatchSplitter.Split("   \n\n  ").ShouldBeEmpty();
    }
}
