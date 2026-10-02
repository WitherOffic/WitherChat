namespace WitherChat.Desktop.Models;

public static class TutorialCatalog
{
    public const int QuickStartStepCount = 10;

    private static readonly IReadOnlyDictionary<TutorialTopic, int> ContextStepCounts =
        new Dictionary<TutorialTopic, int>
        {
            [TutorialTopic.Channels] = 3,
            [TutorialTopic.Logs] = 4,
            [TutorialTopic.Moderation] = 5,
            [TutorialTopic.Connect] = 3,
            [TutorialTopic.Settings] = 8,
            [TutorialTopic.Donations] = 4,
            [TutorialTopic.DonationsSetup] = 2,
            [TutorialTopic.Events] = 2,
            [TutorialTopic.Protection] = 3,
            [TutorialTopic.Moments] = 2,
            [TutorialTopic.SmartChat] = 2
        };

    public static IEnumerable<KeyValuePair<TutorialTopic, int>> ContextGuides => ContextStepCounts;

    public static bool IsContextTopic(TutorialTopic topic) => ContextStepCounts.ContainsKey(topic);

    public static int GetStepCount(TutorialTopic topic) => topic == TutorialTopic.QuickStart
        ? QuickStartStepCount
        : ContextStepCounts.GetValueOrDefault(topic, 1);
}
