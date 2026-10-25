using Quattromani.Core;

namespace Quattromani.Core.Tests;

public class AgentRegistryTests
{
    static (AgentRegistry Registry, Func<DateTime> Now, Action<TimeSpan> Advance) Make()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        return (new AgentRegistry(() => t), () => t, d => t += d);
    }

    [Fact]
    public void An_agent_is_active_after_a_request_and_idle_after_the_window()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var r = new AgentRegistry(() => t);
        r.Touch("s1", "claude-code");
        Assert.Equal(["claude-code"], r.Active().Select(a => a.Name));
        t += AgentRegistry.IdleAfter + TimeSpan.FromSeconds(1);
        Assert.Empty(r.Active());
    }

    [Fact]
    public void Sessions_of_the_same_client_collapse_and_a_null_name_keeps_the_known_one()
    {
        var r = new AgentRegistry();
        r.Touch("s1", "codex");
        r.Touch("s2", "codex");
        r.Touch("s1", null);
        Assert.Equal(["codex"], r.Active().Select(a => a.Name));
    }

    [Fact]
    public void Remove_drops_the_session_and_Changed_fires_only_on_real_changes()
    {
        var r = new AgentRegistry();
        var changes = 0;
        r.Changed += () => changes++;
        r.Touch("s1", "claude-code");
        r.Touch("s1", "claude-code"); // same name, still fresh: nothing new to show
        Assert.Equal(1, changes);
        r.Remove("s1");
        r.Remove("s1");
        Assert.Equal(2, changes);
        Assert.Empty(r.Active());
    }

    [Fact]
    public void Active_reports_sessions_and_the_last_drawing_of_each_agent()
    {
        var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var r = new AgentRegistry(() => t);
        r.Touch("s1", "claude-code");
        r.Touch("s2", "claude-code");
        r.Touch("s3", "codex");
        r.Drew("claude-code");
        t += TimeSpan.FromSeconds(30);
        r.Drew("claude-code");
        var a = r.Active().Single(x => x.Name == "claude-code");
        Assert.Equal(2, a.Sessions);
        Assert.Equal(2, a.Commands);
        Assert.Equal(t, a.LastDraw);
        Assert.Null(r.Active().Single(x => x.Name == "codex").LastDraw);
    }
}
