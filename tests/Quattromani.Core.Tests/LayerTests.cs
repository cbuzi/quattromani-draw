using Quattromani.Core;

namespace Quattromani.Core.Tests;

public class LayerTests
{
    [Fact]
    public void New_objects_go_to_the_active_layer_or_the_named_one()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        bus.Execute([new AddLayer { Name = "sketch" }]); // becomes active
        bus.Rect("b");
        bus.Rect("c", layer: "Layer 1");
        var layers = bus.Document.Pages[0].Layers;
        Assert.Equal(["a", "c"], layers[0].Objects.Select(o => o.Name));
        Assert.Equal(["b"], layers[1].Objects.Select(o => o.Name));
    }

    [Fact]
    public void A_locked_layer_refuses_edits_until_it_is_unlocked()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        bus.Execute([new SetLayer { Layer = "Layer 1", Locked = true }]);
        var ex = Assert.Throws<CommandException>(() => bus.Execute([new MoveObjects { Targets = ["a"], Dx = 5 }]));
        Assert.Contains("locked", ex.Message);
        Assert.Throws<CommandException>(() => bus.Rect("b")); // nothing can be added either

        bus.Execute([new SetLayer { Layer = "Layer 1", Locked = false }]);
        bus.Execute([new MoveObjects { Targets = ["a"], Dx = 5 }]);
        Assert.Equal(5, bus.Bounds("a").X);
    }

    [Fact]
    public void A_locked_object_refuses_edits_and_can_be_unlocked()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        bus.Execute([new SetObjectState { Targets = ["a"], Locked = true }]);
        Assert.Throws<CommandException>(() => bus.Execute([new SetStyle { Targets = ["a"], Fill = "#00FF00" }]));
        bus.Execute([new SetObjectState { Targets = ["a"], Locked = false }]);
        bus.Execute([new SetStyle { Targets = ["a"], Fill = "#00FF00" }]);
        Assert.Equal("#00FF00", bus.Document.Find("a").Fill);
    }

    [Fact]
    public void Content_of_a_locked_group_is_locked()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        bus.Rect("b", x: 200);
        bus.Execute([new GroupObjects { Targets = ["a", "b"], Name = "g" }]);
        bus.Execute([new SetObjectState { Targets = ["g"], Locked = true }]);
        Assert.Throws<CommandException>(() => bus.Execute([new MoveObjects { Targets = ["a"], Dx = 1 }]));
    }

    [Fact]
    public void Hidden_layers_and_objects_are_not_rendered_or_pickable()
    {
        var bus = T.NewBus();
        bus.Rect("a");
        bus.Rect("b");
        bus.Execute([new SetObjectState { Targets = ["b"], Hidden = true }]);
        Assert.Equal(["a"], bus.Document.RenderObjects.Select(o => o.Name));
        bus.Execute([new SetLayer { Layer = "Layer 1", Visible = false }]);
        Assert.Empty(bus.Document.RenderObjects);
        Assert.Empty(bus.Document.PickableObjects);
    }

    [Fact]
    public void Creating_into_a_hidden_layer_fails_with_an_explanation()
    {
        var bus = T.NewBus();
        bus.Execute([new SetLayer { Layer = "Layer 1", Visible = false }]);
        var ex = Assert.Throws<CommandException>(() => bus.Rect("a"));
        Assert.Contains("hidden", ex.Message);
    }

    [Fact]
    public void Deleting_a_layer_removes_its_objects_and_undo_brings_them_back()
    {
        var bus = T.NewBus();
        bus.Execute([new AddLayer { Name = "sketch" }]);
        bus.Rect("a");
        bus.Execute([new DeleteLayer { Layer = "sketch" }]);
        Assert.Single(bus.Document.Pages[0].Layers);
        Assert.Empty(bus.Document.Objects);
        bus.Undo();
        Assert.Equal(2, bus.Document.Pages[0].Layers.Count);
        Assert.Equal("a", bus.Document.Find("a").Name);
    }

    [Fact]
    public void The_last_layer_cannot_be_deleted_and_names_must_be_unique()
    {
        var bus = T.NewBus();
        Assert.Throws<CommandException>(() => bus.Execute([new DeleteLayer { Layer = "Layer 1" }]));
        Assert.Throws<CommandException>(() => bus.Execute([new AddLayer { Name = "LAYER 1" }]));
    }

    [Fact]
    public void Moving_an_object_to_another_layer_keeps_it_where_it_appears()
    {
        var bus = T.NewBus();
        bus.Rect("a", x: 0, y: 0, w: 10, h: 10);
        bus.Rect("b", x: 100, y: 100, w: 10, h: 10);
        bus.Execute([new GroupObjects { Targets = ["a", "b"], Name = "g" }, new RotateObjects { Targets = ["g"], Degrees = 30 }]);
        bus.Execute([new AddLayer { Name = "sketch", Activate = false }]);
        var before = bus.Bounds("a");

        bus.Execute([new MoveToLayer { Targets = ["a"], Layer = "sketch" }]);

        Assert.Equal(["a"], bus.Document.Pages[0].Layers[1].Objects.Select(o => o.Name));
        T.AssertBox(before, bus.Bounds("a"));
        bus.Undo();
        T.AssertBox(before, bus.Bounds("a"));
        Assert.Equal("g", bus.Document.LayerOf(bus.Document.Find("a"))!.Objects[0].Name);
    }

    [Fact]
    public void Setting_the_active_layer_on_the_bus_does_not_count_as_a_change()
    {
        var bus = T.NewBus();
        bus.Execute([new AddLayer { Name = "sketch", Activate = false }]);
        var version = bus.Version;
        bus.SetActiveLayer("sketch");
        Assert.Equal(version, bus.Version);
        Assert.Equal("sketch", bus.Document.Pages[0].ActiveLayer.Name);
    }
}
