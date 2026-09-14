using BoardTrace.Station.Core;

namespace BoardTrace.Station;

public sealed partial class StationViewModel
{
    private string selectedInputSource = "数据集回放";
    private string cameraDeviceIndex = "0";

    public IReadOnlyList<string> InputSources { get; } = ["数据集回放", "相机采集"];

    public string SelectedInputSource
    {
        get => selectedInputSource;
        set
        {
            if (!SetProperty(ref selectedInputSource, value)) return;
            if (value == "相机采集") ConstructedNormal = false;
            OnPropertyChanged(nameof(IsCameraSelected));
            OnPropertyChanged(nameof(CanEditCameraIndex));
            OnPropertyChanged(nameof(CanConstructNormal));
            OnPropertyChanged(nameof(CameraInputNotice));
            RunCommand.NotifyCanExecuteChanged();
            StartPlcCommand.NotifyCanExecuteChanged();
        }
    }

    public string CameraDeviceIndex
    {
        get => cameraDeviceIndex;
        set
        {
            if (!SetProperty(ref cameraDeviceIndex, value)) return;
            OnPropertyChanged(nameof(CameraInputNotice));
            RunCommand.NotifyCanExecuteChanged();
            StartPlcCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsCameraSelected => SelectedInputSource == "相机采集";
    public bool CanConstructNormal => !IsCameraSelected && CanEditCaptureSource;
    public bool CanEditCaptureSource => CanEdit && !plcRunning;
    public bool CanEditCameraIndex => IsCameraSelected && CanEditCaptureSource;
    public string CameraInputNotice => !IsCameraSelected ? "相机模式须显式选择；PLC 与人工使用同一输入来源。"
        : loadedRecipe is null ? "相机模式须先加载包含当前样本参考图的已发布方案。"
        : !int.TryParse(CameraDeviceIndex, out var index) || index < 0 ? "相机设备序号必须是从 0 开始的整数。"
        : "相机保留实际帧尺寸；无法打开、空帧或尺寸不符将保存未判定的失败档案。";
    private bool CanUseInputSource => !IsCameraSelected || loadedRecipe is not null &&
        int.TryParse(CameraDeviceIndex, out var index) && index >= 0;

    private IImageSource CreateSelectedSource(ReplaySample sample, bool constructed) => IsCameraSelected
        ? new CameraImageSource(sample.SampleId, int.Parse(CameraDeviceIndex))
        : CreateReplaySource(sample, constructed);
}
