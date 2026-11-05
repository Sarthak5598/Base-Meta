using System.Diagnostics;
using DataLens.Core;

namespace DataLens.Tests;

public class FuzzyMatchTests
{
    private static readonly string[] Statuses = { "paid", "pending", "refunded", "shipped" };

    private static FuzzyMatch.Result Rank(string query, params string[] candidates) =>
        FuzzyMatch.Rank(candidates.Length == 0 ? Statuses : candidates, query);

    // ---- typos -----------------------------------------------------------------------------

    [Theory]
    [InlineData("shiped", "shipped")]     // a missing letter
    [InlineData("shippped", "shipped")]   // an extra letter
    [InlineData("shopped", "shipped")]    // a wrong letter
    [InlineData("pendng", "pending")]
    [InlineData("refundd", "refunded")]
    [InlineData("pednign", "pending")]    // two pairs of neighbouring letters swapped: two edits
    public void A_typo_finds_the_value_under_did_you_mean(string typed, string expected)
    {
        var r = Rank(typed);
        Assert.Empty(r.Matches);
        Assert.Equal(new[] { expected }, r.Close);
    }

    [Fact]
    public void Swapping_two_neighbouring_letters_is_one_typo()
    {
        var r = Rank("teh", "the", "tea", "ten");
        Assert.Contains("the", r.Close);
    }

    [Fact]
    public void A_half_typed_word_with_a_slip_still_finds_it()
    {
        // "shipp" is a start of "shipped"; "shpp" is that with a letter missing
        Assert.Equal(new[] { "shipped" }, Rank("shpp").Close);
        Assert.Equal(new[] { "refunded" }, Rank("refnd").Close);
    }

    [Fact]
    public void A_typo_in_one_word_of_several_is_forgiven()
    {
        var r = Rank("new yrk", "New York", "Newark", "Boston");
        Assert.Equal("New York", r.Close[0]);                       // one edit away, so it comes first
        Assert.Contains("Newark", r.Close);                         // two edits away: a fair second guess
        Assert.DoesNotContain("Boston", r.Close);
        Assert.Equal("John Smith", Rank("jonh", "John Smith", "Jane Doe").Close[0]);   // swapped inside a word
    }

    [Fact]
    public void Nothing_close_gives_nothing()
    {
        var r = Rank("zzzzzz");
        Assert.Empty(r.Matches);
        Assert.Empty(r.Close);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    public void One_or_two_letters_get_no_typo_forgiveness_since_they_would_match_everything(string typed)
    {
        Assert.Empty(Rank(typed, "ba", "bb", "xa").Close);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(9, 2)]
    [InlineData(10, 3)]
    [InlineData(40, 3)]
    public void Longer_words_forgive_more_typos(int length, int edits) =>
        Assert.Equal(edits, FuzzyMatch.MaxEdits(length));

    [Fact]
    public void A_value_that_is_all_typo_is_not_a_match()
    {
        // "abc" allows one typo, but nothing here is one typo from "abc"; a value that needs as many edits
        // as the query has letters is a different word, not a misspelling
        Assert.Empty(Rank("abc", "xyz", "q").Close);
    }

    // ---- ordering of real matches ----------------------------------------------------------

    [Fact]
    public void Matches_are_ordered_exact_then_prefix_then_word_start_then_anywhere()
    {
        var r = Rank("paid", "xpaid", "paid", "partially paid", "paidup", "unpaid", "a paid");
        Assert.Equal(new[] { "paid", "paidup", "a paid", "partially paid", "xpaid", "unpaid" }, r.Matches);
    }

    [Fact]
    public void Within_a_tier_shorter_values_come_first_then_alphabetical()
    {
        Assert.Equal(new[] { "ship", "shipped", "shipment", "shipping" }, Rank("ship", "shipping", "shipment", "shipped", "ship").Matches);
        Assert.Equal(new[] { "ab", "abc", "abcd" }, Rank("a", "abcd", "ab", "abc").Matches);
    }

    [Fact]
    public void A_real_match_never_also_appears_under_did_you_mean()
    {
        var r = Rank("pend", "pending", "pendant", "bend");
        Assert.Equal(new[] { "pendant", "pending" }, r.Matches.OrderBy(x => x).ToArray());
        Assert.DoesNotContain("pending", r.Close);
        Assert.Contains("bend", r.Close);   // one letter off "pend"
    }

    [Fact]
    public void Closer_spellings_come_first_and_ones_too_far_off_are_left_out()
    {
        // "shipped": "shopped" and "shiped" are one edit away; "shpd" needs three, more than 7 letters allow
        var r = Rank("shipped", "shpd", "shiped", "shopped");
        Assert.Empty(r.Matches);
        Assert.Equal(new[] { "shopped", "shiped" }, r.Close);   // same distance: the one nearer in length first
        Assert.DoesNotContain("shpd", r.Close);
    }

    // ---- what counts as the same ----------------------------------------------------------

    [Fact]
    public void Case_accents_and_extra_spaces_do_not_matter()
    {
        Assert.Equal(new[] { "Café Noir" }, Rank("cafe", "Café Noir", "Tea").Matches);
        Assert.Equal(new[] { "München" }, Rank("MUNCHEN", "München", "Berlin").Matches);
        Assert.Equal(new[] { "New   York" }, Rank("new york", "New   York").Matches);
        Assert.Equal(new[] { "PAID" }, Rank("  pAiD ", "PAID", "unpaid").Matches.Take(1).ToArray());
    }

    [Fact]
    public void An_empty_search_lists_everything_in_order()
    {
        var r = FuzzyMatch.Rank(new[] { "pending", "Paid", "shipped" }, "");
        Assert.Equal(new[] { "Paid", "pending", "shipped" }, r.Matches);
        Assert.Empty(r.Close);
        Assert.Equal(new[] { "Paid", "pending", "shipped" }, FuzzyMatch.Rank(new[] { "pending", "Paid", "shipped" }, null).Matches);
    }

    // ---- the distance itself ---------------------------------------------------------------

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("ab", "ba", 1)]          // a swap is one edit
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("same", "same", 0)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("ca", "abc", 3)]         // optimal string alignment, not full Damerau
    public void Edit_distance(string a, string b, int expected)
    {
        Assert.Equal(expected, FuzzyMatch.Distance(a, b));
        Assert.Equal(expected, FuzzyMatch.Distance(b, a));
    }

    // ---- safety and speed ------------------------------------------------------------------

    [Fact]
    public void A_huge_value_is_still_matched_as_text_but_never_by_edit_distance()
    {
        var huge = new string('x', 5000) + "needle";
        Assert.Contains(huge, Rank("needle", huge, "other").Matches);
        Assert.Empty(Rank("needl3", huge, "other").Close);   // too long to compare letter by letter
    }

    [Fact]
    public void A_thousand_values_rank_quickly()
    {
        var values = Enumerable.Range(0, 1000).Select(i => $"customer {i} from somewhere").ToList();
        var watch = Stopwatch.StartNew();
        var r = FuzzyMatch.Rank(values, "custmer 42");
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 1500, $"took {watch.ElapsedMilliseconds} ms");
        Assert.NotEmpty(r.Close.Concat(r.Matches));
    }

    [Fact]
    public void Odd_characters_do_not_break_it()
    {
        var r = FuzzyMatch.Rank(new[] { "50% off!", "a_b", "x\\y", "", "   " }, "50%");
        Assert.Contains("50% off!", r.Matches);
        Assert.Empty(FuzzyMatch.Rank(Array.Empty<string>(), "anything").Matches);
    }
}
