using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Application.Experiments;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Instruments;
using InstrumentControlWorkbench.Core.Measurements;
using InstrumentControlWorkbench.Core.Parameters;
using InstrumentControlWorkbench.Core.Routing;

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
    public string[] PaletteKinds { get; } = ["等待", "顺序", "重复", "超时保护", "重试", "读取仪器", "设置参数", "参数扫描", "源输出", "源与测量读数", "源表电阻", "独立源电阻", "变量赋值", "条件检查", "条件分支", "条件循环", "变量运算", "结果回调", "数值迭代", "子流程", "Hall 分析", "范德堡分析", "独立源 Hall 分析", "独立源范德堡分析", "样品电阻", "独立源样品电阻", "读取后计算"];
    public bool SupportsNodeForm => NodeForm?.IsSupported == true;
    public bool HasPaletteForm => PaletteForm is not null;
    public bool IsNodeFormVisible => SupportsNodeForm && !ShowAdvancedNode;
    public bool IsNodeJsonVisible => !SupportsNodeForm || ShowAdvancedNode;
    public string NodeEditorToggleText => ShowAdvancedNode ? "参数表单" : "高级 JSON";
    public bool SupportsGraph => workspace.SupportsStructureEditing;
    public double GraphWidth => GraphNodes.Count == 0 ? 340 : GraphNodes.Max(n => n.X) + 240;
    public double GraphHeight => Math.Max(220, GraphNodes.Count * 86 + 24);
    public string SelectedNodeTitle => SelectedNode is null ? "选择流程节点" : $"{SelectedNode.Title} · {SelectedNode.Id.Value}";
    public bool CanEditNode => CanEdit && validated is not null && SelectedNode is not null && !PreparationDirty && !SampleDirty;
    public bool IsNodeReadOnly => !CanEditNode || NodeForm?.IsDirty == true;
    private bool CanApplyNode() => CanEditNode && NodeFragmentDirty;
    private bool CanDiscardNode() => CanEdit && validated is not null && NodeFragmentDirty;
    private bool CanAppendNode() => CanEditNode && !NodeFragmentDirty && SelectedNode!.Definition.CanAppend;
    private bool CanRemoveNode() => CanEditNode && !NodeFragmentDirty && SelectedNode!.Definition.CanRemove;
    private bool CanMoveEarlier() => CanEditNode && !NodeFragmentDirty && SelectedNode!.Definition.CanMoveEarlier;
    private bool CanMoveLater() => CanEditNode && !NodeFragmentDirty && SelectedNode!.Definition.CanMoveLater;
    partial void OnSelectedNodeChanged(PlanGraphNodeViewModel? value)
    {
        foreach (var node in GraphNodes) node.IsSelected = node == value;
        LoadSelectedFragment(); RefreshPaletteVariables(); OnPropertyChanged(nameof(SelectedNodeTitle)); RefreshGraphCommands();
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
                () => !disposed && !IsDocumentBusy && !HasUnappliedEdits));
        }
        SelectedNode = GraphNodes.FirstOrDefault(n => n.Id == old) ?? GraphNodes.FirstOrDefault();
        LoadSelectedFragment(); RefreshPaletteVariables(); OnPropertyChanged(nameof(GraphWidth)); OnPropertyChanged(nameof(GraphHeight));
    }
    private void SelectGraphNode(PlanGraphNodeViewModel node)
    {
        if (HasUnappliedEdits && node != SelectedNode || disposed || IsDocumentBusy || !GraphNodes.Contains(node)) return;
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
                form = new(workspace.ReadStep(validated.Plan, SelectedNode.Id), workspace.Devices, () => { if (ReferenceEquals(NodeForm, form)) FormChanged(); }, axes: workspace.AxisDefinitions, routing: validated.Plan.Routing);
                form.SetAvailableVariables(workspace.DescribeVariables(validated.Plan, SelectedNode.Id));
                var hostPlan = validated.Plan; var target = SelectedNode.Id;
                form.CalculationForm?.SetDraftInspector(draft => workspace.DescribeCalculationDraft(hostPlan, target, PlanStructureEditKind.Replace, draft));
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
        if (PaletteKind is not ("读取仪器" or "设置参数" or "参数扫描" or "源输出" or "源与测量读数" or "源表电阻" or "独立源电阻" or "变量赋值" or "条件检查" or "条件分支" or "条件循环" or "变量运算" or "结果回调" or "数值迭代" or "子流程" or "Hall 分析" or "范德堡分析" or "独立源 Hall 分析" or "独立源范德堡分析" or "样品电阻" or "独立源样品电阻" or "读取后计算")) return;
        PlanStepFormViewModel? form = null;
        form = new(CreatePaletteStep(PaletteKind), workspace.Devices, () => { if (ReferenceEquals(PaletteForm, form)) RefreshGraphCommands(); }, template: true, axes: workspace.AxisDefinitions, routing: validated?.Plan.Routing);
        PaletteForm = form; RefreshPaletteVariables();
    }
    private void RefreshPaletteVariables()
    {
        var hostPlan = validated?.Plan; var selected = SelectedNode is { Definition.CanAppend: true } node ? node : null;
        PaletteForm?.SetAvailableVariables(hostPlan is not null && selected is not null
            ? workspace.DescribeVariables(hostPlan, selected.Id, PlanVariablePosition.AppendChild) : null);
        PaletteForm?.CalculationForm?.SetDraftInspector(hostPlan is not null && selected is not null
            ? draft => workspace.DescribeCalculationDraft(hostPlan, selected.Id, PlanStructureEditKind.Append, draft) : null);
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
            "源与测量读数" => new ReadSourceSenseStep(id, new("unselected-source"), SourceFunction.Voltage, new("voltage_read"), new("current_read")),
            "源表电阻" => new ReadResistanceStep(id, new("unselected-source"), SourceFunction.Voltage, new("voltage_read"), new("current_read"), new("resistance"), new(0, PhysicalUnit.Ampere)),
            "独立源电阻" => new ReadCurrentResistanceStep(id, new("unselected-source"), new("unselected-meter"), CurrentReference.ProgrammedSetpoint, new("voltage_read"), new("current_read"), new("resistance"), new(0, PhysicalUnit.Ampere)),
            "变量赋值" => new AssignValueStep(id, new("requested"), new ConstantValue(new(0, PhysicalUnit.Dimensionless))),
            "条件检查" => new ConditionStep(id, new(new("unselected_variable"), ComparisonOperator.Equal, new ConstantValue(new(0, PhysicalUnit.Dimensionless)))),
            "条件分支" => new IfElseStep(id, new(new("unselected_variable"), ComparisonOperator.Equal, new ConstantValue(new(0, PhysicalUnit.Dimensionless))), new SequenceStep(new(id.Value + "-then"), [])),
            "条件循环" => new WhileStep(id, new(new("unselected_variable"), ComparisonOperator.Equal, new ConstantValue(new(0, PhysicalUnit.Dimensionless))), new SequenceStep(new(id.Value + "-body"), [])),
            "变量运算" => new MutateValueStep(id, new("requested"), VariableMutation.Set, new(0, PhysicalUnit.Dimensionless)),
            "结果回调" => new OutcomeMutationStep(id, new SequenceStep(new(id.Value + "-body"), [])),
            "数值迭代" => new ForEachStep(id, new("requested"), new([new(0, PhysicalUnit.Dimensionless)]), new SequenceStep(new(id.Value + "-body"), [])),
            "子流程" => new SubtreeStep(id, []),
            "读取后计算" => new CalculatedMeasurementStep(id, new ReadSourceSenseStep(new(id.Value + "-read"), new("unselected-source"), SourceFunction.Voltage, new("V"), new("I")), [new(new("P"), new ArithmeticValue(ArithmeticOperator.Multiply, new VariableValue(new("V")), new VariableValue(new("I"))))]),
            "样品电阻" => new ReadSampleResistanceStep(new ReadResistanceStep(id, new("unselected-source"), SourceFunction.Voltage, new("voltage"), new("current"), new("resistance"), new(0, PhysicalUnit.Ampere)), new(new("unselected-sample"), new("unselected-wiring"), SampleRouteSelection.FourWireResistance), TimeSpan.Zero),
            "独立源样品电阻" => new ReadSampleResistanceStep(new ReadCurrentResistanceStep(id, new("unselected-source"), new("unselected-meter"), CurrentReference.ProgrammedSetpoint, new("voltage"), new("current"), new("resistance"), new(0, PhysicalUnit.Ampere)), new(new("unselected-sample"), new("unselected-wiring"), SampleRouteSelection.FourWireResistance), TimeSpan.Zero),
            "Hall 分析" or "范德堡分析" or "独立源 Hall 分析" or "独立源范德堡分析" => CreateAnalysisTemplate(id, kind),
            _ => throw new NotSupportedException("选择可用的步骤类型。"),
        };
    }
    private static SampleAnalysisStep CreateAnalysisTemplate(StepId id, string kind)
    {
        var topology = kind.Contains("范德堡", StringComparison.Ordinal) ? SampleTopology.VanDerPauw : SampleTopology.HallBar;
        var independent = kind.StartsWith("独立源", StringComparison.Ordinal);
        var readings = SampleResistanceAnalysis.Selections(topology).Select((selection, index) =>
        {
            var child = new StepId(id.Value + "-read-" + (index + 1));
            ResistanceAcquisitionStep acquisition = independent
                ? new ReadCurrentResistanceStep(child, new("unselected-source"), new("unselected-meter"), CurrentReference.ProgrammedSetpoint, new(id.Value + ".voltage"), new(id.Value + ".current"), new(id.Value + ".resistance"), new(0, PhysicalUnit.Ampere))
                : new ReadResistanceStep(child, new("unselected-source"), SourceFunction.Voltage, new(id.Value + ".voltage"), new(id.Value + ".current"), new(id.Value + ".resistance"), new(0, PhysicalUnit.Ampere));
            return new ReadSampleResistanceStep(acquisition, new(new("unselected-sample"), new("unselected-wiring"), selection), TimeSpan.Zero);
        });
        return new(id, readings, SampleResistanceAnalysis.Kinds(topology).Select(quantity => new SampleDerivedColumn(quantity, new(quantity.ToString()))));
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
