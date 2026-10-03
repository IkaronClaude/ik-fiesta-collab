using System.Text.Json;
using Fiesta.Collab.Core.Models;
using Shouldly;
using Xunit;

namespace Fiesta.Collab.Core.Tests;

public class BuildAsTests
{
    [Fact]
    public void Target_survives_a_save_and_reload_of_the_metadata()
    {
        var map = new Dictionary<string, BuildAs.Target> { ["server"] = new("overlay", "Shine/View") };
        var meta = new Dictionary<string, object> { [BuildAs.MetadataKey] = BuildAs.ToJson(map) };
        BuildAs.For(meta, "server").ShouldBe(new BuildAs.Target("overlay", "Shine/View"));

        var saved = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(meta))!;
        BuildAs.For(saved, "server").ShouldBe(new BuildAs.Target("overlay", "Shine/View"));
    }

    [Fact]
    public void Other_envs_and_tables_without_the_rule_build_their_own_view()
    {
        var meta = new Dictionary<string, object>
        {
            [BuildAs.MetadataKey] = BuildAs.ToJson(new() { ["server"] = new("overlay", "Shine") })
        };
        BuildAs.For(meta, "client").ShouldBeNull();
        BuildAs.For(new Dictionary<string, object>(), "server").ShouldBeNull();
        BuildAs.For(null, "server").ShouldBeNull();
    }
}
