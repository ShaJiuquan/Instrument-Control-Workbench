using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Core.Experiments;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanWorkspaceViewModel
{
    private bool loadingNode;
    [ObservableProperty] private PlanGraphNodeViewModel? selectedNode;
    [ObservableProperty] private string nodeFragment = "";
    [ObservableProperty] private bool nodeFragmentDirty;
    [ObservableProperty] private string paletteKind = "等待";
    public ObservableCollection<PlanGraphNodeViewModel> GraphNodes { get; } = new();
    public string[] PaletteKinds { get; } = ["等待", "顺序", "重复", "超时保护"];
    public bool SupportsGraph => workspace.SupportsStructureEditing;
    public double GraphWidth => GraphNodes.Count == 0 ? 340 : GraphNodes.Max(n => n.X) + 240;
    public double GraphHeight => Math.Max(220, GraphNodes.Count * 86 + 24);
    public string SelectedNodeTitle => SelectedNode is null ? "选择流程节点" : $"{SelectedNode.Title} · {SelectedNode.Id.Value}";
    public bool CanEditNode => CanEdit && validated is not null && SelectedNode is not null;
    public bool IsNodeReadOnly => !CanEditNode;
    private bool CanApplyNode() => CanEditNode && NodeFragmentDirty;
    private bool CanDiscardNode() => CanEditNode && NodeFragmentDirty;
    private bool CanAppendNode() => CanEditNode && !NodeFragmentDirty && SelectedNode!.Definition.CanAppend;
    private bool CanRemoveNode() => CanEditNode && !NodeFragmentDirty && SelectedNode!.Definition.CanRemove;
    private bool CanMoveEarlier() => CanEditNode && !NodeFragmentDirty && SelectedNode!.Definition.CanMoveEarlier;
    private bool CanMoveLater() => CanEditNode && !NodeFragmentDirty && SelectedNode!.Definition.CanMoveLater;
    partial void OnSelectedNodeChanged(PlanGraphNodeViewModel? value)
    {
        foreach (var node in GraphNodes) node.IsSelected = node == value;
        LoadSelectedFragment(); OnPropertyChanged(nameof(SelectedNodeTitle)); RefreshGraphCommands();
    }
    partial void OnNodeFragmentChanged(string value)
    {
        if (loadingNode) return;
        NodeFragmentDirty = true; HasError = false; Message = "节点修改尚未应用；应用并验证或撤销后才能运行。";
        RefreshCommands(); changed();
    }
    partial void OnNodeFragmentDirtyChanged(bool value) { RefreshCommands(); changed(); }
    private void RefreshGraphCommands()
    {
        OnPropertyChanged(nameof(CanEditNode)); OnPropertyChanged(nameof(IsNodeReadOnly)); OnPropertyChanged(nameof(SelectedNodeTitle));
        ApplyNodeCommand.NotifyCanExecuteChanged(); DiscardNodeCommand.NotifyCanExecuteChanged(); AppendNodeCommand.NotifyCanExecuteChanged();
        RemoveNodeCommand.NotifyCanExecuteChanged(); MoveEarlierCommand.NotifyCanExecuteChanged(); MoveLaterCommand.NotifyCanExecuteChanged();
        foreach (var node in GraphNodes) node.SelectCommand.NotifyCanExecuteChanged();
    }
    private void ClearGraph()
    {
        GraphNodes.Clear(); loadingNode = true;
        try { SelectedNode = null; NodeFragment = ""; NodeFragmentDirty = false; }
        finally { loadingNode = false; }
        OnPropertyChanged(nameof(GraphWidth)); OnPropertyChanged(nameof(GraphHeight));
    }
    private void RebuildGraph(ExperimentPlan plan)
    {
        if (!SupportsGraph) { ClearGraph(); return; }
        var old = SelectedNode?.Id; GraphNodes.Clear();
        var definitions = workspace.DescribeStructure(plan);
        var outlineById = Outline.ToDictionary(n => n.Id);
        foreach (var definition in definitions)
        {
            outlineById.TryGetValue(definition.Id, out var outline);
            GraphNodes.Add(new(definition, outline?.Title ?? "测量步骤", outline?.Detail ?? definition.Id.Value,
                definition.Depth * 240 + 12, GraphNodes.Count * 86 + 12, SelectGraphNode,
                () => !disposed && !IsDocumentBusy && !NodeFragmentDirty));
        }
        SelectedNode = GraphNodes.FirstOrDefault(n => n.Id == old) ?? GraphNodes.FirstOrDefault();
        LoadSelectedFragment(); OnPropertyChanged(nameof(GraphWidth)); OnPropertyChanged(nameof(GraphHeight));
    }
    private void SelectGraphNode(PlanGraphNodeViewModel node)
    {
        if (NodeFragmentDirty && node != SelectedNode || disposed || IsDocumentBusy || !GraphNodes.Contains(node)) return;
        SelectedNode = node;
    }
    private void LoadSelectedFragment()
    {
        loadingNode = true;
        try { NodeFragment = SelectedNode is null || validated is null ? "" : workspace.ReadFragment(validated.Plan, SelectedNode.Id); NodeFragmentDirty = false; }
        finally { loadingNode = false; }
    }
    [RelayCommand(CanExecute = nameof(CanApplyNode))]
    private void ApplyNode() { if (CanApplyNode()) EditNode(new(PlanStructureEditKind.Replace, SelectedNode!.Id, NodeFragment)); }
    [RelayCommand(CanExecute = nameof(CanDiscardNode))]
    private void DiscardNode() { if (CanDiscardNode()) { LoadSelectedFragment(); HasError = false; Message = "节点修改已撤销。"; RefreshCommands(); changed(); } }
    [RelayCommand(CanExecute = nameof(CanAppendNode))]
    private void AppendNode()
    {
        if (!CanAppendNode()) return;
        var id = "step-" + Guid.NewGuid().ToString("N");
        var fragment = PaletteKind switch
        {
            "delay" or "等待" => $"{{\"id\":\"{id}\",\"kind\":\"delay\",\"seconds\":1}}",
            "sequence" or "顺序" => $"{{\"id\":\"{id}\",\"kind\":\"sequence\",\"children\":[]}}",
            "repeat" or "重复" => $"{{\"id\":\"{id}\",\"kind\":\"repeat\",\"cycles\":2,\"body\":{{\"id\":\"{id}-body\",\"kind\":\"sequence\",\"children\":[]}}}}",
            "timeout" or "超时保护" => $"{{\"id\":\"{id}\",\"kind\":\"timeout\",\"timeoutSeconds\":30,\"body\":{{\"id\":\"{id}-body\",\"kind\":\"sequence\",\"children\":[]}}}}",
            _ => null,
        };
        if (fragment is null) { HasError = true; Message = "选择可用的步骤类型。"; return; }
        EditNode(new(PlanStructureEditKind.Append, SelectedNode!.Id, fragment));
    }
    [RelayCommand(CanExecute = nameof(CanRemoveNode))]
    private void RemoveNode() { if (CanRemoveNode()) EditNode(new(PlanStructureEditKind.Remove, SelectedNode!.Id)); }
    [RelayCommand(CanExecute = nameof(CanMoveEarlier))]
    private void MoveEarlier() { if (CanMoveEarlier()) EditNode(new(PlanStructureEditKind.MoveEarlier, SelectedNode!.Id)); }
    [RelayCommand(CanExecute = nameof(CanMoveLater))]
    private void MoveLater() { if (CanMoveLater()) EditNode(new(PlanStructureEditKind.MoveLater, SelectedNode!.Id)); }
    private void EditNode(PlanStructureEdit edit)
    {
        try
        {
            var result = workspace.EditStructure(validated!.Plan, edit); Apply(result);
            SelectedNode = GraphNodes.FirstOrDefault(n => n.Id == edit.Selected) ?? GraphNodes.FirstOrDefault();
            IsDirty = true; Message = "流程修改已通过完整计划验证，可运行或保存。";
        }
        catch (Exception error) { HasError = true; Message = $"流程修改未应用，原计划保留：{error.Message}"; }
        RefreshCommands(); changed();
    }
}

public partial class PlanGraphNodeViewModel : ObservableObject
{
    public PlanGraphNodeViewModel(PlanStructureNode definition, string title, string detail, double x, double y,
        Action<PlanGraphNodeViewModel> select, Func<bool> canSelect)
    {
        Definition = definition; Title = title; Detail = detail; X = x; Y = y;
        SelectCommand = new RelayCommand(() => select(this), canSelect);
    }
    public PlanStructureNode Definition { get; }
    public StepId Id => Definition.Id;
    public string Title { get; }
    public string Detail { get; }
    public double X { get; }
    public double Y { get; }
    public string Relation => Definition.Parent is null ? "开始" : Definition.Slot switch
    {
        "children" => $"步骤 {Definition.Position + 1}",
        "body" => "内部步骤",
        "condition" => "检查",
        "then" => "成立分支",
        "else" => "否则分支",
        "success" => "成功回调",
        "failure" => "失败回调",
        "readings" => "分析输入",
        _ => Definition.Slot,
    };
    public IRelayCommand SelectCommand { get; }
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isActive;
}
