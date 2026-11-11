using System.Xml.Serialization;
using Jellyfin.Plugin.TranscodeGuard.Configuration;
using Jellyfin.Plugin.TranscodeGuard.Prerolls;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

public sealed class PrerollRulesTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);

    private readonly string _folder = Directory.CreateTempSubdirectory("transcode-guard-prerolls-").FullName;

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void TheFolderIsListedInFileNameOrderWithOnlyVisibleVideos()
    {
        foreach (var name in new[] { "02-studio.MKV", "01-welcome.mp4", "notes.txt", "._01-welcome.mp4", ".hidden.mp4", "10-outro.webm" })
        {
            File.WriteAllText(Path.Join(_folder, name), string.Empty);
        }

        Directory.CreateDirectory(Path.Join(_folder, "nested.mp4"));

        var files = PrerollRules.ListPrerollFiles(_folder + Path.DirectorySeparatorChar);

        Assert.Equal(new[] { "01-welcome.mp4", "02-studio.MKV", "10-outro.webm" }, files.Select(Path.GetFileName));
        Assert.All(files, file => Assert.True(Path.IsPathFullyQualified(file)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoFolderMeansNoPrerolls(string? folder)
    {
        Assert.Empty(PrerollRules.ListPrerollFiles(folder));
    }

    [Fact]
    public void AMissingFolderMeansNoPrerollsRatherThanAnError()
    {
        Assert.Empty(PrerollRules.ListPrerollFiles(Path.Join(_folder, "gone")));
    }

    [Fact]
    public void AnUnusablePathIsNoPrerollRatherThanAnError()
    {
        Assert.Empty(PrerollRules.ListPrerollFiles("bad\0folder"));
        Assert.False(PrerollRules.IsInFolder(Path.Join(_folder, "a\0.mp4"), _folder));
        Assert.False(PrerollRules.IsInFolder(Path.Join(_folder, "a.mp4"), "bad\0folder"));
    }

    [Fact]
    public void OnlyPathsInsideTheFolderCountAsPrerolls()
    {
        var root = Path.Join(_folder, "prerolls");

        Assert.True(PrerollRules.IsInFolder(Path.Join(root, "intro.mp4"), root));
        Assert.True(PrerollRules.IsInFolder(Path.Join(root, "intro.mp4"), root + Path.DirectorySeparatorChar));
        Assert.False(PrerollRules.IsInFolder(Path.Join(_folder, "prerolls-old", "intro.mp4"), root));
        Assert.False(PrerollRules.IsInFolder(Path.Join(_folder, "movie.mkv"), root));
        Assert.False(PrerollRules.IsInFolder(null, root));
        Assert.False(PrerollRules.IsInFolder(Path.Join(root, "intro.mp4"), " "));
    }

    [Theory]
    [InlineData(100, 99, true)]
    [InlineData(100, 0, true)]
    [InlineData(0, 0, false)]
    [InlineData(50, 49, true)]
    [InlineData(50, 50, false)]
    [InlineData(150, 99, true)]
    [InlineData(-5, 0, false)]
    public void TheChanceIsAPercentage(int chance, int roll, bool expected)
    {
        Assert.Equal(expected, PrerollRules.PassesChance(chance, roll));
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]
    [InlineData(500, 0, true)]
    [InlineData(500, -1, true)]
    public void TheSessionLimitCountsShowsAndZeroMeansNoLimit(int shown, int limit, bool expected)
    {
        Assert.Equal(expected, PrerollRules.IsUnderSessionLimit(shown, limit));
    }

    [Fact]
    public void PlayAllInOrderPlaysEveryPrerollInFileOrder()
    {
        var candidates = Candidates(seen: new[] { true, false, true });

        var picked = PrerollRules.Pick(candidates, oncePerUser: false, playAll: true, random: false, new Random(1));

        Assert.Equal(candidates, picked);
    }

    [Fact]
    public void OncePerUserDropsWhatTheUserHasSeen()
    {
        var candidates = Candidates(seen: new[] { true, false, true, false });

        var picked = PrerollRules.Pick(candidates, oncePerUser: true, playAll: true, random: false, new Random(1));

        Assert.Equal(new[] { candidates[1], candidates[3] }, picked);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OncePerUserShowsNothingOnceEverythingIsSeen(bool playAll, bool random)
    {
        var picked = PrerollRules.Pick(Candidates(seen: new[] { true, true }), oncePerUser: true, playAll, random, new Random(1));

        Assert.Empty(picked);
    }

    [Fact]
    public void PlayAllRandomShufflesEveryPrerollWithoutDroppingAny()
    {
        var candidates = Candidates(seen: new bool[8]);
        var orders = new HashSet<string>();

        for (var seed = 0; seed < 20; seed++)
        {
            var picked = PrerollRules.Pick(candidates, oncePerUser: false, playAll: true, random: true, new Random(seed));

            Assert.Equal(candidates.OrderBy(c => c.Id), picked.OrderBy(c => c.Id));
            orders.Add(string.Join(',', picked.Select(c => c.Id)));
        }

        Assert.True(orders.Count > 1, "twenty shuffles of eight pre-rolls all came out in the same order");
    }

    [Fact]
    public void OneRandomPicksAnyUnseenPreroll()
    {
        var candidates = Candidates(seen: new[] { false, true, false, false });
        var pickedIds = new HashSet<Guid>();

        for (var seed = 0; seed < 50; seed++)
        {
            var picked = Assert.Single(PrerollRules.Pick(candidates, oncePerUser: true, playAll: false, random: true, new Random(seed)));
            pickedIds.Add(picked.Id);
        }

        Assert.Equal(new[] { candidates[0].Id, candidates[2].Id, candidates[3].Id }.ToHashSet(), pickedIds);
    }

    [Fact]
    public void OneInOrderStartsWithTheFirstFile()
    {
        var candidates = Candidates(seen: new bool[3]);

        var picked = PrerollRules.Pick(candidates, oncePerUser: false, playAll: false, random: false, new Random(1));

        Assert.Equal(candidates[0], Assert.Single(picked));
    }

    [Fact]
    public void OneInOrderWalksTheListInsteadOfReplayingTheFirstFile()
    {
        var first = Candidate(lastSeen: Monday);
        var second = Candidate(lastSeen: null);
        var third = Candidate(lastSeen: null);

        Assert.Equal(second, Assert.Single(PrerollRules.Pick(new[] { first, second, third }, false, false, false, new Random(1))));

        // Once every file has been seen, the one seen longest ago comes round again.
        var all = new[] { first, second with { LastSeen = Monday.AddMinutes(10) }, third with { LastSeen = Monday.AddMinutes(20) } };
        Assert.Equal(first, Assert.Single(PrerollRules.Pick(all, false, false, false, new Random(1))));
    }

    [Fact]
    public void NoPrerollsMeansNothingToPick()
    {
        Assert.Empty(PrerollRules.Pick(Array.Empty<PrerollCandidate>(), false, true, true, new Random(1)));
    }

    [Fact]
    public void TheSessionCounterStopsAtTheLimitUntilTheSessionEnds()
    {
        var counter = new PrerollSessionCounter();
        var key = PrerollSessionCounter.Key(Guid.NewGuid(), "device-1");
        var otherDevice = PrerollSessionCounter.Key(Guid.NewGuid(), "device-2");

        Assert.True(counter.CanShow(key, 1));
        Assert.True(counter.TryRecordShow(key, 1));
        Assert.False(counter.CanShow(key, 1));
        Assert.False(counter.TryRecordShow(key, 1));
        Assert.True(counter.TryRecordShow(key, 0));
        Assert.True(counter.CanShow(otherDevice, 1));

        counter.Forget(key);

        Assert.True(counter.CanShow(key, 1));
    }

    [Fact]
    public void PrerollsAreOffByDefaultWithAFullChanceAndOneShowPerSession()
    {
        var config = new PluginConfiguration();

        Assert.False(config.EnablePrerolls);
        Assert.Equal(string.Empty, config.PrerollFolder);
        Assert.Equal(100, config.PrerollChancePercent);
        Assert.Equal(1, config.PrerollSessionLimit);
        Assert.False(config.PrerollOncePerUser);
        Assert.False(config.PrerollPlayAll);
        Assert.False(config.PrerollRandom);
        Assert.Empty(config.PrerollIncludedClientPatterns);
        Assert.Empty(config.PrerollExcludedClientPatterns);
        Assert.Empty(config.PrerollExcludedUserIds);
    }

    [Fact]
    public void PrerollSettingsRoundTripThroughXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            EnablePrerolls = true,
            PrerollFolder = "/media/prerolls",
            PrerollChancePercent = 35,
            PrerollSessionLimit = 0,
            PrerollOncePerUser = true,
            PrerollPlayAll = true,
            PrerollRandom = true,
            PrerollIncludedClientPatterns = new[] { "Jellyfin Web" },
            PrerollExcludedClientPatterns = new[] { "Roku" },
            PrerollExcludedUserIds = new[] { "4e6ea05698214e7c940d2af1806f1f8e" }
        };

        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var roundTripped = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.True(roundTripped.EnablePrerolls);
        Assert.Equal("/media/prerolls", roundTripped.PrerollFolder);
        Assert.Equal(35, roundTripped.PrerollChancePercent);
        Assert.Equal(0, roundTripped.PrerollSessionLimit);
        Assert.True(roundTripped.PrerollOncePerUser);
        Assert.True(roundTripped.PrerollPlayAll);
        Assert.True(roundTripped.PrerollRandom);
        Assert.Equal(new[] { "Jellyfin Web" }, roundTripped.PrerollIncludedClientPatterns);
        Assert.Equal(new[] { "Roku" }, roundTripped.PrerollExcludedClientPatterns);
        Assert.Equal(new[] { "4e6ea05698214e7c940d2af1806f1f8e" }, roundTripped.PrerollExcludedUserIds);
    }

    private static PrerollCandidate Candidate(DateTime? lastSeen)
    {
        return new PrerollCandidate(Guid.NewGuid(), lastSeen != null, lastSeen);
    }

    private static PrerollCandidate[] Candidates(bool[] seen)
    {
        return seen.Select((wasSeen, index) => new PrerollCandidate(Guid.NewGuid(), wasSeen, wasSeen ? Monday.AddMinutes(index) : null)).ToArray();
    }
}
