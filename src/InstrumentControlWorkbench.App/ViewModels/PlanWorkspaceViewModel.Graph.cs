using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanWorkspaceViewModel
{
    private bool loadingNode;
    private bool jsonFragmentEdited;
    [ObservableProperty] private PlanGraphNodeViewModel? selectedNode;
    [ObservableProperty] private string nodeFragment = "";
    [ObservableProperty] private bool nodeFragmentDirty;
    [ObservableProperty] private string paletteKind = "等待";
    [ObservableProperty] private PlanStepFormViewModel? nodeForm;
    [ObservableProperty] private PlanStepFormViewModel? paletteForm;
    [ObservableProperty] private bool showAdvancedNode;
    public ObservableCollection<PlanGraphNodeViewModel> GraphNodes { get; } = new();
    public string[] PaletteKinds { get; } = ["等待", "顺序", "重复", "超时保护", "重试", "读取仪器", "设置参数", "参数扫描", "源输出"];
    public bool SupportsNodeForm => NodeForm?.IsSupported == true;
    public bool HasPaletteForm => PaletteForm is not null;
    public bool IsNodeFormVisible => SupportsNodeForm && !ShowAdvancedNode;
    public bool IsNodeJsonVisible => !SupportsNodeForm || ShowAdvancedNode;
    public string NodeEditorToggleText => ShowAdvancedNode ? "参数表单" : "高级 JSON";
    public bool SupportsGraph => workspace.SupportsStructureEditing;
    public double GraphWidth => GraphNodes.Count == 0 ? 340 : GraphNodes.Max(n => n.X) + 240;
    public double GraphHeight => Math.Max(220, GraphNodes.Count * 86 + 24);
    public string SelectedNodeTitle => SelectedNode is null ? "选择流程节点" : $"{SelectedNode.Title} · {SelectedNode.Id.Value}";
    public bool CanEditNode => CanEdit && validated is not null && SelectedNode is not null;
    public bool IsNodeReadOnly => !CanEditNode || NodeForm?.IsDirty == true;
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
        jsonFragmentEdited = true;
        NodeFragmentDirty = true; HasError = false; Message = "节点修改尚未应用；应用并验证或撤销后才能运行。";
        RefreshCommands(); changed();
    }
    partial void OnNodeFragmentDirtyChanged(bool value) { RefreshCommands(); changed(); }
    partial void OnNodeFormChanged(PlanStepFormViewModel? value) { OnPropertyChanged(nameof(SupportsNodeForm)); NotifyEditorMode(); }
    partial void OnShowAdvancedNodeChanged(bool value) => NotifyEditorMode();
    private void NotifyEditorMode()
    {
        OnPropertyChanged(nameof(IsNodeFormVisible)); OnPropertyChanged(nameof(IsNodeJsonVisible)); OnPropertyChanged(nameof(NodeEditorToggleText)); ToggleNodeEditorCommand.NotifyCanExecuteChanged();
    }
    private bool CanToggleNodeEditor() => !disposed && !IsDocumentBusy && SupportsNodeForm;
    [RelayCommand(CanExecute = nameof(CanToggleNodeEditor))]
    private void ToggleNodeEditor() { if (CanToggleNodeEditor()) ShowAdvancedNode = !ShowAdvancedNode; }
    partial void OnPaletteFormChanged(PlanStepFormViewModel? value) => OnPropertyChanged(nameof(HasPaletteForm));
    partial void OnPaletteKindChanged(string value) { ResetPaletteForm(); RefreshGraphCommands(); }
    private void FormChanged()
    {
        if (loadingNode) return;
        NodeFragmentDirty = true; HasError = false; Message = "表单修改尚未应用；应用并验证或撤销后才能运行。";
        RefreshCommands(); changed();
    }
    private void RefreshGraphCommands()
    {
        OnPropertyChanged(nameof(CanEditNode)); OnPropertyChanged(nameof(IsNodeReadOnly)); OnPropertyChanged(nameof(SelectedNodeTitle));
        ApplyNodeCommand.NotifyCanExecuteChanged(); DiscardNodeCommand.NotifyCanExecuteChanged(); AppendNodeCommand.NotifyCanExecuteChanged();
        RemoveNodeCommand.NotifyCanExecuteChanged(); MoveEarlierCommand.NotifyCanExecuteChanged(); MoveLaterCommand.NotifyCanExecuteChanged();
        foreach (var node in GraphNodes) node.SelectCommand.NotifyCanExecuteChanged();
        ToggleNodeEditorCommand.NotifyCanExecuteChanged();
        if (NodeForm is { } form) form.IsReadOnly = !CanEditNode || jsonFragmentEdited;
        if (PaletteForm is { } palette) palette.IsReadOnly = !CanAppendNode();
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
        try
        {
            NodeFragment = SelectedNode is null || validated is null ? "" : workspace.ReadFragment(validated.Plan, SelectedNode.Id);
            jsonFragmentEdited = false; NodeForm = null; ShowAdvancedNode = false;
            if (SelectedNode is not null && validated is not null)
            {
                PlanStepFormViewModel? form = null;
                form = new(workspace.ReadStep(validated.Plan, SelectedNode.Id), workspace.Devices, () => { if (ReferenceEquals(NodeForm, form)) FormChanged(); });
                NodeForm = form;
            }
            NodeFragmentDirty = false;
        }
        finally { loadingNode = false; }
    }
    [RelayCommand(CanExecute = nameof(CanApplyNode))]
    private void ApplyNode()
    {
        if (!CanApplyNode()) return;
        ApplyEditorChange(() =>
        {
            if (NodeForm?.IsDirty == true && jsonFragmentEdited) throw new InvalidOperationException("表单与高级 JSON 均有修改，请撤销后选择一种编辑方式。");
            return NodeForm?.IsDirty == true
                ? workspace.EditStep(validated!.Plan, PlanStructureEditKind.Replace, SelectedNode!.Id, NodeForm.Build())
                : workspace.EditStructure(validated!.Plan, new(PlanStructureEditKind.Replace, SelectedNode!.Id, NodeFragment));
        }, SelectedNode!.Id);
    }
    [RelayCommand(CanExecute = nameof(CanDiscardNode))]
    private void DiscardNode() { if (CanDiscardNode()) { LoadSelectedFragment(); HasError = false; Message = "节点修改已撤销。"; RefreshCommands(); changed(); } }
    [RelayCommand(CanExecute = nameof(CanAppendNode))]
    private void AppendNode()
    {
        if (!CanAppendNode()) return;
        var parent = SelectedNode!.Id;
        if (ApplyEditorChange(() =>
        {
            var step = PaletteForm is { } form ? form.Build() : CreatePaletteStep(PaletteKind);
            return workspace.EditStep(validated!.Plan, PlanStructureEditKind.Append, parent, step);
        }, parent)) ResetPaletteForm();
    }
    private void ResetPaletteForm()
    {
        PaletteForm = null;
        if (PaletteKind is not ("读取仪器" or "设置参数" or "参数扫描" or "源输出")) return;
        PlanStepFormViewModel? form = null;
        form = new(CreatePaletteStep(PaletteKind), workspace.Devices, () => { if (ReferenceEquals(PaletteForm, form)) RefreshGraphCommands(); }, template: true);
        PaletteForm = form;
    }
    private static ExperimentStep CreatePaletteStep(string kind)
    {
        var id = new StepId("step-" + Guid.NewGuid().ToString("N"));
        var address = new ParameterAddress(new("unselected"), new("unselected"));
        return kind switch
        {
            "delay" or "等待" => new DelayStep(id, TimeSpan.FromSeconds(1)),
            "sequence" or "顺序" => new SequenceStep(id, []),
            "repeat" or "重复" => new RepeatStep(id, 2, new SequenceStep(new(id.Value + "-body"), [])),
            "timeout" or "超时保护" => new TimeoutStep(id, TimeSpan.FromSeconds(30), new SequenceStep(new(id.Value + "-body"), [])),
            "retry" or "重试" => new RetryStep(id, 2, TimeSpan.FromSeconds(.5), new SequenceStep(new(id.Value + "-body"), [])),
            "读取仪器" => new ReadStep(id, [new(new("reading"), address)]),
            "设置参数" => new SetParameterStep(id, address, new ConstantValue(new(0, PhysicalUnit.Dimensionless))),
            "参数扫描" => new ScanStep(id, address, new("axis"), new([new(0, PhysicalUnit.Dimensionless)]), TimeSpan.Zero, new SequenceStep(new(id.Value + "-body"), [])),
            "源输出" => new SourceOutputStep(id, address.Device, false),
            _ => throw new NotSupportedException("选择可用的步骤类型。"),
        };
    }
    [RelayCommand(CanExecute = nameof(CanRemoveNode))]
    private void RemoveNode() { if (CanRemoveNode()) EditNode(new(PlanStructureEditKind.Remove, SelectedNode!.Id)); }
    [RelayCommand(CanExecute = nameof(CanMoveEarlier))]
    private void MoveEarlier() { if (CanMoveEarlier()) EditNode(new(PlanStructureEditKind.MoveEarlier, SelectedNode!.Id)); }
    [RelayCommand(CanExecute = nameof(CanMoveLater))]
    private void MoveLater() { if (CanMoveLater()) EditNode(new(PlanStructureEditKind.MoveLater, SelectedNode!.Id)); }
    private void EditNode(PlanStructureEdit edit)
        => ApplyEditorChange(() => workspace.EditStructure(validated!.Plan, edit), edit.Selected);
    private bool ApplyEditorChange(Func<ValidatedPlanDocument> edit, StepId selected)
    {
        if (!CanEditNode) return false;
        var success = false;
        try
        {
            var result = edit(); Apply(result);
            SelectedNode = GraphNodes.FirstOrDefault(n => n.Id == selected) ?? GraphNodes.FirstOrDefault();
            IsDirty = true; Message = "流程修改已通过完整计划验证，可运行或保存。";
            success = true;
        }
        catch (Exception error) { HasError = true; Message = $"流程修改未应用，原计划保留：{error.Message}"; }
        RefreshCommands(); changed();
        return success;
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
