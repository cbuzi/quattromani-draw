using Quattromani.Core;

namespace Quattromani.Core.Tests;

public class CommandBusTests
{
    [Fact]
    public void Execute_creates_the_object_and_raises_the_version()
    {
        var bus = T.NewBus();
        var result = bus.Execute([new CreateShape { Type = "rectangle", Name = "a", Width = 10, Height = 10, Fill = "#FF0000" }]);
        Assert.Single(result.CreatedIds);
        Assert.Equal(1, bus.Version);
        Assert.Equal("a", bus.Document.Find(result.CreatedIds[0]).Name);
    }

    [Fact]
    public void A_batch_is_a_single_undo_step()
    {
        var bus = T.NewBus();
        bus.Execute([
            new CreateShape { Type = "rectangle", Name = "a", Width = 10, Height = 10 },
            new CreateShape { Type = "ellipse", Name = "b", Width = 10, Height = 10 },
        ]);
        Assert.Equal(2, bus.Document.Objects.Count());
        Assert.True(bus.Undo());
        Assert.Empty(bus.Document.Objects);
        Assert.False(bus.Undo());
    }

    [Fact]
    public void A_failing_batch_changes_nothing()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        var version = bus.Version;
        var ex = Assert.Throws<CommandException>(() => bus.Execute([
            new MoveObjects { Targets = ["a"], Dx = 50 },
            new MoveObjects { Targets = ["does-not-exist"], Dx = 1 },
        ]));
        Assert.Contains("not found", ex.Message);
        Assert.Equal(version, bus.Version);
        Assert.Equal(0, bus.Bounds("a").X);
    }

    [Fact]
    public void Redo_reapplies_what_undo_removed()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        bus.Execute([new MoveObjects { Targets = ["a"], Dx = 30 }]);
        bus.Undo();
        Assert.Equal(0, bus.Bounds("a").X);
        Assert.True(bus.Redo());
        Assert.Equal(30, bus.Bounds("a").X);
    }

    [Fact]
    public void GetChanges_lists_what_happened_after_a_version_and_who_did_it()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        var seen = bus.Version;
        bus.Execute([new MoveObjects { Targets = ["a"], Dx = 5 }]);
        bus.Execute([new MoveObjects { Targets = ["a"], Dx = 5 }], "llm:claude-code");

        var (version, changes) = bus.GetChanges(seen);
        Assert.Equal(bus.Version, version);
        Assert.Equal(["human", "llm:claude-code"], changes.Select(c => c.Author));
        Assert.All(changes, c => Assert.Equal("do", c.Kind));
    }

    [Fact]
    public void The_author_must_be_human_or_an_llm()
    {
        var bus = T.NewBus();
        Assert.Throws<ArgumentException>(() => bus.Execute([new CreateShape { Type = "rectangle", Width = 1, Height = 1 }], "robot"));
    }

    [Fact]
    public void Undo_history_is_capped()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        for (var i = 0; i < CommandBus.MaxUndoSteps + 50; i++) bus.Execute([new MoveObjects { Targets = ["a"], Dx = 1 }]);
        var steps = 0;
        while (bus.Undo()) steps++;
        Assert.Equal(CommandBus.MaxUndoSteps, steps);
    }

    [Fact]
    public void The_change_log_is_capped_and_says_how_far_it_goes_back()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        for (var i = 0; i < CommandBus.MaxLogEntries + 100; i++) bus.Execute([new MoveObjects { Targets = ["a"], Dx = 1 }]);
        Assert.True(bus.LogFloor > 0);
        var (_, changes) = bus.GetChanges(0);
        Assert.True(changes.Count <= CommandBus.MaxLogEntries);
        Assert.Equal(bus.LogFloor + 1, changes[0].Version); // what remains is contiguous up to the newest
    }

    [Fact]
    public void Replacing_the_document_leaves_a_reset_entry_and_clears_undo()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        var seen = bus.Version;
        bus.Replace(new Document(), "new", null);
        Assert.Empty(bus.Document.Objects);
        Assert.False(bus.Undo());
        Assert.Contains(bus.GetChanges(seen).Changes, c => c.Kind == "reset");
    }
}
