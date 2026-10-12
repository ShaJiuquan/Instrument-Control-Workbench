using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using InstrumentControlWorkbench.App.ViewModels;

namespace InstrumentControlWorkbench.App.Controls;

public sealed class PlanFlowEdges : Control
{
    public static readonly StyledProperty<ObservableCollection<PlanGraphNodeViewModel>?> NodesProperty =
        AvaloniaProperty.Register<PlanFlowEdges, ObservableCollection<PlanGraphNodeViewModel>?>(nameof(Nodes));
    private ObservableCollection<PlanGraphNodeViewModel>? observed;
    private bool attached;
    public ObservableCollection<PlanGraphNodeViewModel>? Nodes { get => GetValue(NodesProperty); set => SetValue(NodesProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) { base.OnPropertyChanged(change); if (change.Property == NodesProperty) { Observe(); InvalidateVisual(); } }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); attached = true; Observe(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { attached = false; Observe(); base.OnDetachedFromVisualTree(e); }
    private void Observe()
    {
        if (observed is not null) observed.CollectionChanged -= Changed;
        observed = attached ? Nodes : null;
        if (observed is not null) observed.CollectionChanged += Changed;
    }
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Nodes is null) return;
        var nodes = Nodes.ToDictionary(n => n.Id); var pen = new Pen(Brush.Parse("#B5BACA"), 1.6);
        foreach (var node in Nodes)
        {
            if (node.Definition.Parent is not { } parentId || !nodes.TryGetValue(parentId, out var parent)) continue;
            var from = new Point(parent.X + 220, parent.Y + 35); var to = new Point(node.X, node.Y + 35);
            var elbow = Math.Max(from.X + 5, to.X - 10);
            context.DrawLine(pen, from, new(elbow, from.Y)); context.DrawLine(pen, new(elbow, from.Y), new(elbow, to.Y)); context.DrawLine(pen, new(elbow, to.Y), to);
            context.DrawLine(pen, to, to + new Vector(-5, -4)); context.DrawLine(pen, to, to + new Vector(-5, 4));
        }
    }
}
