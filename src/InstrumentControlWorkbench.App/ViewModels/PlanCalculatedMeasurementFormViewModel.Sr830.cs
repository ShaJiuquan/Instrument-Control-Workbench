using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstrumentControlWorkbench.Core.Experiments;
using InstrumentControlWorkbench.Core.Parameters;

namespace InstrumentControlWorkbench.App.ViewModels;

public partial class PlanCalculatedMeasurementFormViewModel
{
    public bool HasSr830Body => BodyForm.HasSr830Frame;
    [ObservableProperty] private string nominalResultPrefix = "";
    [ObservableProperty] private string nominalGenerationMessage = "补全 SR830 测量和换算前缀后生成。此操作替换当前计算列；修改模式或原始前缀后请重新生成。";
    partial void OnNominalResultPrefixChanged(string value) => GenerateSr830NominalCommand.NotifyCanExecuteChanged();
    private void Sr830BodyChanged()
    {
        if (HasSr830Body && !loading && !refreshing) NominalGenerationMessage = "测量配置已变化；请重新生成名义换算或核对当前公式。";
        DraftChanged();
    }
    private bool CanGenerateSr830Nominal()
    {
        if (IsReadOnly || !HasSr830Body) return false;
        try
        {
            return BodyForm.Build() is ReadSr830FrameStep frame && Sr830InputReferredAnalysis.Create(original.Id, frame, new ParameterId(NominalResultPrefix)).Columns.Count > 0;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or NotSupportedException) { return false; }
    }
    [RelayCommand(CanExecute = nameof(CanGenerateSr830Nominal))]
    private void GenerateSr830Nominal()
    {
        if (!CanGenerateSr830Nominal()) return;
        var frame = (ReadSr830FrameStep)BodyForm.Build();
        var generated = Sr830InputReferredAnalysis.Create(original.Id, frame, new(NominalResultPrefix));
        refreshing = true;
        try
        {
            columns.Clear(); foreach (var column in generated.Columns) AddForm(column, false);
        }
        finally { refreshing = false; }
        NominalGenerationMessage = "已按当前输入模式生成名义换算；原始帧会保留。应用完整计划后生效。";
        RefreshCommands(); DraftChanged();
    }
}
