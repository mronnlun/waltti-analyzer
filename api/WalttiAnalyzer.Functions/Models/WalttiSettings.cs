namespace WalttiAnalyzer.Functions.Models;

public class WalttiSettings
{
    public string DatabasePath { get; set; } = "data/waltti.db";
    public string FeedId { get; set; } = "Vaasa";
    public string DigitransitApiUrl { get; set; } = "https://api.digitransit.fi/routing/v2/waltti/gtfs/v1";
    public string DigitransitApiKey { get; set; } = "";
}
