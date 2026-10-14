using OpportunityPilot.Application.Research.Rules;

namespace OpportunityPilot.UnitTests.Research;

public class PlaceMatchTests
{
    [Theory]
    [InlineData("Ghaziabad, Uttar Pradesh, IN", "India")]
    [InlineData("New Delhi, Delhi, IN", "India")]
    [InlineData("Kochi, Kerala, IN", "India")]
    [InlineData("Pune, Maharashtra, India", "India")]
    [InlineData("Bangalore, Karnataka", "Bengaluru")]
    [InlineData("Gurugram, Haryana, IN", "Gurgaon")]
    [InlineData("Noida, Uttar Pradesh, IN", "Delhi NCR")]
    [InlineData("Berlin, Germany", "Germany")]
    [InlineData("Austin, TX, US", "United States")]
    public void A_country_or_a_second_name_matches_the_place_a_posting_states(string location, string configured) =>
        Assert.True(PlaceMatch.InField(location, configured));

    [Theory]
    [InlineData("London, UK", "India")]
    [InlineData("Remote, working in the evenings", "India")]
    [InlineData("Berlin, Germany", "India")]
    [InlineData("Pune, Maharashtra, IN", "Germany")]
    [InlineData("Mumbai, IN", "Delhi NCR")]
    [InlineData("", "India")]
    public void Other_places_and_the_word_in_do_not_match(string location, string configured) =>
        Assert.False(PlaceMatch.InField(location, configured));

    [Fact]
    public void A_place_that_is_not_a_known_group_is_left_to_the_ordinary_word_match() => Assert.False(PlaceMatch.InField("Pune, IN", "Atlantis"));

    [Theory]
    [InlineData("Anywhere", true)]
    [InlineData("Worldwide", true)]
    [InlineData("Remote", true)]
    [InlineData("Pune", false)]
    [InlineData(null, false)]
    public void Anywhere_is_not_a_place_a_filter_can_fail_on(string? location, bool unspecific) => Assert.Equal(unspecific, PlaceMatch.IsUnspecific(location));
}
