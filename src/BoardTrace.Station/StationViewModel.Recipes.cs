using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using CommunityToolkit.Mvvm.Input;

namespace BoardTrace.Station;

public sealed record RecipeChoice(PublishedRecipeSummary Summary, bool Cached)
{
    public string Label => $"{Summary.Name} · {Summary.Algorithm} · {Summary.PublishedAt.LocalDateTime:MM-dd HH:mm}{(Cached ? " · 已缓存" : "")}";
}

public sealed partial class StationViewModel
{
    private readonly List<ReplaySample> replaySamples = [];
    private LocalRecipeStore recipeStore = null!;
    // Borrowed from coordinator after a successful handover; never disposed by the view model.
    private LoadedRecipe? loadedRecipe;
    private RecipeChoice? selectedRecipe;
    private IReadOnlyList<PublishedRecipeSummary> centralRecipes = [];
    private string recipeNotice = "刷新可查看中央已发布版本。";

    public ObservableCollection<RecipeChoice> PublishedRecipes { get; } = [];
    public AsyncRelayCommand RefreshRecipesCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadRecipeCommand { get; private set; } = null!;
    public AsyncRelayCommand LoadCachedRecipeCommand { get; private set; } = null!;
    public RelayCommand UseDevelopmentRecipeCommand { get; private set; } = null!;
    public string ActiveRecipeName => loadedRecipe is { } recipe ? $"{recipe.Name} · {recipe.Algorithm}" : "经典图像差分 · 开发参数";
    public string ActiveRecipeIdentity => loadedRecipe?.VersionId.ToString() ?? "640 × 640 · 参考图配准";
    public string RecipeNotice { get => recipeNotice; private set => SetProperty(ref recipeNotice, value); }
    public RecipeChoice? SelectedRecipe
    {
        get => selectedRecipe;
        set { if (SetProperty(ref selectedRecipe, value)) NotifyRecipeCommands(); }
    }

    private bool IsRecipeBusy => RefreshRecipesCommand.IsRunning || DownloadRecipeCommand.IsRunning || LoadCachedRecipeCommand.IsRunning;

    private void InitializeRecipeCommands()
    {
        recipeStore = new LocalRecipeStore(options.DatabasePath);
        RefreshRecipesCommand = new AsyncRelayCommand(RefreshRecipesAsync, () => CanEdit);
        DownloadRecipeCommand = new AsyncRelayCommand(DownloadRecipeAsync, () => CanChangeRecipe && SelectedRecipe != null && !coordinator.IsFaulted);
        LoadCachedRecipeCommand = new AsyncRelayCommand(LoadCachedRecipeAsync, () => CanChangeRecipe && SelectedRecipe?.Cached == true && !coordinator.IsFaulted);
        UseDevelopmentRecipeCommand = new RelayCommand(UseDevelopmentRecipe, () => CanChangeRecipe && loadedRecipe != null && !coordinator.IsFaulted);
        foreach (var command in new[] { RefreshRecipesCommand, DownloadRecipeCommand, LoadCachedRecipeCommand })
            command.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(AsyncRelayCommand.IsRunning)) return;
                NotifyAccessChanged();
            };
    }

    private void NotifyRecipeCommands()
    {
        RefreshRecipesCommand.NotifyCanExecuteChanged();
        DownloadRecipeCommand.NotifyCanExecuteChanged();
        LoadCachedRecipeCommand.NotifyCanExecuteChanged();
        UseDevelopmentRecipeCommand.NotifyCanExecuteChanged();
    }

    private async Task InitializeRecipesAsync()
    {
        await Task.Run(recipeStore.Initialize);
        await UpdateRecipeChoicesAsync();
    }

    private async Task UpdateRecipeChoicesAsync()
    {
        var cached = await Task.Run(recipeStore.List);
        var cachedIds = cached.Select(version => version.Id).ToHashSet();
        var selectedId = SelectedRecipe?.Summary.Id ?? loadedRecipe?.VersionId;
        var choices = centralRecipes.Concat(cached).DistinctBy(version => version.Id)
            .OrderByDescending(version => version.PublishedAt)
            .Select(version => new RecipeChoice(version, cachedIds.Contains(version.Id))).ToArray();
        PublishedRecipes.Clear();
        foreach (var choice in choices) PublishedRecipes.Add(choice);
        SelectedRecipe = PublishedRecipes.FirstOrDefault(choice => choice.Summary.Id == selectedId) ?? PublishedRecipes.FirstOrDefault();
    }

    private async Task RefreshRecipesAsync()
    {
        RecipeNotice = "正在读取中央版本与本地缓存…";
        try
        {
            centralRecipes = await operatorClient!.GetFromJsonAsync<PublishedRecipeSummary[]>("api/recipes/versions", uploadCancellation.Token)
                ?? throw new InvalidDataException("中央未返回版本列表。");
            await UpdateRecipeChoicesAsync();
            RecipeNotice = centralRecipes.Count == 0 ? "中央尚无已发布方案；当前可继续工程回放。" : $"中央有 {centralRecipes.Count} 个已发布版本。选择后下载并使用。";
        }
        catch (Exception error) { RecipeOperationFailed("刷新版本失败", error); }
    }

    private async Task DownloadRecipeAsync()
    {
        var selected = SelectedRecipe!;
        RecipeNotice = "正在下载并校验完整方案资产…";
        try
        {
            var recipe = await new PublishedRecipeDownloader(recipeStore, operatorClient!).DownloadAsync(selected.Summary.Id, uploadCancellation.Token);
            ApplyRecipe(recipe);
            await UpdateRecipeChoicesAsync();
        }
        catch (Exception error) { RecipeOperationFailed("下载或加载失败", error); }
    }

    private async Task LoadCachedRecipeAsync()
    {
        var versionId = SelectedRecipe!.Summary.Id;
        RecipeNotice = "正在校验本地完整方案资产…";
        try { ApplyRecipe(await Task.Run(() => recipeStore.Load(versionId), uploadCancellation.Token)); }
        catch (Exception error) { RecipeOperationFailed("加载本地版本失败", error); }
    }

    private void ApplyRecipe(LoadedRecipe recipe)
    {
        ReplaySample[] samples;
        try
        {
            var allowedSamples = recipe.SampleIds.ToHashSet(StringComparer.Ordinal);
            samples = replaySamples.Where(sample => allowedSamples.Contains(sample.SampleId)).ToArray();
            if (samples.Length == 0) throw new InvalidDataException("该版本与当前回放清单没有共同样本。");
            coordinator.UsePublishedRecipe(recipe);
        }
        catch { if (!ReferenceEquals(recipe, loadedRecipe)) recipe.Dispose(); throw; }
        loadedRecipe = recipe;
        OnPropertyChanged(nameof(CameraInputNotice));
        RunCommand.NotifyCanExecuteChanged();
        activeBatch = null;
        passedFirstArticle = null;
        UpdateBatchDisplay();
        SelectRecipeSamples(samples);
        RecipeNotice = "完整版本已加载。工程回放使用缓存参考图，检测档案绑定此版本。";
        AddEvent($"已加载方案 {recipe.Name} · {recipe.VersionId}。");
    }

    private void UseDevelopmentRecipe()
    {
        try
        {
            coordinator.UseDevelopmentRecipe(new ClassicalSettings());
            activeBatch = null;
            passedFirstArticle = null;
            loadedRecipe = null;
            OnPropertyChanged(nameof(CameraInputNotice));
            RunCommand.NotifyCanExecuteChanged();
            UpdateBatchDisplay();
            SelectRecipeSamples(replaySamples);
            RecipeNotice = "已明确切换至开发参数，工程回放不计入生产批次。";
            AddEvent("已切换至经典开发参数。");
        }
        catch (Exception error) { RecipeOperationFailed("切换工程回放失败", error); }
    }

    private void SelectRecipeSamples(IEnumerable<ReplaySample> samples)
    {
        var sampleId = SelectedSample?.SampleId;
        Samples.Clear();
        foreach (var sample in samples) Samples.Add(sample);
        SelectedSample = Samples.FirstOrDefault(sample => sample.SampleId == sampleId) ?? Samples.FirstOrDefault();
        OnPropertyChanged(nameof(ActiveRecipeName));
        OnPropertyChanged(nameof(ActiveRecipeIdentity));
        NotifyRecipeCommands();
    }

    private IImageSource CreateReplaySource(ReplaySample sample, bool constructed) => loadedRecipe is null
        ? new ReplayImageSource(options.DataRoot, sample, constructed)
        : new PublishedReplayImageSource(options.DataRoot, sample, constructed ? loadedRecipe.GetReferenceBytes(sample.SampleId) : null);

    private void RecipeOperationFailed(string action, Exception error)
    {
        if (stopping && error is OperationCanceledException) return;
        if (error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
        {
            sessionExpired = true;
            ClearLocalOperatorSession();
            NotifyAccessChanged();
            RecipeNotice = "人员登录已失效，请退出 / 换班后重新登录。";
        }
        else RecipeNotice = $"{action}：{error.Message} 当前使用：{ActiveRecipeName}。";
        AddEvent(RecipeNotice);
    }
}
