using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using MuseRAM.Core;
using Button = System.Windows.Controls.Button;
using ColorConverter = System.Windows.Media.ColorConverter;
using Forms = System.Windows.Forms;
using IOPath = System.IO.Path;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace MuseRAM.App;

public partial class MainWindow : Window
{
    private static readonly string CurrentAppVersion =
        typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
    private static readonly string CurrentBuildId =
        typeof(MainWindow).Assembly.ManifestModule.ModuleVersionId.ToString("N");
    private readonly AppState _state = new();
    private readonly MemoryStatusService _memoryStatus = new();
    private readonly ProcessSampler _processSampler = new();
    private readonly OptimizationPlanner _planner = new();
    private readonly OptimizationReboundTracker _reboundTracker = new();
    private readonly ApplicationReboundDetailTracker _applicationReboundDetailTracker = new();
    private readonly ApplicationReboundBackoffTracker _applicationBackoffTracker;
    private readonly ApplicationOptimizationRuleRuntime _applicationRuleRuntime = new();
    private readonly Dictionary<ApplicationRuleOutcomeAttributionKey, HashSet<ApplicationRuleTargetReference>>
        _applicationRuleOutcomeAttributions = new();
    private readonly WorkingSetTrimmer _trimmer = new();
    private readonly BackgroundActivityTracker _activityTracker = new(resetIdleOnBackgroundActivity: false);
    private readonly BackgroundActivityTracker _strictActivityTracker = new();
    private readonly CandidateIdleTracker _candidateIdleTracker = new();
    private readonly AppOverheadSampler _overheadSampler = new();
    private readonly LocalSettingsStore _settingsStore = new();
    private readonly ActivityHistoryStore _historyStore = new();
    private readonly List<ActivityHistoryEntry> _activityHistory = new();
    private readonly BenefitLearningStore _benefitLearningStore = new();
    private readonly HashSet<string> _dismissedSuggestionIds;
    private readonly CalibrationMetricsStore _calibrationMetricsStore = new();
    private readonly RuntimeProgressStore _runtimeProgressStore = new();
    private readonly ReboundHistoryStore _reboundHistoryStore = new();
    private readonly ProcessIoCalibrationTracker _processIoCalibrationTracker = new();
    private readonly ProcessCpuCalibrationTracker _processCpuCalibrationTracker = new();
    private readonly ActivityThresholdShadowTracker _activityThresholdShadowTracker = new();
    private readonly DiagnosticLog _diagnosticLog;
    private readonly WindowsServiceManager _serviceManager = new();
    private readonly DispatcherTimer _monitorTimer = new() { Interval = MonitoringIntervalPolicy.IdleInterval };
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _uiResponsivenessTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly long _sessionStartedTimestamp = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _museRamStartedAt = DateTimeOffset.UtcNow;
    private TimeSpan _restoredSessionUptime;
    private IReadOnlyList<ApplicationOptimizationRuleTargetProgress> _pendingApplicationRuleProgress =
        Array.Empty<ApplicationOptimizationRuleTargetProgress>();
    private CancellationTokenSource? _responsivenessCancellation;
    private readonly object _calibrationWriteQueueGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<int, DateTimeOffset> _lastTrimTimes = new();
    private readonly Dictionary<int, long> _lastTrimProcessStartTimes = new();
    private readonly System.Drawing.Icon _applicationIcon;
    private readonly TrayMemoryIconController _trayMemoryIconController = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _trayMemoryIconFailureLogged;
    private System.Windows.Controls.ContextMenu _trayMenu = null!;
    private MenuItem _trayOpenMenuItem = null!;
    private MenuItem _trayOptimizeMenuItem = null!;
    private MenuItem _trayExitMenuItem = null!;
    private LocalSettings _settings;
    private IReadOnlyList<ProcessFamilySnapshot> _families = Array.Empty<ProcessFamilySnapshot>();
    private IReadOnlyList<ProtectionSuggestion> _currentProtectionSuggestions =
        Array.Empty<ProtectionSuggestion>();
    private IReadOnlyList<ProtectionSuggestion> _displayedProtectionSuggestions =
        Array.Empty<ProtectionSuggestion>();
    private UpdateAsset? _availableUpdate;
    private string? _updateShimmerDismissedVersion;
    private bool _updateCheckInProgress;
    private bool _automaticUpdatePromptShown;
    private System.Windows.Media.Animation.Storyboard? _protectionSuggestionShimmerStoryboard;
    private bool _protectionSuggestionShimmerActive;
    private IReadOnlyDictionary<string, BackgroundActivity> _activity =
        new Dictionary<string, BackgroundActivity>();
    private IReadOnlyDictionary<string, BackgroundActivity> _strictActivity =
        new Dictionary<string, BackgroundActivity>();
    private IReadOnlyDictionary<int, CandidateIdleReadiness> _candidateIdleReadiness =
        new Dictionary<int, CandidateIdleReadiness>();
    private IReadOnlyList<ActivityThresholdShadowState> _activityThresholdShadowStates =
        Array.Empty<ActivityThresholdShadowState>();
    private HashSet<string>? _lastPreviewCandidateFamilyKeys;
    private MemorySnapshot _currentMemory;
    private DateTimeOffset? _automaticOptimizationSafetyAnchor;
    private readonly Dictionary<OptimizationTriggerKind, (DateTimeOffset RecordedAt, string Signature)>
        _lastCandidateCalibrations = new();
    private DateTimeOffset? _lastMonitoringCalibrationAt;
    private DateTimeOffset? _lastLargeMemoryOpportunityAt;
    private DateTimeOffset? _lastLongIdleEvaluationAt;
    private DateTimeOffset? _lastRuntimeProgressSaveAt;
    private TimeSpan _lastProcessCaptureDuration;
    private ProcessCaptureDiagnostics _lastProcessCaptureDiagnostics;
    private DateTimeOffset _scheduledOptimizationAnchor = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastSuccessfulOptimizationAt = DateTimeOffset.UtcNow;
    private bool _syncingControls;
    private readonly HashSet<Popup> _openApplicationRulePopups = new();
    private readonly HashSet<Button> _suppressedPopupTriggerClicks = new();
    private bool _exitRequested;
    private bool _revealAfterRendering;
    private readonly bool _startHidden;
    private bool _compactMode;
    private string _currentPageName = "OverviewPage";
    private string _historyAnalysisTab = "Activity";
    private int _reboundHistoryLimit = 5;
    private int? _selectedReboundRunSequence;
    private bool _syncingReboundRunSelection;
    private UiLanguage _uiLanguage;
    private OptimizationResultDisplay? _lastOptimizationResult;
    private string _lastResultFallbackKey = "RuntimeNotRun";
    private ulong? _previousAvailablePhysicalBytes;
    private long _cumulativeTrimBytes;
    private long _cumulativeNetGainBytes;
    private DateTimeOffset? _lastMemoryMetricsAt;
    private string? _editingCustomProfileId;
    private string? _editingCustomStableSuppressionProfileId;
    private StableSuppressionCatalogItem? _selectedStableSuppressionCatalogItem;
    private bool _stableSuppressionDraftDirty;
    private bool _customProfileDraftDirty;
    private bool _loadingStableSuppressionEditor;
    private bool _loadingCustomProfileEditor;
    private BenefitLearningRow? _editingStableAnchorRow;
    private bool _loadingStableAnchorEditor;
    private bool _stableAnchorValueChanged;
    private SliderBounds? _currentCustomProfileBounds;
    private readonly string? _startupDataWarning;
    private readonly bool _settingsLoadedSafely;
    private bool _settingsWriteAvailable;
    private Task _calibrationWriteQueue = Task.CompletedTask;
    private Task? _initializationTask;
    private int _calibrationWriteGeneration;
    private int _diagnosticClearInProgress;
    private Task? _backgroundResponsivenessTask;
    private long _lastUiHeartbeatTimestamp;
    private DateTimeOffset _lastUiStallRecordedAt;
    private DateTimeOffset _lastBackgroundStallRecordedAt;
    private string? _activeOptimizationRunId;
    private List<RuntimeActivityProgress> _pendingRuntimeActivities = new();
    private List<RuntimeTrimProgress> _pendingRuntimeTrimHistory = new();
    private readonly List<ReboundHistoryRun> _reboundRunHistory = new();
    private readonly Dictionary<string, ApplicationOptimizationRuleExecutionState> _applicationRuleStates =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ApplicationOptimizationRuleTargetDecision> _pendingApplicationRuleDecisions =
        Array.Empty<ApplicationOptimizationRuleTargetDecision>();
    private OptimizationRunKind _pendingReboundRunKind;
    private int _nextReboundRunSequence;
    private int? _activeReboundRunSequence;
    private DateTimeOffset? _lastReboundHistorySaveAt;

    public MainWindow()
    {
        InitializeComponent();
        InputModality.Attach(this);
        DataContext = _state;
        var settingsLoad = _settingsStore.LoadWithStatus();
        var benefitLearningLoad = _benefitLearningStore.LoadWithStatus();
        _dismissedSuggestionIds = benefitLearningLoad.DismissedSuggestionIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _settings = settingsLoad.Settings;
        _diagnosticLog = new DiagnosticLog(
            isEnabled: () => _settings.DiagnosticDataCollectionEnabled);
        _settingsLoadedSafely = settingsLoad.ErrorMessage is null;
        _settingsWriteAvailable = _settingsLoadedSafely;
        _applicationBackoffTracker = new ApplicationReboundBackoffTracker(
            benefitLearningLoad.Records,
            DateTimeOffset.UtcNow,
            benefitLearningLoad.FamilyStableRecords);
        if (_settings.RuntimeProgressPersistenceEnabled)
        {
            var runtimeProgressLoad = _runtimeProgressStore.LoadWithStatus(DateTimeOffset.UtcNow);
            if (runtimeProgressLoad.Progress is { } progress)
                LoadRuntimeProgress(progress, DateTimeOffset.UtcNow);
            if (!string.IsNullOrWhiteSpace(runtimeProgressLoad.ErrorMessage))
                _diagnosticLog.Warning($"Runtime progress was ignored: {runtimeProgressLoad.ErrorMessage}");
        }
        var reboundHistoryLoad = _reboundHistoryStore.LoadWithStatus(DateTimeOffset.UtcNow);
        if (reboundHistoryLoad.History is { } reboundHistory)
            LoadReboundHistory(reboundHistory);
        if (!string.IsNullOrWhiteSpace(reboundHistoryLoad.ErrorMessage))
            _diagnosticLog.Warning($"Rebound history was ignored: {reboundHistoryLoad.ErrorMessage}");
        _lastRuntimeProgressSaveAt = DateTimeOffset.UtcNow;
        var commandLineArguments = Environment.GetCommandLineArgs();
        _startHidden = StartupLaunchPolicy.ShouldStartHidden(commandLineArguments);
        if (_startHidden)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
        }
        _uiLanguage = UiLanguageCatalog.FromCode(_settings.LanguageCode);
        ApplyLanguage(_uiLanguage);
        _startupDataWarning = settingsLoad.ErrorMessage is not null
            ? TF("SettingsLoadFailureFormat", settingsLoad.ErrorMessage)
            : benefitLearningLoad.ErrorMessage is not null
                ? TF("BenefitLearningLoadFailureFormat", benefitLearningLoad.ErrorMessage)
                : null;
        if (settingsLoad.Migrated) _diagnosticLog.Info("Migrated settings.json to schema version 1.");
        if (benefitLearningLoad.Migrated) _diagnosticLog.Info($"Migrated benefit-learning.json to schema version {BenefitLearningStore.CurrentSchemaVersion}.");
        _activityHistory.AddRange(_historyStore.Load());
        RefreshActivityHistory();
        ApplyTheme(IsLightThemeActive());
        _applicationIcon = LoadApplicationIcon();
        _trayIcon = CreateTrayIcon();
        UpdateTrayMemoryIcon();
        SystemEvents.UserPreferenceChanged += SystemEvents_OnUserPreferenceChanged;
        _monitorTimer.Tick += async (_, _) =>
        {
            if (!_state.IsBusy) await RefreshSnapshotAsync();
        };
        _memoryTimer.Tick += (_, _) => RefreshMemoryMetrics();
        _uiResponsivenessTimer.Tick += UiResponsivenessTimer_OnTick;
    }

    internal bool StartsHidden => _startHidden;

    internal Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e) =>
        await InitializeAsync();

    private async Task InitializeCoreAsync()
    {
        LoadSettingsIntoControls();
        if (_settingsLoadedSafely)
        {
            try
            {
                if (_settings.StartWithWindows)
                {
                    if (StartupRegistration.RepairEnabledPath())
                        _diagnosticLog.Info("Updated Windows startup registration to the current executable path.");
                    if (!StartupRegistration.IsEnabled())
                        throw new InvalidOperationException("Windows did not retain the MuseRAM logon task.");
                }
                else
                {
                    StartupRegistration.SetEnabled(false);
                    if (StartupRegistration.IsEnabled())
                        throw new InvalidOperationException("Windows did not remove the MuseRAM logon task.");
                }
            }
            catch (Exception exception)
            {
                _diagnosticLog.Warning("Unable to synchronize Windows startup registration.", exception);
                _state.Status = TF("StartupFailureFormat", exception.Message);
                _syncingControls = true;
                StartupCheckBox.IsThreeState = true;
                StartupCheckBox.IsChecked = null;
                _syncingControls = false;
            }
        }
        if (!string.IsNullOrWhiteSpace(_startupDataWarning))
        {
            _diagnosticLog.Warning(_startupDataWarning);
            ShowThemedMessage(
                T("LocalDataLoadWarningTitle"),
                _startupDataWarning,
                image: MessageBoxImage.Warning);
        }
        RefreshProtectedList();
        RefreshApplicationRuleList();
        SelectNavigation(OverviewPage, "概览");
        await RefreshSnapshotAsync();
        await Task.Delay(300);
        await RefreshSnapshotAsync();
        _monitorTimer.Start();
        _memoryTimer.Start();
        if (_settings.DiagnosticDataCollectionEnabled) StartResponsivenessMonitoring();
        _ = RunStartupUpdateMaintenanceAsync();
    }

    private void LoadSettingsIntoControls()
    {
        var wasSyncing = _syncingControls;
        _syncingControls = true;
        try
        {
            RefreshProfileSelectors();
            RefreshCustomProfileCatalog();
            RefreshStableSuppressionCatalog();
            ShowCustomConfigurationSection(showStableSuppression: false);
            AutoToggle.IsChecked = _settings.AutoOptimization;
            CompactAutoToggle.IsChecked = _settings.AutoOptimization;
            SynchronizeScheduledOptimizationControls();
            SynchronizeLongIdleOptimizationControls();
            SynchronizeCandidateModeControls();
            SynchronizeCandidateDisplayLimitControls();
            SynchronizeStableStateSuppressionControls();
            StartupCheckBox.IsChecked = _settings.StartWithWindows;
            StartupCheckBox.IsEnabled = _settingsLoadedSafely;
            EnhancedSafetyCheckBox.IsChecked = _settings.EnhancedSafety;
            IntelligentCandidateSelectionCheckBox.IsChecked = _settings.IntelligentCandidateSelection;
            DiagnosticDataCollectionCheckBox.IsChecked = _settings.DiagnosticDataCollectionEnabled;
            RuntimeProgressPersistenceCheckBox.IsChecked = _settings.RuntimeProgressPersistenceEnabled;
            OverviewBenefitLearningCheckBox.IsChecked = _settings.IntelligentCandidateSelection;
            UpdateBenefitLearningStatus();
            ShowBuiltInProfilesCheckBox.IsChecked = _settings.ShowBuiltInProfiles;
            ShowBuiltInProfilesCheckBox.IsEnabled = _settings.CustomProfiles.Count > 0;
            ShowBuiltInStableSuppressionProfilesCheckBox.IsChecked =
                _settings.ShowBuiltInStableStateSuppressionProfiles;
            ShowBuiltInStableSuppressionProfilesCheckBox.IsEnabled =
                _settings.CustomStableStateSuppressionProfiles.Count > 0;
            FollowSystemThemeCheckBox.IsChecked = _settings.FollowSystemTheme;
            TrayMemoryUsageIconCheckBox.IsChecked = _settings.ShowMemoryUsageInTrayIcon;
            SynchronizeUpdateFrequencyPresentation();
            ThemeButton.IsEnabled = !_settings.FollowSystemTheme;
            QuickThemeButton.IsEnabled = !_settings.FollowSystemTheme;
            CompactThemeButton.IsEnabled = !_settings.FollowSystemTheme;
            CloseBehaviorBox.SelectedItem = CloseBehaviorBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag as string, _settings.CloseButtonBehavior.ToString(), StringComparison.Ordinal));
            LanguageBox.ItemsSource = UiLanguageCatalog.Options;
            LanguageBox.SelectedItem = UiLanguageCatalog.Options.First(option => option.Language == _uiLanguage);
            _state.AutoStatus = _settings.AutoOptimization ? T("Enabled") : T("Disabled");
            ApplyTheme(IsLightThemeActive());
            UpdateMonitoringInterval();
        }
        finally
        {
            _syncingControls = wasSyncing;
        }
        EnsureStableSuppressionEditorSelection();
    }

    private async Task<bool> RefreshSnapshotAsync(bool waitForCurrentRefresh = false)
    {
        if (waitForCurrentRefresh)
        {
            await _refreshGate.WaitAsync();
        }
        else if (!await _refreshGate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            PruneLastTrimHistory(Array.Empty<ProcessSnapshot>(), DateTimeOffset.UtcNow);
            var lastTrimTimes = _lastTrimTimes.ToDictionary(pair => pair.Key, pair => pair.Value);
            var lastTrimProcessStartTimes = _lastTrimProcessStartTimes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value);
            var capture = await Task.Run(() =>
            {
                var started = Stopwatch.GetTimestamp();
                var snapshots = _processSampler.Capture(
                    lastTrimTimes,
                    lastTrimProcessStartTimes);
                return (
                    Snapshots: snapshots,
                    Elapsed: Stopwatch.GetElapsedTime(started),
                    Diagnostics: _processSampler.LastCaptureDiagnostics);
            });
            var processes = capture.Snapshots;
            _lastProcessCaptureDuration = capture.Elapsed;
            _lastProcessCaptureDiagnostics = capture.Diagnostics;
            PruneLastTrimHistory(processes, DateTimeOffset.UtcNow);
            _families = ApplicationFamilyGrouper.Group(processes);
            var applicationRules = ApplicationOptimizationRuleSettings.Resolve(_settings);
            SynchronizeApplicationRuleStates(applicationRules);
            if (_pendingApplicationRuleProgress.Count > 0)
            {
                _applicationRuleRuntime.RestoreProgress(
                    _pendingApplicationRuleProgress,
                    applicationRules,
                    _families,
                    DateTimeOffset.UtcNow);
                _pendingApplicationRuleProgress = Array.Empty<ApplicationOptimizationRuleTargetProgress>();
                RestoreApplicationRuleDisplayStates(applicationRules);
            }
            var ruleProfileThreshold = _settings.ResolveOptimizationSettings(manual: false)
                .MinimumFamilyWorkingSetBytes;
            foreach (var rule in applicationRules.Where(rule =>
                         rule.TriggerMode == ApplicationOptimizationRuleTriggerMode.Delayed))
                _applicationRuleRuntime.ObserveWorkingSet(rule, _families, ruleProfileThreshold);
            var candidateIdleSettings = _settings.ResolveOptimizationSettings(manual: false) with
            {
                EnhancedSafety = _settings.EnhancedSafety,
                IntelligentCandidateSelection = _settings.IntelligentCandidateSelection
            };
            _candidateIdleReadiness = _candidateIdleTracker.Observe(_families, candidateIdleSettings);
            RefreshProtectedList();
            RefreshApplicationRuleList();
            var learningRevision = _applicationBackoffTracker.LearningRevision;
            _applicationBackoffTracker.Observe(
                _families,
                DateTimeOffset.Now,
                _settings.IntelligentCandidateSelection);
            foreach (var outcome in _applicationBackoffTracker.DrainCompletedOutcomes())
            {
                var completedOutcome = outcome;
                ApplyCompletedApplicationRuleOutcome(completedOutcome);
                QueueCalibrationWrite(() => _calibrationMetricsStore.AppendApplicationOutcome(completedOutcome));
            }
            var reboundObservedAt = DateTimeOffset.Now;
            _applicationReboundDetailTracker.Observe(_families, reboundObservedAt);
            SynchronizeReboundRunHistory(reboundObservedAt);
            SaveReboundHistoryIfDue(reboundObservedAt);
            UpdateApplicationReboundSummary();
            var activityProtection = CurrentProtectionRules();
            var activityProtectionContext = activityProtection.CreateContext(
                _families.SelectMany(family => family.Processes));
            var observableFamilies = _families
                .Select(family => activityProtection.FilterUnprotectedProcesses(
                    family,
                    activityProtectionContext))
                .Where(family => family is not null)
                .Select(family => family!)
                .ToArray();
            var activityObservedAt = DateTimeOffset.UtcNow;
            RestorePendingRuntimeProgress(candidateIdleSettings, activityObservedAt);
            _activity = _activityTracker.Observe(
                observableFamilies,
                activityObservedAt,
                candidateIdleSettings.ActiveCpuThresholdPercent,
                candidateIdleSettings.ActiveIoThresholdBytesPerSecond);
            _strictActivity = _strictActivityTracker.Observe(
                observableFamilies,
                activityObservedAt);
            if (_settings.DiagnosticDataCollectionEnabled)
            {
                _activityThresholdShadowStates = _activityThresholdShadowTracker.Observe(
                    observableFamilies,
                    activityObservedAt,
                    candidateIdleSettings,
                    _settings.ActiveCustomProfile?.BaseProfile ?? _settings.Profile);
                RecordProcessActivitySamples(activityObservedAt);
            }
            if (!_memoryStatus.TryGetSnapshot(out _currentMemory))
            {
                _state.Status = T("StatusMemoryUnavailable");
                return false;
            }

            var longTermRetrySettings = _settings.ResolveOptimizationSettings(manual: false);
            var severeMemoryPressure = OptimizationPlanner.IsSevereMemoryPressureRegardlessOfOptimizationOverride(
                _currentMemory,
                longTermRetrySettings);
            var naturalStableSettings = _settings.ResolveStableStateSuppressionSettings();
            var stableObservationEnabled =
                _settings.IntelligentCandidateSelection && naturalStableSettings is not null;
            var longTermRetryGrowthSettings = naturalStableSettings ??
                StableStateSuppressionSettings.For(
                    _settings.ActiveCustomProfile?.BaseProfile ?? _settings.Profile);
            _applicationBackoffTracker.ObserveNaturalStableStates(
                stableObservationEnabled
                    ? StableStateSuppressionPolicy.NaturalStableStateSnapshots(
                        _families,
                        candidateIdleSettings,
                        CurrentProtectionRules(),
                        _candidateIdleReadiness,
                        _applicationBackoffTracker.FamilyStableLearningRecords,
                        _applicationBackoffTracker.NaturalStableScopeRequests(
                            activityObservedAt))
                    : Array.Empty<NaturalStableStateSnapshot>(),
                DateTimeOffset.Now,
                naturalStableSettings,
                severeMemoryPressure,
                stableObservationEnabled);
            foreach (var observation in _applicationBackoffTracker.DrainCompletedStableObservations())
            {
                var completedObservation = observation;
                QueueCalibrationWrite(() =>
                    _calibrationMetricsStore.AppendStableStateObservation(completedObservation));
            }
            if (_applicationBackoffTracker.LearningRevision != learningRevision)
                SaveBenefitLearning();
            UpdateBenefitLearningStatus();
            RefreshProtectedList();
            _applicationBackoffTracker.UpdateLongTermRetryPermissions(
                _families,
                severeMemoryPressure,
                longTermRetrySettings.MinimumFamilyWorkingSetBytes,
                longTermRetryGrowthSettings,
                DateTimeOffset.Now);
            UpdateMemoryMetricsIfDue(_currentMemory);
            UpdateVisibleProcessCollections();
            if (!_state.IsBusy)
            {
                var applicationRule = FindDueApplicationRule(applicationRules, DateTimeOffset.UtcNow);
                if (applicationRule is not null)
                {
                    await RunOptimizationAsync(
                        manual: false,
                        snapshotAlreadyRefreshed: true,
                        applicationRule: applicationRule);
                }
                var scheduledOptimizationDue = applicationRule is null &&
                                               !IsScheduledOptimizationUnavailable() &&
                                               _settings.ScheduledOptimizationEnabled &&
                                               CanRunUnattendedOptimization() &&
                                               ScheduledOptimizationPolicy.IsDue(
                                                   _scheduledOptimizationAnchor,
                                                   DateTimeOffset.UtcNow,
                                                   _settings.ScheduledOptimizationIntervalMinutes);
                if (scheduledOptimizationDue)
                {
                    _scheduledOptimizationAnchor = DateTimeOffset.UtcNow;
                    await RunOptimizationAsync(
                        manual: true,
                        scheduled: true,
                        snapshotAlreadyRefreshed: true);
                }
                else if (applicationRule is null && _settings.AutoOptimization && CanRunUnattendedOptimization())
                {
                    await RunOptimizationAsync(manual: false, snapshotAlreadyRefreshed: true);
                }
                var automaticSettings = _settings.ResolveOptimizationSettings(manual: false);
                var longIdleNow = DateTimeOffset.UtcNow;
                var longIdleDue = _settings.LongIdleOptimizationEnabled &&
                                  !OptimizationPlanner.HasMemoryPressure(_currentMemory, automaticSettings) &&
                                  CanRunUnattendedOptimization() &&
                                  LongIdleOptimizationPolicy.IsDue(
                                      _lastSuccessfulOptimizationAt,
                                      longIdleNow,
                                      _settings.LongIdleOptimizationMinutes) &&
                                  LongIdleOptimizationPolicy.CanEvaluate(
                                      _lastLongIdleEvaluationAt,
                                      longIdleNow);
                if (applicationRule is null && longIdleDue)
                {
                    _lastLongIdleEvaluationAt = longIdleNow;
                    await RunOptimizationAsync(
                        manual: false,
                        snapshotAlreadyRefreshed: true,
                        longIdle: true);
                }
            }
            SaveRuntimeProgressIfDue();
            return true;
        }
        catch (Exception exception)
        {
            _state.Status = TF("StatusRefreshSkippedFormat", exception.Message);
            _diagnosticLog.Warning("Monitoring refresh was skipped.", exception);
            return false;
        }
        finally
        {
            _refreshGate.Release();
            UpdateMonitoringInterval();
        }
    }

    private bool CanRunUnattendedOptimization()
    {
        var cooldown = _settings.ResolveOptimizationSettings(manual: false).AutoCooldown;
        return AutomaticOptimizationSafetyWindow.CanRun(
            _automaticOptimizationSafetyAnchor,
            DateTimeOffset.Now,
            cooldown);
    }

    private void UpdateMetrics(MemorySnapshot memory)
    {
        if (_previousAvailablePhysicalBytes.HasValue)
        {
            var change = checked((long)memory.AvailablePhysicalBytes - (long)_previousAvailablePhysicalBytes.Value);
            _state.MemoryChange = FormatMetricBytes(change);
        }
        _previousAvailablePhysicalBytes = memory.AvailablePhysicalBytes;
        _state.MemoryUsage = $"{memory.LoadPercent}%";
        _state.MemoryLoadPercent = memory.LoadPercent;
        _state.AvailableMemory = DisplayFormat.Bytes(memory.AvailablePhysicalBytes);
        _state.UsedMemory = DisplayFormat.Bytes(memory.UsedPhysicalBytes);
        _state.PhysicalMemorySummary = $"{DisplayFormat.Bytes(memory.UsedPhysicalBytes)} / {DisplayFormat.Bytes(memory.TotalPhysicalBytes)}";
        _state.CommitLoadPercent = memory.CommitLoadPercent;
        _state.CommittedMemorySummary = $"{DisplayFormat.Bytes(memory.CommittedBytes)} / {DisplayFormat.Bytes(memory.CommitLimitBytes)}";
        _state.LastUpdated = DateTime.Now.ToString("HH:mm:ss");
        UpdateSessionUptime();
        MemoryChart.AddSample(memory.LoadPercent);
        UpdateReboundRate(memory);
        UpdateLongIdleOptimizationStatus(DateTimeOffset.UtcNow);
    }

    private void UpdateSessionUptime()
    {
        var elapsed = CurrentSessionUptime();
        _state.SessionUptime = elapsed.TotalDays >= 1
            ? TF("SessionUptimeDaysFormat", (int)elapsed.TotalDays, elapsed.Hours, elapsed.Minutes)
            : elapsed.TotalHours >= 1
                ? TF("SessionUptimeHoursFormat", (int)elapsed.TotalHours, elapsed.Minutes)
                : TF("SessionUptimeMinutesFormat", (int)elapsed.TotalMinutes);
    }

    private TimeSpan CurrentSessionUptime()
    {
        var currentProcessUptime = Stopwatch.GetElapsedTime(_sessionStartedTimestamp);
        return _restoredSessionUptime > TimeSpan.MaxValue - currentProcessUptime
            ? TimeSpan.MaxValue
            : _restoredSessionUptime + currentProcessUptime;
    }

    private DateTimeOffset ApplicationRuleMuseRamStartedAt() =>
        _museRamStartedAt - _restoredSessionUptime;

    private void RefreshMemoryMetrics()
    {
        UpdateSessionUptime();
        if (!_memoryStatus.TryGetSnapshot(out var memory)) return;
        _currentMemory = memory;
        UpdateTrayMemoryIcon(memory);
        UpdateMemoryMetricsIfDue(memory);
    }

    private void UpdateMemoryMetricsIfDue(MemorySnapshot memory, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && _lastMemoryMetricsAt.HasValue && now - _lastMemoryMetricsAt.Value < TimeSpan.FromSeconds(2.5)) return;
        _lastMemoryMetricsAt = now;
        UpdateMetrics(memory);
        UpdateSelfOverhead();
    }

    private void UpdateProcessRows()
    {
        var selectedFamilyKey = (ProcessesGrid.SelectedItem as ProcessRow)?.Family.Key;
        var protection = CurrentProtectionRules();
        var protectionContext = protection.CreateContext(_families.SelectMany(family => family.Processes));
        var now = DateTimeOffset.Now;
        var settings = ResolvePreviewSettings();
        var evaluations = _currentMemory.TotalPhysicalBytes == 0
            ? new Dictionary<string, CandidateEvaluation>(StringComparer.OrdinalIgnoreCase)
            : CreatePreviewPlan(now, protection, settings).CandidateEvaluations.ToDictionary(
                evaluation => evaluation.FamilyKey,
                StringComparer.OrdinalIgnoreCase);
        SynchronizeCollection(
            _state.Processes,
            _families
                .OrderByDescending(family => ProtectionSortOrder(family, protection, protectionContext))
                .ThenByDescending(family => family.WorkingSetBytes)
                .Select(family => CreateProcessRow(
                    family,
                    protection,
                    protectionContext,
                    evaluations.GetValueOrDefault(family.Key),
                    settings,
                    now))
                .Take(250));
        if (selectedFamilyKey is not null)
        {
            ProcessesGrid.SelectedItem = _state.Processes.FirstOrDefault(row =>
                string.Equals(row.Family.Key, selectedFamilyKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void UpdatePreviewRows()
    {
        if (_currentMemory.TotalPhysicalBytes == 0) return;
        var now = DateTimeOffset.Now;
        var protection = CurrentProtectionRules();
        var protectionContext = protection.CreateContext(_families.SelectMany(family => family.Processes));
        var settings = ResolvePreviewSettings();
        var plan = CreatePreviewPlan(now, protection, settings);
        RecordCandidateTransitions(plan, settings, now);
        _state.CandidateSorting = T(_settings.IntelligentCandidateSelection
            ? "CandidateSortingBenefitAware"
            : "CandidateSortingComprehensive");
        IReadOnlyList<OptimizationCandidate> previewCandidates = plan.Candidates;
        var displayedFamilyKeys = previewCandidates
            .Select(candidate => candidate.Family.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var familyByKey = _families.ToDictionary(
            family => family.Key,
            StringComparer.OrdinalIgnoreCase);
        var pausedEvaluations = new Dictionary<string, CandidateEvaluation>(
            StringComparer.OrdinalIgnoreCase);
        var pausedCandidates = new List<OptimizationCandidate>();
        foreach (var evaluation in plan.CandidateEvaluations)
        {
            if (displayedFamilyKeys.Contains(evaluation.FamilyKey) ||
                !familyByKey.TryGetValue(evaluation.FamilyKey, out var family))
            {
                continue;
            }

            var unprotectedFamily = protection.FilterUnprotectedProcesses(family, protectionContext);
            var baseEligibleFamily = CandidatePreviewPolicy.CreateBaseEligibleFamily(
                unprotectedFamily,
                settings);
            var lifecycleVisibleFamily = CandidatePreviewPolicy.CreateLifecycleVisibleFamily(unprotectedFamily);
            if (!CandidatePreviewPolicy.IsTemporarilyBlocked(
                    evaluation,
                    baseEligibleFamily is not null,
                    lifecycleVisibleFamily is not null))
            {
                continue;
            }

            var pausedFamily = baseEligibleFamily ?? lifecycleVisibleFamily!;
            pausedEvaluations[family.Key] = evaluation;
            pausedCandidates.Add(new OptimizationCandidate(
                pausedFamily,
                pausedFamily.Processes,
                pausedFamily.IdleConfidenceScore,
                pausedFamily.WorkingSetBytes,
                string.Empty));
        }
        previewCandidates = previewCandidates.Concat(pausedCandidates).ToArray();
        previewCandidates = _settings.IntelligentCandidateSelection
            ? BenefitAwareRanking.OrderCandidates(
                previewCandidates,
                _applicationBackoffTracker.OutcomeMultipliers)
            : previewCandidates
                .OrderByDescending(candidate => candidate.PotentialReleaseBytes)
                .ThenBy(candidate => candidate.Family.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var displayLimit = CandidateDisplayLimitPolicy.Normalize(_settings.CandidateDisplayLimit);
        if (displayLimit > CandidateDisplayLimitPolicy.Unlimited)
            previewCandidates = previewCandidates.Take(displayLimit).ToArray();
        SynchronizeCollection(_state.Candidates, previewCandidates.Select(candidate =>
        {
            var row = CreateCandidateRow(candidate, protection, protectionContext, now);
            if (!pausedEvaluations.TryGetValue(candidate.Family.Key, out var evaluation)) return row;
            var pause = FormatCandidatePause(candidate.Family, evaluation, now);
            var displayWorkingSetBytes = DisplayProcessableWorkingSetBytes(candidate.Family);
            return row with
            {
                Memory = TF(
                    "CandidateMemoryFormat",
                    DisplayFormat.Bytes(displayWorkingSetBytes),
                    DisplayFormat.Bytes(candidate.Family.WorkingSetBytes)),
                MemoryDetail = TF(
                    evaluation.ExclusionReasons.Contains(CandidateExclusionReason.BelowFamilyWorkingSet)
                        ? "ProcessMemoryInsufficientDetailFormat"
                        : "ProcessMemoryDetailFormat",
                    DisplayFormat.Bytes(displayWorkingSetBytes),
                    DisplayFormat.BinaryThreshold(settings.MinimumFamilyWorkingSetBytes),
                    DisplayFormat.Bytes(candidate.Family.WorkingSetBytes)),
                MemoryBytes = displayWorkingSetBytes,
                IdleStatus = pause.Status,
                IdleStatusDetail = pause.Detail,
                RetentionIcon = pause.Icon
            };
        }));
        _state.Status = FormatPlan(plan);
    }

    private (string Status, string? Detail, RetentionStatusIcon Icon) FormatCandidatePause(
        ProcessFamilySnapshot family,
        CandidateEvaluation evaluation,
        DateTimeOffset now)
    {
        var originalFamily = _families.FirstOrDefault(candidateFamily =>
            string.Equals(candidateFamily.Key, family.Key, StringComparison.OrdinalIgnoreCase)) ?? family;
        var protection = CurrentProtectionRules();
        var protectionContext = protection.CreateContext(_families.SelectMany(candidateFamily =>
            candidateFamily.Processes));
        var unprotectedOriginal = protection.FilterUnprotectedProcesses(originalFamily, protectionContext);
        var componentKeys = ApplicationComponentIdentity.GroupProcesses(family).Keys;
        var statuses = _applicationBackoffTracker.NaturalStableObservationStatuses();
        var stableSettings = _settings.IntelligentCandidateSelection
            ? _settings.ResolveStableStateSuppressionSettings()
            : null;
        var activeStableRecord = stableSettings is null
            ? null
            : StableStateSuppressionPolicy.ActiveStableRecord(
                originalFamily,
                _families,
                _applicationBackoffTracker.FamilyStableLearningRecords,
                _settings.ResolveOptimizationSettings(manual: false),
                protection);
        var hasLongTermStableReference = HasActiveLongTermStableReference(
            unprotectedOriginal,
            activeStableRecord,
            stableSettings,
            now);
        var stableComponentKeys = activeStableRecord is { ComponentKeys.Count: > 0 }
            ? activeStableRecord.ComponentKeys
            : componentKeys;
        var reviewSchedule = stableSettings is null
            ? null
            : _applicationBackoffTracker.GetNaturalStableReviewSchedule(
                family.Key,
                stableComponentKeys,
                stableSettings,
                StableStateSuppressionPolicy.CurrentNaturalStableLaunchSignature(
                    originalFamily, stableComponentKeys));
        var pendingBenefitObservation = componentKeys.Any(
            _applicationBackoffTracker.PendingObservationComponentKeys(now).Contains);
        var indicator = ProcessRetentionPresentation.Resolve(
            isProtected: unprotectedOriginal is null,
            isPartiallyProtected: unprotectedOriginal is not null &&
                                  unprotectedOriginal.Processes.Count < originalFamily.Processes.Count,
            exclusionReasons: evaluation.ExclusionReasons,
            naturalStableObservation: !pendingBenefitObservation && stableComponentKeys.Any(
                _applicationBackoffTracker.NaturalStableObservationComponentKeys().Contains),
            hasLongTermStableReference: hasLongTermStableReference,
            isEligible: evaluation.IsEligible,
            hasProcessableTargets: evaluation.TargetProcessCount > 0,
            naturalStableReview: !pendingBenefitObservation && stableComponentKeys.Any(
                _applicationBackoffTracker.NaturalStableReviewComponentKeys().Contains),
            naturalStableGrowthReview: !pendingBenefitObservation && stableComponentKeys.Any(
                _applicationBackoffTracker.NaturalStableGrowthReviewComponentKeys().Contains),
            naturalStableProvisionalValidation: !pendingBenefitObservation && stableComponentKeys.Any(
                _applicationBackoffTracker.NaturalStableProvisionalValidationComponentKeys().Contains),
            reboundObservationPending: pendingBenefitObservation);
        var (status, detail) = FormatRetentionIndicator(
            indicator, family, stableComponentKeys, statuses, now, reviewSchedule);
        return (status, detail, RetentionIconFor(indicator));
    }

    private static bool HasActiveLongTermStableReference(
        ProcessFamilySnapshot? unprotectedFamily,
        ApplicationStableLearningRecord? record,
        StableStateSuppressionSettings? settings,
        DateTimeOffset now)
    {
        if (unprotectedFamily is null || record is null || settings is null ||
            record.ComponentKeys.Count == 0 ||
            StableStateSuppressionPolicy.SuppressionLimitBytes(record, settings, now) is not { } limit)
            return false;

        var components = ApplicationComponentIdentity.GroupProcesses(unprotectedFamily);
        if (!record.ComponentKeys.All(components.ContainsKey)) return false;
        var workingSet = record.ComponentKeys.Aggregate(0L, (total, key) =>
        {
            var componentBytes = components[key].Aggregate(0L, (componentTotal, process) =>
            {
                var bytes = Math.Max(0, process.WorkingSetBytes);
                return bytes > long.MaxValue - componentTotal ? long.MaxValue : componentTotal + bytes;
            });
            return componentBytes > long.MaxValue - total ? long.MaxValue : total + componentBytes;
        });
        return workingSet <= limit;
    }

    private ProcessRow CreateCandidateRow(
        OptimizationCandidate candidate,
        ProtectionRules protection,
        ProtectionContext protectionContext,
        DateTimeOffset now)
    {
        var originalFamily = _families.FirstOrDefault(family =>
            string.Equals(family.Key, candidate.Family.Key, StringComparison.OrdinalIgnoreCase)) ?? candidate.Family;
        var targetFamily = new ProcessFamilySnapshot(
            candidate.Family.Key,
            candidate.Family.DisplayName,
            candidate.Family.ExecutableDirectory,
            candidate.TargetProcesses);
        var displayWorkingSetBytes = DisplayProcessableWorkingSetBytes(originalFamily);
        var autoOptimizationStatus = FormatBackoffStatus(candidate.Family, now);
        var row = CreateProcessRow(originalFamily, protection, protectionContext,
            decisionFamily: targetFamily, now: now) with
        {
            AutoOptimizationStatus = autoOptimizationStatus,
            Memory = TF(
                "CandidateMemoryFormat",
                DisplayFormat.Bytes(displayWorkingSetBytes),
                DisplayFormat.Bytes(originalFamily.WorkingSetBytes)),
            MemoryDetail = TF(
                "ProcessMemoryDetailFormat",
                DisplayFormat.Bytes(displayWorkingSetBytes),
                DisplayFormat.BinaryThreshold(_settings.ResolveOptimizationSettings(manual: false).MinimumFamilyWorkingSetBytes),
                DisplayFormat.Bytes(originalFamily.WorkingSetBytes)),
            IdleScore = targetFamily.IdleScore.ToString("0.0", CultureInfo.CurrentCulture),
            IdleScoreDetail = TF("IdleScoreDetailFormat", targetFamily.IdleScore, targetFamily.Processes.Count)
        };
        if (row.RetentionIcon == RetentionStatusIcon.None &&
            (string.Equals(row.IdleStatus, T("ActivityObserving"), StringComparison.Ordinal) ||
             string.Equals(row.IdleStatus, T("ActivityMinimized"), StringComparison.Ordinal)))
        {
            row = row with { RetentionIcon = RetentionStatusIcon.ActivityObserving };
        }
        if (!_settings.IntelligentCandidateSelection)
        {
            return row with { Ranking = T("LearningOffShort") };
        }

        var record = _applicationBackoffTracker.LearningRecords.FirstOrDefault(entry =>
            string.Equals(entry.FamilyKey, candidate.Family.Key, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return row with { Ranking = T("LearningPendingShort") };
        }
        if (record.ValidSampleCount < 3)
        {
            return row with { Ranking = TF("LearningInProgressShortFormat", record.ValidSampleCount) };
        }

        var expectedBytes = BenefitAwareRanking.ExpectedRetainedBytes(
            candidate,
            _applicationBackoffTracker.OutcomeMultipliers);
        return row with
        {
            Ranking = TF("LearningEstimateShortFormat", DisplayFormat.Bytes((long)expectedBytes))
        };
    }

    private string FormatBackoffStatus(ProcessFamilySnapshot family, DateTimeOffset now) =>
        _applicationBackoffTracker.GetBackoffStatus(
            family.Key,
            ApplicationComponentIdentity.GroupProcesses(family).Keys.ToArray(),
            now) switch
        {
            null => T("NoBackoff"),
            { ObservationPending: true } => T("ReboundObservationPending"),
            { LongTermObservation: true, LongTermSawForeground: true } =>
                T("AutoBackoffLongTermAwaitingIdle"),
            { LongTermObservation: true } => T("AutoBackoffLongTerm"),
            { BlockedUntil: { } until } => TF(
                "AutoBackoffSecondsFormat",
                FormatRemaining(until - now)),
            _ => T("NoBackoff")
        };

    private (string Status, string? Detail) FormatRetentionIndicator(
        ProcessRetentionIndicator indicator,
        ProcessFamilySnapshot decisionFamily,
        IEnumerable<string> componentKeys,
        IReadOnlyList<NaturalStableObservationStatus> observationStatuses,
        DateTimeOffset now,
        NaturalStableReviewSchedule? reviewSchedule = null)
    {
        var componentKeySet = componentKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observation = observationStatuses.FirstOrDefault(status =>
            status.ComponentKeys.Any(componentKeySet.Contains) && indicator switch
            {
                ProcessRetentionIndicator.NaturalStableGrowthReview => status.IsGrowthReview,
                ProcessRetentionIndicator.NaturalStableReview =>
                    !status.IsGrowthReview &&
                    status.Origin == NaturalStableObservationOrigin.HistoricalBoundedConfirmation,
                ProcessRetentionIndicator.NaturalStableObservation =>
                    !status.IsGrowthReview &&
                    status.Origin != NaturalStableObservationOrigin.HistoricalBoundedConfirmation,
                _ => true
            });
        string Remaining(DateTimeOffset until) => FormatRemaining(until - now);
        return indicator switch
        {
            ProcessRetentionIndicator.EntireFamilyProtection =>
                (T("ProtectionActive"), T("ProtectedStateHelp")),
            ProcessRetentionIndicator.PartialProtection =>
                (T("ProtectionPartial"), T("ProtectedStateHelp")),
            ProcessRetentionIndicator.SessionStableState =>
                (T("ProcessStatusSessionStable"),
                    observation is { Phase: StableObservationPhase.ProvisionalValidation,
                        ValidationDeadline: not null }
                        ? FormatStableValidationObservation(observation, now)
                        : reviewSchedule is null
                            ? T("ProcessStatusSessionStableHelp")
                            : FormatStableReviewSchedule(reviewSchedule, now)),
            ProcessRetentionIndicator.LongTermStableState =>
                (T("CandidateStableStateSuppressed"),
                    observation is { Phase: StableObservationPhase.ProvisionalValidation,
                        ValidationDeadline: not null }
                        ? TF("StableStateRetainedDuringReviewFormat",
                            FormatStableValidationObservation(observation, now))
                        : observation is { Origin: NaturalStableObservationOrigin.HistoricalBoundedConfirmation }
                            ? TF("StableStateRetainedDuringReviewFormat",
                                FormatStableReviewObservation(observation, now))
                            : reviewSchedule is null
                                ? T("StableStateRetainedHelp")
                                : FormatStableReviewSchedule(reviewSchedule, now)),
            ProcessRetentionIndicator.NaturalStableObservation =>
                (T("ProcessStatusStableObservation"), observation is null
                    ? T("ProcessStatusStableObservationHelpV2")
                    : observation.HasFiniteDeadline
                        ? TF(observation.LatestIsLowActivity
                                ? "ProcessStatusStableObservationDetailFormat"
                                : "ProcessStatusStableObservationActiveDetailFormat",
                            Remaining(observation.Deadline),
                            observation.ObservationCount, DisplayFormat.Bytes(observation.LatestWorkingSetBytes))
                        : TF(observation.LatestIsLowActivity
                                ? "ProcessStatusStableRollingDetailFormat"
                                : "ProcessStatusStableRollingActiveDetailFormat",
                            observation.ObservationCount,
                            DisplayFormat.Bytes(observation.LatestWorkingSetBytes))),
            ProcessRetentionIndicator.NaturalStableReview =>
                (T("ProcessStatusStableReview"), observation is null
                    ? T("ProcessStatusStableReviewHelp")
                    : TF("StableStateRetainedDuringReviewFormat",
                        observation is { Phase: StableObservationPhase.ProvisionalValidation,
                            ValidationDeadline: not null }
                            ? FormatStableValidationObservation(observation, now)
                            : FormatStableReviewObservation(observation, now))),
            ProcessRetentionIndicator.NaturalStableGrowthReview =>
                (T("ProcessStatusStableGrowthReview"), observation is null
                    ? T("ProcessStatusStableGrowthReviewHelpV2")
                    : TF("ProcessStatusStableGrowthReviewLimitDetailFormat",
                        DisplayFormat.Bytes(observation.ValidationUpperLimitBytes ?? 0),
                        DisplayFormat.Bytes(observation.LatestWorkingSetBytes),
                        observation.ValidationDeadline is { } validationDeadline
                            ? Remaining(validationDeadline)
                            : Remaining(observation.Deadline))),
            ProcessRetentionIndicator.BenefitObservation =>
                (T("ProcessStatusBenefitObservation"), T("ProcessStatusBenefitObservationHelp")),
            ProcessRetentionIndicator.BenefitObservationWithHistoricalStable =>
                (T("ProcessStatusBenefitObservation"),
                    T("ProcessStatusBenefitObservationWithHistoricalStableHelp")),
            ProcessRetentionIndicator.AutomaticBackoff =>
                (T("ProcessStatusBackoff"), T("AutoOptimizationStatusHelp")),
            ProcessRetentionIndicator.Foreground =>
                (T("ProcessStatusForeground"), T("ProcessStatusForegroundHelp")),
            ProcessRetentionIndicator.IoActivity =>
                (T("ProcessStatusIoActivity"), T("ProcessStatusActivityHelp")),
            ProcessRetentionIndicator.CpuActivity =>
                (T("ProcessStatusCpuActivity"), T("ProcessStatusActivityHelp")),
            ProcessRetentionIndicator.Sampling =>
                (T("ProcessStatusSampling"), T("ProcessStatusSamplingHelp")),
            ProcessRetentionIndicator.Cooldown =>
                (T("ProcessStatusCooldown"), T("ProcessStatusCooldownHelp")),
            ProcessRetentionIndicator.VisibleWindowWait =>
                (T("ProcessStatusVisibleWait"), T("ProcessStatusVisibleWaitHelp")),
            ProcessRetentionIndicator.BelowWorkingSetThreshold =>
                (T("ProcessStatusBelowThreshold"), T("ProcessStatusBelowThresholdHelp")),
            ProcessRetentionIndicator.RelationshipActivity =>
                (T("ProcessStatusRelationship"), T("ProcessStatusRelationshipHelp")),
            ProcessRetentionIndicator.BelowIdleScore =>
                (T("ProcessStatusIdleScore"), T("ProcessStatusIdleScoreHelp")),
            ProcessRetentionIndicator.CandidateReady =>
                (T("ProcessStatusCandidateReady"), T("ProcessStatusCandidateReadyHelp")),
            _ => ("--", null)
        };
    }

    private string FormatStableValidationObservation(
        NaturalStableObservationStatus observation,
        DateTimeOffset now) =>
        TF(
            observation.ContinuousStableSince.HasValue
                ? "ProcessStatusStableValidationDetailFormat"
                : "ProcessStatusStableValidationWaitingDetailFormat",
            observation.ContinuousStableSince is { } stableSince
                ? FormatRemaining(now - stableSince)
                : FormatRemaining(TimeSpan.Zero),
            FormatRemaining(observation.ValidationDeadline!.Value - now),
            DisplayFormat.Bytes(observation.LatestWorkingSetBytes));

    private string FormatStableReviewObservation(
        NaturalStableObservationStatus observation,
        DateTimeOffset now) =>
        observation.HasFiniteDeadline
            ? TF(
                observation.LatestIsLowActivity
                    ? "ProcessStatusStableReviewDetailFormat"
                    : "ProcessStatusStableReviewActiveDetailFormat",
                FormatRemaining(observation.Deadline - now),
                observation.ObservationCount,
                DisplayFormat.Bytes(observation.LatestWorkingSetBytes))
            : TF(
                observation.LatestIsLowActivity
                    ? "ProcessStatusStableReviewRollingDetailFormat"
                    : "ProcessStatusStableReviewRollingActiveDetailFormat",
                FormatRemaining(now - observation.StartedAt),
                DisplayFormat.Bytes(observation.LatestWorkingSetBytes));

    private string FormatStableReviewSchedule(
        NaturalStableReviewSchedule schedule,
        DateTimeOffset now) =>
        schedule.AwaitingNewRecoveryCycle
            ? TF(
                "ProcessStatusStableReviewAwaitingRecoveryCycleDetailFormat",
                schedule.CompletedReviewCount,
                schedule.InitialReviewTarget,
                schedule.HighMigrationRecoveryCycleCount,
                schedule.RequiredHighMigrationRecoveryCycles)
            : schedule.NextReviewAt > now
            ? TF(
                "ProcessStatusStableReviewScheduleDetailFormat",
                FormatStableReviewRemaining(schedule.NextReviewAt - now),
                schedule.CompletedReviewCount,
                schedule.InitialReviewTarget,
                schedule.HighMigrationRecoveryCycleCount,
                schedule.RequiredHighMigrationRecoveryCycles)
            : TF(
                "ProcessStatusStableReviewDueDetailFormat",
                schedule.CompletedReviewCount,
                schedule.InitialReviewTarget,
                schedule.HighMigrationRecoveryCycleCount,
                schedule.RequiredHighMigrationRecoveryCycles);

    private string FormatStableReviewRemaining(TimeSpan remaining)
    {
        var totalSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        if (totalSeconds < 60 * 60) return FormatRemaining(remaining);

        var totalMinutes = totalSeconds / 60;
        return TF(
            "StableReviewRemainingHoursMinutesFormat",
            totalMinutes / 60,
            totalMinutes % 60);
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", seconds / 60, seconds % 60);
    }

    private static long DisplayProcessableWorkingSetBytes(ProcessFamilySnapshot family) =>
        family.Processes.Aggregate(0L, (total, process) =>
        {
            if (process.ProcessId == Environment.ProcessId) return total;
            var bytes = Math.Max(0, process.WorkingSetBytes);
            return bytes > long.MaxValue - total ? long.MaxValue : total + bytes;
        });

    private ProcessRow CreateProcessRow(
        ProcessFamilySnapshot family,
        ProtectionRules protection,
        ProtectionContext protectionContext,
        CandidateEvaluation? evaluation = null,
        OptimizationSettings? settings = null,
        DateTimeOffset now = default,
        ProcessFamilySnapshot? decisionFamily = null)
    {
        var observedAt = now;
        var unprotectedFamily = protection.FilterUnprotectedProcesses(family, protectionContext);
        var isProtected = unprotectedFamily is null;
        var isPartiallyProtected = unprotectedFamily is not null &&
                                   unprotectedFamily.Processes.Count < family.Processes.Count;
        var activityFamily = unprotectedFamily ?? family;
        if (decisionFamily is not null)
            activityFamily = protection.FilterUnprotectedProcesses(decisionFamily, protectionContext) ?? decisionFamily;
        var assessment = _activity.GetValueOrDefault(family.Key);
        var activity = isProtected
            ? "--"
            : activityFamily.HasReliableActivitySample
            ? activityFamily.HasForegroundProcess
                ? T("ActivityForeground")
                : assessment is not null
                    ? assessment.State switch
                    {
                        BackgroundActivityState.Idle => T("ActivityIdle"),
                        BackgroundActivityState.Working => T("ActivityWorking"),
                        BackgroundActivityState.Visible => T("ActivityVisible"),
                        _ => activityFamily.HasMinimizedWindow ? T("ActivityMinimized") : T("ActivityObserving")
                    }
                    : activityFamily.HasVisibleWindow
                        ? T("ActivityVisible")
                        : activityFamily.HasMinimizedWindow ? T("ActivityMinimized") : T("ActivityObserving")
            : T("ActivityObserving");
        var componentKeys = ApplicationComponentIdentity.GroupProcesses(activityFamily).Keys;
        var observationStatuses = _applicationBackoffTracker.NaturalStableObservationStatuses();
        var stableSettings = _settings.IntelligentCandidateSelection
            ? _settings.ResolveStableStateSuppressionSettings()
            : null;
        var activeStableRecord = settings is null || stableSettings is null
            ? null
            : StableStateSuppressionPolicy.ActiveStableRecord(
                family,
                _families,
                _applicationBackoffTracker.FamilyStableLearningRecords,
                settings,
                protection);
        var hasLongTermStableReference = HasActiveLongTermStableReference(
            unprotectedFamily,
            activeStableRecord,
            stableSettings,
            observedAt);
        var stableComponentKeys = activeStableRecord is { ComponentKeys.Count: > 0 }
            ? activeStableRecord.ComponentKeys
            : componentKeys;
        var pendingBenefitObservation = componentKeys.Any(
            _applicationBackoffTracker.PendingObservationComponentKeys(observedAt).Contains);
        var naturalStableObservation = !pendingBenefitObservation && stableComponentKeys.Any(
            _applicationBackoffTracker.NaturalStableObservationComponentKeys().Contains);
        var naturalStableReview = !pendingBenefitObservation && stableComponentKeys.Any(
            _applicationBackoffTracker.NaturalStableReviewComponentKeys().Contains);
        var naturalStableGrowthReview = !pendingBenefitObservation && stableComponentKeys.Any(
            _applicationBackoffTracker.NaturalStableGrowthReviewComponentKeys().Contains);
        var naturalStableProvisionalValidation = !pendingBenefitObservation && stableComponentKeys.Any(
            _applicationBackoffTracker.NaturalStableProvisionalValidationComponentKeys().Contains);
        var retentionIndicator = ProcessRetentionPresentation.Resolve(
            isProtected,
            isPartiallyProtected,
            evaluation?.ExclusionReasons,
            naturalStableObservation,
            hasLongTermStableReference,
            evaluation?.IsEligible == true,
            evaluation?.TargetProcessCount > 0,
            naturalStableReview,
            naturalStableGrowthReview,
            naturalStableProvisionalValidation,
            reboundObservationPending: pendingBenefitObservation);
        var reviewSchedule = stableSettings is null
            ? null
            : _applicationBackoffTracker.GetNaturalStableReviewSchedule(
                activityFamily.Key,
                stableComponentKeys,
                stableSettings,
                StableStateSuppressionPolicy.CurrentNaturalStableLaunchSignature(
                    activityFamily, stableComponentKeys));
        var (protectedText, protectionDetail) = FormatRetentionIndicator(
            retentionIndicator, activityFamily, stableComponentKeys, observationStatuses, observedAt,
            reviewSchedule);
        var retentionIcon = RetentionIconFor(retentionIndicator);
        var mainProcess = activityFamily.Processes.OrderByDescending(process => process.WorkingSetBytes).First();
        var targetWorkingSetBytes = DisplayProcessableWorkingSetBytes(family);
        var targetProcessCount = evaluation?.TargetProcessCount ?? activityFamily.Processes.Count;
        var idleScore = isProtected || targetProcessCount == 0
            ? T("IdleScoreUnavailable")
            : (evaluation?.LegacyIdleScore ?? activityFamily.IdleScore).ToString("0.0", CultureInfo.CurrentCulture);
        var minimumWorkingSetBytes = settings?.MinimumFamilyWorkingSetBytes ?? 0;
        var idleStatus = FormatIdleStatus(activityFamily, activity);
        if (!string.Equals(idleStatus, activity, StringComparison.Ordinal) &&
            retentionIndicator is ProcessRetentionIndicator.None)
        {
            retentionIcon = RetentionStatusIcon.Idle;
        }
        return new ProcessRow(
            mainProcess.ProcessId,
            family.DisplayName,
            TF(
                "CandidateMemoryFormat",
                DisplayFormat.Bytes(targetWorkingSetBytes),
                DisplayFormat.Bytes(family.WorkingSetBytes)),
            settings is null
                ? null
                : TF(
                    evaluation?.ExclusionReasons.Contains(CandidateExclusionReason.BelowFamilyWorkingSet) == true
                        ? "ProcessMemoryInsufficientDetailFormat"
                        : "ProcessMemoryDetailFormat",
                    DisplayFormat.Bytes(targetWorkingSetBytes),
                    DisplayFormat.BinaryThreshold(minimumWorkingSetBytes),
                    DisplayFormat.Bytes(family.WorkingSetBytes)),
            family.WorkingSetBytes,
            idleScore,
            isProtected || targetProcessCount == 0
                ? null
                : TF(
                    "IdleScoreDetailFormat",
                    evaluation?.LegacyIdleScore ?? activityFamily.IdleScore,
                    targetProcessCount),
            idleStatus,
            null,
            protectedText,
            protectionDetail,
            retentionIcon,
            string.Empty,
            string.Empty,
            mainProcess.ExecutablePath,
            family);
    }

    private static RetentionStatusIcon RetentionIconFor(ProcessRetentionIndicator indicator) => indicator switch
    {
        ProcessRetentionIndicator.EntireFamilyProtection => RetentionStatusIcon.Protected,
        ProcessRetentionIndicator.PartialProtection => RetentionStatusIcon.PartiallyProtected,
        ProcessRetentionIndicator.SessionStableState => RetentionStatusIcon.SessionStable,
        ProcessRetentionIndicator.LongTermStableState => RetentionStatusIcon.Stable,
        ProcessRetentionIndicator.NaturalStableReview => RetentionStatusIcon.Review,
        ProcessRetentionIndicator.NaturalStableGrowthReview => RetentionStatusIcon.GrowthReview,
        ProcessRetentionIndicator.BelowWorkingSetThreshold or ProcessRetentionIndicator.BelowIdleScore =>
            RetentionStatusIcon.Threshold,
        ProcessRetentionIndicator.CandidateReady => RetentionStatusIcon.Candidate,
        ProcessRetentionIndicator.NaturalStableObservation => RetentionStatusIcon.StableObserving,
        ProcessRetentionIndicator.BenefitObservation or
            ProcessRetentionIndicator.BenefitObservationWithHistoricalStable => RetentionStatusIcon.Observing,
        ProcessRetentionIndicator.Sampling or ProcessRetentionIndicator.Cooldown or
            ProcessRetentionIndicator.VisibleWindowWait =>
            RetentionStatusIcon.Waiting,
        ProcessRetentionIndicator.AutomaticBackoff => RetentionStatusIcon.Backoff,
        ProcessRetentionIndicator.Foreground or
            ProcessRetentionIndicator.IoActivity or ProcessRetentionIndicator.CpuActivity or
            ProcessRetentionIndicator.RelationshipActivity => RetentionStatusIcon.Activity,
        _ => RetentionStatusIcon.None
    };

    private OptimizationSettings ResolvePreviewSettings()
    {
        var settings = _settings.ResolveOptimizationSettings(manual: false);
        return settings with
        {
            EnhancedSafety = _settings.EnhancedSafety,
            IgnoreMemoryPressureThreshold = true,
            IntelligentCandidateSelection = _settings.IntelligentCandidateSelection,
            MaxApplications = int.MaxValue
        };
    }

    private string FormatIdleStatus(ProcessFamilySnapshot family, string currentActivity)
    {
        if (!_activity.TryGetValue(family.Key, out var assessment))
            return currentActivity;
        return !family.HasForegroundProcess &&
               assessment.IdleFor >= BackgroundActivityTracker.MinimumObservation &&
               assessment.ObservedFor >= BackgroundActivityTracker.MinimumObservation &&
               assessment.SampleCount >= BackgroundActivityTracker.MinimumSamples
            ? TF(
                "UnusedMinutesFormat",
                Math.Max(1, (int)Math.Floor(assessment.IdleFor.TotalMinutes)))
            : currentActivity;
    }

    private OptimizationPlan CreatePreviewPlan(
        DateTimeOffset now,
        ProtectionRules protection,
        OptimizationSettings settings)
    {
        var learningFilters = CurrentLearningFilters(now);
        return _planner.CreatePlan(
            _currentMemory,
            _families,
            settings,
            protection,
            _lastTrimTimes,
            now,
            manual: false,
            _activity,
            automaticBackoffFamilies: null,
            _applicationBackoffTracker.OutcomeMultipliers,
            intelligentPreview: true,
            learningConfidences: _applicationBackoffTracker.LearningConfidences,
            candidateIdleReadiness: _candidateIdleReadiness,
            pendingReboundObservationFamilies: null,
            lastTrimProcessStartTimes: _lastTrimProcessStartTimes,
            automaticBackoffComponents: learningFilters.BlockedComponents,
            pendingReboundObservationComponents: learningFilters.PendingComponents,
            stableSuppressedComponents: learningFilters.StableComponents,
            automaticThresholdOverrides: ApplicationOptimizationRulePolicy.CreateAutomaticThresholdOverrides(
                ApplicationOptimizationRuleSettings.Resolve(_settings),
                _families,
                _settings.AutoOptimization));
    }

    private static int ProtectionSortOrder(
        ProcessFamilySnapshot family,
        ProtectionRules protection,
        ProtectionContext protectionContext)
    {
        var unprotectedFamily = protection.FilterUnprotectedProcesses(family, protectionContext);
        if (unprotectedFamily is null) return 2;
        return unprotectedFamily.Processes.Count < family.Processes.Count ? 1 : 0;
    }

    private ProcessFamilySnapshot? ResolveCurrentTargetFamily(ProcessRow target)
    {
        var byKey = _families.FirstOrDefault(family =>
            string.Equals(family.Key, target.Family.Key, StringComparison.OrdinalIgnoreCase));
        if (byKey is not null) return byKey;
        if (string.IsNullOrWhiteSpace(target.Path)) return null;

        var normalizedPath = NormalizeExecutablePath(target.Path);
        return _families.FirstOrDefault(family => family.Processes.Any(process =>
            !string.IsNullOrWhiteSpace(process.ExecutablePath) &&
            string.Equals(
                NormalizeExecutablePath(process.ExecutablePath),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase)));
    }

    private async void OptimizeNow_OnClick(object sender, RoutedEventArgs e) =>
        await RunOptimizationAsync(manual: true);

    private void MainWindow_OnPreviewMouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestorButton(source) is { } trigger && ResolveManagedPopup(trigger) is not null)
        {
            return;
        }
        CloseManagedPopups();
        if (TryClosePopupFromTrigger(source))
        {
            e.Handled = true;
            return;
        }
        if (!IsDescendantOf(source, ProcessesGrid)) ProcessesGrid.UnselectAll();
        if (!IsDescendantOf(source, HistoryList)) HistoryList.UnselectAll();
        if (!IsDescendantOf(source, CandidatesGrid)) CandidatesGrid.UnselectAll();
    }

    private void ComboBox_OnPreviewMouseWheel(
        object sender,
        System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox || comboBox.IsDropDownOpen) return;

        e.Handled = true;
        if (VisualTreeHelper.GetParent(comboBox) is not UIElement parent) return;

        parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
            e.MouseDevice,
            e.Timestamp,
            e.Delta)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent,
            Source = comboBox
        });
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static Button? FindAncestorButton(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button button) return button;
        }
        return null;
    }

    private Popup? ResolveManagedPopup(Button button) =>
        ReferenceEquals(button, ScheduleMenuButton) ? SchedulePopup
        : ReferenceEquals(button, CandidateModeMenuButton) ? CandidateModePopup
        : ReferenceEquals(button, UpdateFrequencyMenuButton) ? UpdateFrequencyPopup
        : button.Parent is Grid parent
            ? parent.Children.OfType<Popup>().FirstOrDefault(candidate =>
                candidate.StaysOpen && ReferenceEquals(candidate.PlacementTarget, button))
            : null;

    private void ManagedPopupTrigger_OnPreviewMouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Button button || ResolveManagedPopup(button) is not { } popup) return;
        if (ReferenceEquals(button, ScheduleMenuButton) && IsScheduledOptimizationUnavailable())
        {
            e.Handled = true;
            return;
        }

        var open = !popup.IsOpen;
        CloseManagedPopups(popup);
        popup.IsOpen = open;
        e.Handled = true;
    }

    private void CloseManagedPopups(Popup? except = null)
    {
        if (!ReferenceEquals(SchedulePopup, except)) SchedulePopup.IsOpen = false;
        if (!ReferenceEquals(CandidateModePopup, except)) CandidateModePopup.IsOpen = false;
        if (!ReferenceEquals(UpdateFrequencyPopup, except)) UpdateFrequencyPopup.IsOpen = false;
        foreach (var popup in _openApplicationRulePopups.ToArray())
        {
            if (!ReferenceEquals(popup, except)) popup.IsOpen = false;
        }
    }

    private bool TryClosePopupFromTrigger(DependencyObject source)
    {
        Button? button = null;
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button candidate)
            {
                button = candidate;
                break;
            }
        }

        if (button is null) return false;
        Popup? popup = ReferenceEquals(button, ScheduleMenuButton) ? SchedulePopup
            : ReferenceEquals(button, CandidateModeMenuButton) ? CandidateModePopup
            : ReferenceEquals(button, CandidateDisplayMenuButton) ? CandidateDisplayPopup
            : ReferenceEquals(StableAnchorPopup.PlacementTarget, button) ? StableAnchorPopup
            : button.Parent is Grid parent
                ? parent.Children.OfType<Popup>().FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.PlacementTarget, button))
                : null;
        if (popup?.IsOpen != true) return false;

        _suppressedPopupTriggerClicks.Add(button);
        popup.IsOpen = false;
        return true;
    }

    private bool ConsumeSuppressedPopupTriggerClick(object sender) =>
        sender is Button button && _suppressedPopupTriggerClicks.Remove(button);

    private void ProcessesGrid_OnPreviewMouseRightButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var row = source is null
            ? null
            : ItemsControl.ContainerFromElement(ProcessesGrid, source) as DataGridRow;
        ProcessesGrid.SelectedItem = row?.Item;
    }

    private void ProcessesContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        UpdateApplicationContextMenu(
            ProcessesGrid.SelectedItem as ProcessRow,
            OptimizeSelectedApplicationMenuItem,
            ProtectSelectedApplicationMenuItem);
    }

    private void CandidatesGrid_OnPreviewMouseRightButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var row = source is null
            ? null
            : ItemsControl.ContainerFromElement(CandidatesGrid, source) as DataGridRow;
        CandidatesGrid.SelectedItem = row?.Item;
    }

    private void CandidatesContextMenu_OnOpened(object sender, RoutedEventArgs e) =>
        UpdateApplicationContextMenu(
            CandidatesGrid.SelectedItem as ProcessRow,
            OptimizeCandidateMenuItem,
            ProtectCandidateMenuItem);

    private void ProcessesContextMenu_OnClosed(object sender, RoutedEventArgs e) => ProcessesGrid.UnselectAll();

    private void CandidatesContextMenu_OnClosed(object sender, RoutedEventArgs e) => CandidatesGrid.UnselectAll();

    private void UpdateApplicationContextMenu(
        ProcessRow? row,
        MenuItem optimizeItem,
        MenuItem protectItem)
    {
        optimizeItem.Tag = row;
        protectItem.Tag = row;
        var protectionCandidate = row is null ? null : CreateProtectionCandidate(row);
        optimizeItem.IsEnabled = !_state.IsBusy && row is not null &&
            (!_settings.EnhancedSafety || !row.Family.HasForegroundProcess);
        optimizeItem.ToolTip = row is not null &&
            _settings.EnhancedSafety && row.Family.HasForegroundProcess
                ? T("ForegroundOptimizationEnhancedSafetyBlocked")
                : null;
        protectItem.IsEnabled = !_state.IsBusy &&
            protectionCandidate is { ProtectionState: not ApplicationProtectionState.EntireFamily };
        protectItem.ToolTip = protectionCandidate switch
        {
            null when row is not null => T("ApplicationProtectionPathUnavailable"),
            { ProtectionState: ApplicationProtectionState.EntireFamily } => T("ApplicationAlreadyProtected"),
            _ => null
        };
    }

    private RunningProtectionCandidate? CreateProtectionCandidate(ProcessRow row)
    {
        var family = ResolveCurrentTargetFamily(row);
        return family is null
            ? null
            : RunningProtectionCandidateCatalog.Create(
                new[] { family },
                ApplicationProtectionSettings.Resolve(_settings)).SingleOrDefault();
    }

    private void ProtectSelectedApplication_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy || (sender as FrameworkElement)?.Tag is not ProcessRow row) return;
        ProtectApplication(row);
    }

    private void ProtectCandidate_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy || (sender as FrameworkElement)?.Tag is not ProcessRow row) return;
        ProtectApplication(row);
    }

    private void ProtectApplication(ProcessRow row)
    {
        var candidate = CreateProtectionCandidate(row);
        if (candidate is null || candidate.ProtectionState == ApplicationProtectionState.EntireFamily) return;
        if (!TryUpdateSettings(settings => ApplicationProtectionSettings.ProtectEntireFamily(
                settings,
                candidate.ApplicationExecutablePath))) return;

        RefreshProtectedList();
        UpdateProcessRows();
        UpdatePreviewRows();
        var message = TF("ApplicationProtectionAddedFormat", row.Name);
        _state.Status = message;
        AddHistory("ApplicationProtectionAddedFormat", row.Name);
    }

    private async void OptimizeSelectedApplication_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProcessRow row) return;
        await OptimizeApplicationAsync(row);
    }

    private async void OptimizeCandidate_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProcessRow row) return;
        await OptimizeApplicationAsync(row);
    }

    private async Task OptimizeApplicationAsync(ProcessRow row)
    {
        if (!_settings.SelectedApplicationOptimizationPromptSuppressed &&
            !ShowSelectedApplicationOptimizationPrompt(row.Name))
        {
            return;
        }
        await RunOptimizationAsync(manual: true, target: row);
    }

    private async void OptimizeProtectedGroup_OnClick(object sender, RoutedEventArgs e)
    {
        CloseContainingPopup(sender);
        var group = (sender as FrameworkElement)?.Tag as ProtectedApplicationGroup;
        if (_state.IsBusy || group is not { IsRunning: true })
        {
            return;
        }
        if (!_settings.SelectedApplicationOptimizationPromptSuppressed &&
            !ShowSelectedApplicationOptimizationPrompt(group.Name, bypassProtection: true))
        {
            return;
        }

        await RunOptimizationAsync(
            manual: true,
            protectedTarget: new ProtectedOptimizationTarget(
                group.FamilyKey,
                group.Name,
                group.Executables.Select(executable => executable.Path).ToArray()));
    }

    private async void OptimizeProtectedExecutable_OnClick(object sender, RoutedEventArgs e)
    {
        CloseContainingPopup(sender);
        var executable = (sender as FrameworkElement)?.Tag as ProtectedExecutableEntry;
        if (_state.IsBusy || executable is not { IsRunning: true })
        {
            return;
        }
        if (!_settings.SelectedApplicationOptimizationPromptSuppressed &&
            !ShowSelectedApplicationOptimizationPrompt(executable.Name, bypassProtection: true))
        {
            return;
        }

        await RunOptimizationAsync(
            manual: true,
            protectedTarget: new ProtectedOptimizationTarget(
                executable.FamilyKey,
                executable.Name,
                new[] { executable.Path }));
    }

    private async void SetApplicationRule_OnClick(object sender, RoutedEventArgs e)
    {
        CloseContainingPopup(sender);
        if ((sender as FrameworkElement)?.Tag is not ProtectedApplicationGroup group) return;
        await ConfigureApplicationRuleAsync(
            new ApplicationOptimizationRuleTarget
            {
                TargetType = ApplicationOptimizationTargetType.ExecutableGroup,
                Path = group.Path,
                ExecutablePaths = group.Executables.Count > 0
                    ? group.Executables.Select(executable => executable.Path).ToList()
                    : new List<string> { group.Path }
            },
            group.Name,
            protectedTarget: true);
    }

    private async void SetApplicationRuleExecutable_OnClick(object sender, RoutedEventArgs e)
    {
        CloseContainingPopup(sender);
        if ((sender as FrameworkElement)?.Tag is not ProtectedExecutableEntry executable) return;
        await ConfigureApplicationRuleAsync(
            new ApplicationOptimizationRuleTarget
            {
                TargetType = ApplicationOptimizationTargetType.Executable,
                Path = executable.Path
            },
            executable.Name,
            protectedTarget: true);
    }

    private async void EditProtectionGroup_OnClick(object sender, RoutedEventArgs e)
    {
        CloseContainingPopup(sender);
        if ((sender as FrameworkElement)?.Tag is not ProtectedApplicationGroup group || _state.IsBusy)
            return;
        if (!await RefreshSnapshotAsync(waitForCurrentRefresh: true)) return;
        var currentRules = ApplicationProtectionSettings.Resolve(_settings);
        var candidate = RunningProtectionCandidateCatalog.Create(_families, currentRules)
            .FirstOrDefault(item => string.Equals(
                item.FamilyKey,
                group.FamilyKey,
                StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            _state.Status = T("ApplicationRuleTargetNotRunning");
            return;
        }
        var selections = ShowRunningProtectionDialog(new[] { candidate });
        if (selections is null || _state.IsBusy) return;
        var mergedRules = RunningProtectionCandidateCatalog.MergeSelections(currentRules, selections);
        if (!TryUpdateSettings(settings => ApplicationProtectionSettings.Replace(settings, mergedRules))) return;
        RefreshProtectedList();
        UpdateProcessRows();
        UpdatePreviewRows();
    }

    private async Task ConfigureApplicationRuleAsync(
        ApplicationOptimizationRuleTarget suggestedTarget,
        string displayName,
        bool protectedTarget)
    {
        if (_state.IsBusy) return;
        if (!await RefreshSnapshotAsync(waitForCurrentRefresh: true)) return;
        if (_state.IsBusy) return;
        if (!ExecutablePathIdentity.TryNormalize(suggestedTarget.Path, out var normalizedPath))
        {
            _state.Status = T("ApplicationRulePathUnavailable");
            return;
        }

        var rules = ApplicationOptimizationRuleSettings.Resolve(_settings).ToList();
        suggestedTarget.Path = normalizedPath;
        suggestedTarget.ExecutablePaths = suggestedTarget.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
            ? suggestedTarget.ExecutablePaths
                .Where(path => ExecutablePathIdentity.TryNormalize(path, out _))
                .Select(ExecutablePathIdentity.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
        if (suggestedTarget.TargetType == ApplicationOptimizationTargetType.ExecutableGroup &&
            suggestedTarget.ExecutablePaths.Count == 0)
        {
            _state.Status = T("ApplicationRuleTargetRequired");
            return;
        }
        var existing = rules.FirstOrDefault(rule => rule.Targets.Any(target =>
            target.TargetType == suggestedTarget.TargetType &&
            (suggestedTarget.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
                ? string.Equals(target.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)
                : string.Equals(
                    ApplicationOptimizationRulePolicy.TargetIdentity(target),
                    ApplicationOptimizationRulePolicy.TargetIdentity(suggestedTarget),
                    StringComparison.OrdinalIgnoreCase))));
        if (HasApplicationRuleTargetConflict(
                new ApplicationOptimizationRule { Targets = new() { CloneApplicationRuleTarget(suggestedTarget) } },
                existing?.Id))
            return;
        var previousTargetFlags = (existing?.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .ToDictionary(
                candidate => ApplicationOptimizationRulePolicy.TargetIdentity(candidate),
                candidate => candidate.BypassProtectionConfirmed == true,
                StringComparer.OrdinalIgnoreCase);
        var rule = ShowApplicationOptimizationRuleDialog(
            existing,
            existing is null ? suggestedTarget : null,
            displayName);
        if (rule is null) return;
        if (HasApplicationRuleTargetConflict(rule, existing?.Id)) return;

        var configuredTarget = rule.Targets.FirstOrDefault(candidate =>
            candidate.TargetType == suggestedTarget.TargetType &&
            (suggestedTarget.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
                ? string.Equals(candidate.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)
                : string.Equals(
                    ApplicationOptimizationRulePolicy.TargetIdentity(candidate),
                    ApplicationOptimizationRulePolicy.TargetIdentity(suggestedTarget),
                    StringComparison.OrdinalIgnoreCase)));
        if (configuredTarget is null) return;

        var targetsRequiringConfirmation = rule.Targets
            .Where(candidate => candidate.BypassProtectionConfirmed == true &&
                                !previousTargetFlags.GetValueOrDefault(
                                    ApplicationOptimizationRulePolicy.TargetIdentity(candidate)))
            .ToList();
        if (protectedTarget && configuredTarget.BypassProtectionConfirmed != true)
            targetsRequiringConfirmation.Add(configuredTarget);
        targetsRequiringConfirmation = targetsRequiringConfirmation
            .GroupBy(ApplicationOptimizationRulePolicy.TargetIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (targetsRequiringConfirmation.Count > 0)
        {
            var confirmationName = targetsRequiringConfirmation.Count == 1
                ? displayName
                : string.Join(", ", targetsRequiringConfirmation.Select(candidate => candidate.Path));
            if (!ShowRiskConfirmation(
                    T("ApplicationRuleProtectionConfirmTitle"),
                    TF("ApplicationRuleProtectionConfirmFormat", confirmationName),
                    T("CreateApplicationRule")))
            {
                return;
            }
            foreach (var candidate in targetsRequiringConfirmation)
                candidate.BypassProtectionConfirmed = true;
        }
        rule.BypassProtection = false;
        rule.ConfigurationRevision = existing is null
            ? 1
            : Math.Max(1, existing.ConfigurationRevision + 1);

        if (!TryUpdateSettings(settings =>
            {
                var updated = ApplicationOptimizationRuleSettings.Resolve(settings)
                    .Where(candidate => !string.Equals(candidate.Id, rule.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                updated.Add(rule);
                ApplicationOptimizationRuleSettings.Replace(settings, updated);
            }))
        {
            return;
        }

        _state.Status = TF("ApplicationRuleSavedFormat", displayName);
        AddHistory("ApplicationRuleSavedFormat", displayName);
        RefreshProtectedList();
        RefreshApplicationRuleList();
        UpdateProcessRows();
        UpdatePreviewRows();
    }

    private void RefreshApplicationRuleList()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = ApplicationOptimizationRuleSettings.Resolve(_settings)
            .Select(rule =>
            {
                var state = _applicationRuleStates.GetValueOrDefault(rule.Id) ??
                            new ApplicationOptimizationRuleExecutionState();
                var matches = ApplicationOptimizationRulePolicy.ResolveMatches(rule, _families);
                var targetSummary = string.Join(
                    Environment.NewLine,
                    (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
                        .Select(target => $"{ApplicationRuleTargetTypeText(target)}: {ApplicationRuleTargetSummary(target)}"));
                var triggerSummary = string.Join(
                    " · ",
                    new[]
                    {
                        rule.DelayTriggerEnabled ? T("ApplicationRuleDelayTrigger") : null,
                        rule.WorkingSetTriggerEnabled ? T("ApplicationRuleWorkingSetTrigger") : null
                    }.Where(value => value is not null));
                var skip = state.LastSkippedReason;
                if (string.IsNullOrWhiteSpace(skip) && rule.Enabled && matches.Count == 0)
                    skip = T("ApplicationRuleSkipTargetNotRunning");
                return new ApplicationRuleRow(
                    rule.Id,
                    targetSummary,
                    $"{(rule.Enabled ? T("ApplicationRuleEnabled") : T("ApplicationRuleDisabled"))} · {triggerSummary}",
                    FormatApplicationRuleNextCheck(rule, state, matches, now),
                    state.LastExecutionAt is { } last
                        ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                        : T("ApplicationRuleNever"),
                    TF("ApplicationRuleSkipFormat", skip ?? T("ApplicationRuleNoRecentSkip")),
                    rule.Enabled);
            })
            .ToArray();
        SynchronizeCollection(_state.ApplicationRules, rows);
    }

    private async void NewApplicationRule_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        var picker = new OpenFileDialog
        {
            Filter = T("ExecutableFilesFilter"),
            Title = T("ApplicationRules"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        await ConfigureApplicationRuleAsync(
            new ApplicationOptimizationRuleTarget
            {
                TargetType = ApplicationOptimizationTargetType.Executable,
                Path = picker.FileName
            },
            IOPath.GetFileNameWithoutExtension(picker.FileName),
            protectedTarget: false);
    }

    private async void NewRunningApplicationRule_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        if (!await RefreshSnapshotAsync(waitForCurrentRefresh: true) || _state.IsBusy) return;

        var existingTargets = ApplicationOptimizationRuleSettings.Resolve(_settings)
            .SelectMany(rule => rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .Select(ApplicationOptimizationRulePolicy.TargetIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applications = RunningProtectionCandidateCatalog.Create(
                _families,
                ApplicationProtectionSettings.Resolve(_settings))
            .ToArray();
        if (applications.Length == 0)
        {
            ShowThemedMessage(T("ApplicationRules"), T("RunningRulePickerEmpty"));
            return;
        }

        var targets = ShowRunningApplicationRulePicker(applications, existingTargets);
        if (targets is null || targets.Count == 0 || _state.IsBusy) return;
        var selectedTargets = new ApplicationOptimizationRule
        {
            Targets = targets.Select(CloneApplicationRuleTarget).ToList()
        };
        if (HasApplicationRuleTargetConflict(selectedTargets)) return;
        var displayName = string.Join(", ", targets.Select(target => IOPath.GetFileNameWithoutExtension(target.Path)));
        var rule = ShowApplicationOptimizationRuleDialog(
            null,
            targets[0],
            displayName,
            targets.Skip(1).ToArray());
        if (rule is null || _state.IsBusy) return;

        var bypassTargets = rule.Targets
            .Where(target => target.BypassProtectionConfirmed == true)
            .ToArray();
        if (bypassTargets.Length > 0 &&
            !ShowRiskConfirmation(
                T("ApplicationRuleProtectionConfirmTitle"),
                TF("ApplicationRuleProtectionConfirmFormat", displayName),
                T("CreateApplicationRule"))) return;

        rule.BypassProtection = false;
        rule.ConfigurationRevision = 1;
        if (!TryUpdateSettings(settings =>
            {
                var updated = ApplicationOptimizationRuleSettings.Resolve(settings).ToList();
                updated.Add(rule);
                ApplicationOptimizationRuleSettings.Replace(settings, updated);
            })) return;

        _state.Status = TF("ApplicationRuleSavedFormat", displayName);
        AddHistory("ApplicationRuleSavedFormat", displayName);
        RefreshProtectedList();
        RefreshApplicationRuleList();
        UpdateProcessRows();
        UpdatePreviewRows();
    }

    private IReadOnlyList<ApplicationOptimizationRuleTarget>? ShowRunningApplicationRulePicker(
        IReadOnlyList<RunningProtectionCandidate> applications,
        IReadOnlySet<string> existingTargetIdentities)
    {
        var selectionReaders = new List<Func<IReadOnlyList<ApplicationOptimizationRuleTarget>>>();
        var selectionBoxes = new List<System.Windows.Controls.CheckBox>();
        var panel = new StackPanel();
        foreach (var application in applications)
        {
            var familyTarget = new ApplicationOptimizationRuleTarget
            {
                TargetType = ApplicationOptimizationTargetType.ApplicationFamily,
                Path = application.ApplicationExecutablePath,
                BypassProtectionConfirmed = false
            };
            var familyAvailable = !existingTargetIdentities.Contains(
                ApplicationOptimizationRulePolicy.TargetIdentity(familyTarget));
            var availableExecutables = application.Executables
                .Select(executable => (Executable: executable, Target: new ApplicationOptimizationRuleTarget
                {
                    TargetType = ApplicationOptimizationTargetType.Executable,
                    Path = executable.ExecutablePath,
                    BypassProtectionConfirmed = false
                }))
                .Where(item => !existingTargetIdentities.Contains(
                    ApplicationOptimizationRulePolicy.TargetIdentity(item.Target)))
                .ToArray();
            if (!familyAvailable && availableExecutables.Length == 0) continue;

            var label = new StackPanel();
            label.Children.Add(new TextBlock
            {
                Text = TF(
                    "RunningProtectionApplicationFormat",
                    application.DisplayName,
                    application.ProcessCount,
                    DisplayFormat.Bytes(application.WorkingSetBytes)),
                FontWeight = FontWeights.SemiBold
            });
            label.Children.Add(new TextBlock
            {
                Text = application.ApplicationExecutablePath,
                Style = (Style)FindResource("CaptionStyle"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = application.ApplicationExecutablePath
            });
            var familyBox = new System.Windows.Controls.CheckBox
            {
                Content = label,
                IsThreeState = true,
                IsEnabled = familyAvailable,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                ToolTip = T("RunningProtectionFamilyCheckHelp")
            };
            ApplyProgrammaticCheckBoxTheme(familyBox);
            selectionBoxes.Add(familyBox);

            var childPanel = new StackPanel
            {
                Margin = new Thickness(30, 10, 0, 0),
                Visibility = Visibility.Collapsed
            };
            var childBoxes = new List<(System.Windows.Controls.CheckBox Box, ApplicationOptimizationRuleTarget Target)>();
            foreach (var item in availableExecutables)
            {
                var childLabel = new StackPanel();
                childLabel.Children.Add(new TextBlock
                {
                    Text = TF("RunningProtectionExecutableFormat", item.Executable.Name,
                        item.Executable.InstanceCount, DisplayFormat.Bytes(item.Executable.WorkingSetBytes)),
                    FontWeight = FontWeights.SemiBold
                });
                childLabel.Children.Add(new TextBlock
                {
                    Text = item.Executable.ExecutablePath,
                    Style = (Style)FindResource("CaptionStyle"),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = item.Executable.ExecutablePath
                });
                var child = new System.Windows.Controls.CheckBox
                {
                    Content = childLabel,
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
                };
                ApplyProgrammaticCheckBoxTheme(child);
                childBoxes.Add((child, item.Target));
                selectionBoxes.Add(child);
                childPanel.Children.Add(child);
            }

            var expandIcon = new TextBlock
            {
                FontFamily = new MediaFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13,
                Text = "\uE76C"
            };
            var expand = new ToggleButton
            {
                Style = (Style)FindResource("ExpandButtonStyle"),
                Content = expandIcon,
                Visibility = availableExecutables.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = T("ExpandProtectedExecutables")
            };
            expand.Checked += (_, _) =>
            {
                childPanel.Visibility = Visibility.Visible;
                expandIcon.Text = "\uE70D";
            };
            expand.Unchecked += (_, _) =>
            {
                childPanel.Visibility = Visibility.Collapsed;
                expandIcon.Text = "\uE76C";
            };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            header.Children.Add(familyBox);
            Grid.SetColumn(expand, 1);
            header.Children.Add(expand);

            var updating = false;
            familyBox.Click += (_, _) =>
            {
                if (updating || !familyAvailable) return;
                updating = true;
                var selectFamily = familyBox.IsChecked == true;
                foreach (var child in childBoxes) child.Box.IsChecked = false;
                familyBox.IsChecked = selectFamily;
                updating = false;
            };
            foreach (var child in childBoxes)
            {
                child.Box.Checked += (_, _) =>
                {
                    if (updating) return;
                    updating = true;
                    familyBox.IsChecked = null;
                    updating = false;
                };
                child.Box.Unchecked += (_, _) =>
                {
                    if (updating) return;
                    updating = true;
                    familyBox.IsChecked = childBoxes.Any(candidate => candidate.Box.IsChecked == true) ? null : false;
                    updating = false;
                };
            }
            var applicationPanel = new StackPanel();
            applicationPanel.Children.Add(header);
            applicationPanel.Children.Add(childPanel);
            panel.Children.Add(new Border
            {
                Background = (MediaBrush)FindResource("SurfaceRaisedBrush"),
                BorderBrush = (MediaBrush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = applicationPanel
            });
            selectionReaders.Add(() => familyBox.IsChecked == true
                ? new[] { CloneApplicationRuleTarget(familyTarget) }
                : childBoxes.Where(child => child.Box.IsChecked == true)
                    .Select(child => CloneApplicationRuleTarget(child.Target))
                    .ToArray());
        }

        var confirm = new Button
        {
            Content = T("RunningRulePickerConfirm"),
            MinWidth = 132,
            IsDefault = true,
            IsEnabled = false,
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        void UpdateConfirmState() => confirm.IsEnabled = selectionReaders.Any(read => read().Count > 0);
        foreach (var box in selectionBoxes)
        {
            box.Checked += (_, _) => UpdateConfirmState();
            box.Unchecked += (_, _) => UpdateConfirmState();
        }
        var cancel = new Button
        {
            Content = T("Cancel"),
            MinWidth = 88,
            IsCancel = true,
            Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("ButtonStyle")
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        var root = new DockPanel { Margin = new Thickness(22) };
        var title = new TextBlock
        {
            Text = T("RunningRulePickerTitle"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        };
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(title);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer
        {
            Content = panel,
            MaxHeight = 430,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var dialog = new Window
        {
            Owner = this,
            Title = T("RunningRulePickerTitle"),
            Width = 660,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush"),
            Content = root
        };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        ApplyDialogTheme(dialog);
        return dialog.ShowDialog() == true
            ? selectionReaders.SelectMany(read => read())
                .GroupBy(ApplicationOptimizationRulePolicy.TargetIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray()
            : null;
    }

    private void EditApplicationRule_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy || (sender as FrameworkElement)?.Tag is not string ruleId) return;
        var rule = ApplicationOptimizationRuleSettings.Resolve(_settings)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        var target = rule?.Targets?.FirstOrDefault();
        if (rule is null || target is null) return;
        var previousTargets = (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .Select(CloneApplicationRuleTarget)
            .ToArray();
        var previousTargetFlags = previousTargets.ToDictionary(
            candidate => ApplicationOptimizationRulePolicy.TargetIdentity(candidate),
            candidate => candidate.BypassProtectionConfirmed == true,
            StringComparer.OrdinalIgnoreCase);
        var edited = ShowApplicationOptimizationRuleDialog(rule, null, target.Path);
        if (edited is null || _state.IsBusy) return;
        if (HasApplicationRuleTargetConflict(edited, edited.Id)) return;

        var targetsRequiringConfirmation = edited.Targets
            .Where(candidate => candidate.BypassProtectionConfirmed == true &&
                                !previousTargetFlags.GetValueOrDefault(
                                    ApplicationOptimizationRulePolicy.TargetIdentity(candidate)))
            .GroupBy(ApplicationOptimizationRulePolicy.TargetIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (targetsRequiringConfirmation.Length > 0)
        {
            var confirmationName = targetsRequiringConfirmation.Length == 1
                ? targetsRequiringConfirmation[0].Path
                : string.Join(", ", targetsRequiringConfirmation.Select(candidate => candidate.Path));
            if (!ShowRiskConfirmation(
                    T("ApplicationRuleProtectionConfirmTitle"),
                    TF("ApplicationRuleProtectionConfirmFormat", confirmationName),
                    T("EditApplicationRule")))
            {
                return;
            }
        }

        var editedTargetsByIdentity = edited.Targets.ToDictionary(
            candidate => ApplicationOptimizationRulePolicy.TargetIdentity(candidate),
            StringComparer.OrdinalIgnoreCase);
        var affectedTargetIdentities = previousTargets
            .Where(previous =>
            {
                var identity = ApplicationOptimizationRulePolicy.TargetIdentity(previous);
                return !editedTargetsByIdentity.TryGetValue(identity, out var current) ||
                       current.BypassProtectionConfirmed != previous.BypassProtectionConfirmed;
            })
            .Select(ApplicationOptimizationRulePolicy.TargetIdentity)
            .ToArray();
        edited.ConfigurationRevision = Math.Max(1, rule.ConfigurationRevision + 1);
        edited.BypassProtection = false;
        if (!TryUpdateSettings(settings =>
            {
                var updated = ApplicationOptimizationRuleSettings.Resolve(settings)
                    .Where(candidate => !string.Equals(candidate.Id, edited.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                updated.Add(edited);
                 ApplicationOptimizationRuleSettings.Replace(settings, updated);
             })) return;
        foreach (var targetIdentity in affectedTargetIdentities)
        {
            var affectedTarget = previousTargets.FirstOrDefault(targetCandidate =>
                string.Equals(
                    ApplicationOptimizationRulePolicy.TargetIdentity(targetCandidate),
                    targetIdentity,
                    StringComparison.OrdinalIgnoreCase));
            if (affectedTarget is not null)
                _applicationRuleRuntime.ResetExecutionForTarget(rule, affectedTarget);
        }
        _state.Status = T("ApplicationRuleSavedFormat").Replace("{0}", target.Path, StringComparison.Ordinal);
        RefreshProtectedList();
        RefreshApplicationRuleList();
    }

    private void ToggleApplicationRule_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy || (sender as FrameworkElement)?.Tag is not string ruleId) return;
        var rule = ApplicationOptimizationRuleSettings.Resolve(_settings)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return;
        rule.Enabled = !rule.Enabled;
        rule.ConfigurationRevision = Math.Max(1, rule.ConfigurationRevision + 1);
        if (!TryUpdateSettings(settings => ApplicationOptimizationRuleSettings.Replace(
                settings,
                ApplicationOptimizationRuleSettings.Resolve(settings)
                    .Where(candidate => !string.Equals(candidate.Id, rule.Id, StringComparison.OrdinalIgnoreCase))
                    .Append(rule)))) return;
        if (!rule.Enabled)
        {
            _applicationRuleRuntime.ResetExecutionForRule(rule.Id);
            _applicationRuleStates.Remove(rule.Id);
        }
        RefreshProtectedList();
        RefreshApplicationRuleList();
    }

    private void DeleteApplicationRule_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy || (sender as FrameworkElement)?.Tag is not string ruleId) return;
        var rule = ApplicationOptimizationRuleSettings.Resolve(_settings)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return;
        var summary = string.Join(" + ", rule.Targets.Select(target => target.Path));
        if (!ShowRiskConfirmation(
                T("ApplicationRuleDeleteConfirmTitle"),
                TF("ApplicationRuleDeleteConfirmFormat", summary),
                T("DeleteApplicationRule"))) return;
        if (_state.IsBusy) return;
        if (!TryUpdateSettings(settings => ApplicationOptimizationRuleSettings.Remove(settings, rule.Id))) return;
        _applicationRuleRuntime.ResetExecutionForRule(rule.Id);
        _applicationRuleStates.Remove(rule.Id);
        RefreshProtectedList();
        RefreshApplicationRuleList();
    }

    private ApplicationOptimizationRule? ShowApplicationOptimizationRuleDialog(
        ApplicationOptimizationRule? existing,
        ApplicationOptimizationRuleTarget? suggestedTarget,
        string displayName,
        IReadOnlyList<ApplicationOptimizationRuleTarget>? additionalSuggestedTargets = null)
    {
        var draftTargets = (existing?.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .Where(target => target is not null)
            .Select(CloneApplicationRuleTarget)
            .ToList();
        if (suggestedTarget is not null &&
            !draftTargets.Any(target => string.Equals(
                ApplicationOptimizationRulePolicy.TargetIdentity(target),
                ApplicationOptimizationRulePolicy.TargetIdentity(suggestedTarget),
                StringComparison.OrdinalIgnoreCase)))
        {
            var newTarget = CloneApplicationRuleTarget(suggestedTarget);
            newTarget.BypassProtectionConfirmed = false;
            draftTargets.Add(newTarget);
        }
        foreach (var additionalTarget in additionalSuggestedTargets ?? Array.Empty<ApplicationOptimizationRuleTarget>())
        {
            if (draftTargets.Any(target => string.Equals(
                    ApplicationOptimizationRulePolicy.TargetIdentity(target),
                    ApplicationOptimizationRulePolicy.TargetIdentity(additionalTarget),
                    StringComparison.OrdinalIgnoreCase))) continue;
            var newTarget = CloneApplicationRuleTarget(additionalTarget);
            newTarget.BypassProtectionConfirmed = false;
            draftTargets.Add(newTarget);
        }

        var targetRows = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var targetPicker = new System.Windows.Controls.ComboBox
        {
            Style = (Style)FindResource("ThemedComboBoxStyle"),
            MinWidth = 160,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ToolTip = T("ApplicationRuleAddFamilyHelp")
        };
        AutomationProperties.SetName(targetPicker, T("ApplicationRuleAddFamily"));
        foreach (var family in _families
                     .OrderBy(family => family.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var familyPath = family.Processes
                .Select(process => process.ExecutablePath)
                .FirstOrDefault(path => ExecutablePathIdentity.TryNormalize(path, out _));
            if (!ExecutablePathIdentity.TryNormalize(familyPath, out var normalizedFamilyPath)) continue;
            if (draftTargets.Any(target =>
                    target.TargetType == ApplicationOptimizationTargetType.ApplicationFamily &&
                    string.Equals(target.Path, normalizedFamilyPath, StringComparison.OrdinalIgnoreCase))) continue;
            targetPicker.Items.Add(new ComboBoxItem
            {
                Content = $"{family.DisplayName} · {normalizedFamilyPath}",
                Tag = normalizedFamilyPath,
                ToolTip = normalizedFamilyPath
            });
        }
        if (targetPicker.Items.Count > 0) targetPicker.SelectedIndex = 0;

        bool AddTarget(ApplicationOptimizationTargetType targetType, string path)
        {
            if (!ExecutablePathIdentity.TryNormalize(path, out var normalizedPath)) return false;
            if (draftTargets.Any(target =>
                    target.TargetType == targetType &&
                    string.Equals(target.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))) return false;
            draftTargets.Add(new ApplicationOptimizationRuleTarget
            {
                TargetType = targetType,
                Path = normalizedPath,
                BypassProtectionConfirmed = false
            });
            RenderTargetRows();
            return true;
        }

        void RenderTargetRows()
        {
            targetRows.Children.Clear();
            foreach (var target in draftTargets.ToArray())
            {
                var targetText = new TextBlock
                {
                    Text = $"{ApplicationRuleTargetTypeText(target)}: {ApplicationRuleTargetSummary(target)}",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = ApplicationRuleTargetDetail(target),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var bypass = new System.Windows.Controls.CheckBox
                {
                    Content = T("ApplicationRuleBypassProtection"),
                    IsChecked = target.BypassProtectionConfirmed == true,
                    Style = (Style)FindResource("ThemedCheckBoxStyle"),
                    Margin = new Thickness(12, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = T("ApplicationRuleBypassProtectionHelp")
                };
                bypass.Checked += (_, _) => target.BypassProtectionConfirmed = true;
                bypass.Unchecked += (_, _) => target.BypassProtectionConfirmed = false;
                var remove = new Button
                {
                    Content = T("ApplicationRuleRemoveTarget"),
                    MinWidth = 70,
                    Visibility = draftTargets.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
                    Tag = target,
                    Style = (Style)FindResource("InlineActionButtonStyle"),
                    ToolTip = T("ApplicationRuleRemoveTargetHelp")
                };
                remove.Click += (_, _) =>
                {
                    draftTargets.Remove(target);
                    RenderTargetRows();
                };
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(targetText, 0);
                Grid.SetColumn(bypass, 1);
                Grid.SetColumn(remove, 2);
                row.Children.Add(targetText);
                row.Children.Add(bypass);
                row.Children.Add(remove);
                targetRows.Children.Add(row);
            }
        }

        var addFamily = new Button
        {
            Content = T("ApplicationRuleAddFamily"),
            MinWidth = 110,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("ButtonStyle"),
            ToolTip = T("ApplicationRuleAddFamilyHelp"),
            IsEnabled = targetPicker.Items.Count > 0
        };
        addFamily.Click += (_, _) =>
        {
            if (targetPicker.SelectedItem is not ComboBoxItem { Tag: string path } selected ||
                !AddTarget(ApplicationOptimizationTargetType.ApplicationFamily, path)) return;
            targetPicker.Items.Remove(selected);
            targetPicker.SelectedIndex = targetPicker.Items.Count > 0 ? 0 : -1;
            addFamily.IsEnabled = targetPicker.Items.Count > 0;
        };
        var addExecutable = new Button
        {
            Content = T("ApplicationRuleAddExecutable"),
            MinWidth = 100,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("ButtonStyle"),
            ToolTip = T("ApplicationRuleAddExecutableHelp")
        };
        addExecutable.Click += (_, _) =>
        {
            var picker = new OpenFileDialog
            {
                Filter = T("ExecutableFilesFilter"),
                Title = T("ApplicationRuleAddExecutable"),
                CheckFileExists = true,
                Multiselect = false
            };
            if (picker.ShowDialog(this) == true)
                _ = AddTarget(ApplicationOptimizationTargetType.Executable, picker.FileName);
        };
        var targetControls = new Grid
        {
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        targetControls.ColumnDefinitions.Add(new ColumnDefinition());
        targetControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(targetPicker, 0);
        Grid.SetColumn(addFamily, 1);
        Grid.SetColumn(addExecutable, 2);
        targetControls.Children.Add(targetPicker);
        targetControls.Children.Add(addFamily);
        targetControls.Children.Add(addExecutable);
        var showTargetControls = new Button
        {
            Content = T("ApplicationRuleAddTarget"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Style = (Style)FindResource("InlineActionButtonStyle")
        };
        showTargetControls.Click += (_, _) =>
        {
            targetControls.Visibility = targetControls.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        };
        var targetEditor = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        targetEditor.Children.Add(new TextBlock
        {
            Text = T("ApplicationRuleTargets"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        targetEditor.Children.Add(new ScrollViewer
        {
            Content = targetRows,
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        targetEditor.Children.Add(showTargetControls);
        targetEditor.Children.Add(targetControls);
        RenderTargetRows();

        var followAutomatic = new System.Windows.Controls.CheckBox
        {
            Content = T("ApplicationRuleFollowAutomatic"),
            IsChecked = existing?.TriggerMode == ApplicationOptimizationRuleTriggerMode.FollowAutomatic,
            Style = (Style)FindResource("ThemedCheckBoxStyle"),
            ToolTip = T("ApplicationRuleFollowAutomaticHelp")
        };
        var delayEnabled = new System.Windows.Controls.CheckBox
        {
            Content = T("ApplicationRuleDelayTrigger"),
            IsChecked = existing?.TriggerMode != ApplicationOptimizationRuleTriggerMode.FollowAutomatic &&
                        (existing?.DelayTriggerEnabled ?? false),
            Style = (Style)FindResource("ThemedCheckBoxStyle"),
            ToolTip = T("ApplicationRuleDelayTriggerHelp")
        };
        var anchor = new System.Windows.Controls.ComboBox
        {
            Style = (Style)FindResource("ThemedComboBoxStyle"),
            MinWidth = 190,
            ToolTip = T("ApplicationRuleDelayAnchorHelp")
        };
        AutomationProperties.SetName(anchor, T("ApplicationRuleDelayAnchor"));
        anchor.Items.Add(new ComboBoxItem
        {
            Content = T("ApplicationRuleMuseRamStartup"),
            Tag = ApplicationOptimizationDelayAnchor.MuseRamStartup
        });
        anchor.Items.Add(new ComboBoxItem
        {
            Content = T("ApplicationRuleTargetStartup"),
            Tag = ApplicationOptimizationDelayAnchor.TargetApplicationStartup
        });
        anchor.SelectedIndex = existing?.DelayAnchor == ApplicationOptimizationDelayAnchor.MuseRamStartup ? 0 : 1;
        var delayMinutes = RuleTextBox(existing?.DelayMinutes ?? 30, T("ApplicationRuleDelayMinutesHelp"));
        var executionCount = RuleTextBox(existing?.ExecutionCount ?? 1, T("ApplicationRuleExecutionCountHelp"));
        var executionInterval = RuleTextBox(existing?.ExecutionIntervalMinutes ?? 30, T("ApplicationRuleExecutionIntervalHelp"));
        var repeatIndefinitely = new System.Windows.Controls.CheckBox
        {
            Content = T("ApplicationRuleRepeatIndefinitely"),
            IsChecked = existing?.RepeatIndefinitely ?? false,
            Style = (Style)FindResource("ThemedCheckBoxStyle"),
            ToolTip = T("ApplicationRuleRepeatIndefinitelyHelp")
        };
        var workingSetEnabled = new System.Windows.Controls.CheckBox
        {
            Content = T("ApplicationRuleWorkingSetTrigger"),
            IsChecked = existing?.TriggerMode == ApplicationOptimizationRuleTriggerMode.FollowAutomatic ||
                        (existing?.WorkingSetTriggerEnabled ?? false),
            Style = (Style)FindResource("ThemedCheckBoxStyle"),
            ToolTip = T("ApplicationRuleWorkingSetTriggerHelp")
        };
        var followProfileThreshold = new System.Windows.Controls.CheckBox
        {
            Content = T("ApplicationRuleFollowProfileThreshold"),
            IsChecked = existing?.TriggerMode == ApplicationOptimizationRuleTriggerMode.FollowAutomatic
                ? false
                : existing?.WorkingSetThresholdFollowsProfile ?? existing is null,
            Style = (Style)FindResource("ThemedCheckBoxStyle"),
            ToolTip = T("ApplicationRuleFollowProfileThresholdHelp")
        };
        var workingSetMiB = RuleTextBox(
            (existing?.WorkingSetThresholdBytes ?? 512L * 1024 * 1024) / (1024 * 1024),
            T("ApplicationRuleWorkingSetThresholdHelp"));
        var cooldownMinutes = RuleTextBox(existing?.CooldownMinutes ?? 10, T("ApplicationRuleCooldownHelp"));
        var restartWithApplication = new System.Windows.Controls.CheckBox
        {
            Content = T("ApplicationRuleRestartWithApplication"),
            IsChecked = existing?.RestartWithApplication ?? true,
            Style = (Style)FindResource("ThemedCheckBoxStyle"),
            ToolTip = T("ApplicationRuleRestartWithApplicationHelp")
        };

        Grid CreateTriggerForm(params (string Label, FrameworkElement Editor)[] rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var index = 0; index < rows.Length; index++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = rows[index].Label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                rows[index].Editor.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(label, index);
                Grid.SetRow(rows[index].Editor, index);
                Grid.SetColumn(rows[index].Editor, 2);
                grid.Children.Add(label);
                grid.Children.Add(rows[index].Editor);
            }
            return grid;
        }

        var delayForm = CreateTriggerForm(
            (T("ApplicationRuleDelayMinutes"), delayMinutes),
            (T("ApplicationRuleDelayAnchor"), anchor),
            (T("ApplicationRuleExecutionCount"), executionCount),
            (T("ApplicationRuleExecutionInterval"), executionInterval));
        var workingSetForm = CreateTriggerForm(
            (T("ApplicationRuleWorkingSetThreshold"), workingSetMiB),
            (T("ApplicationRuleCooldown"), cooldownMinutes));

        void UpdateExecutionIntervalState()
        {
            var delayed = delayEnabled.IsChecked == true;
            var repeating = repeatIndefinitely.IsChecked == true;
            executionCount.IsEnabled = delayed && !repeating;
            executionInterval.IsEnabled = delayed &&
                (repeating ||
                 !int.TryParse(executionCount.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) ||
                 count != 1);
        }
        void UpdateRestartState()
        {
            var targetStartup = (anchor.SelectedItem as ComboBoxItem)?.Tag is ApplicationOptimizationDelayAnchor selectedAnchor &&
                                selectedAnchor == ApplicationOptimizationDelayAnchor.TargetApplicationStartup;
            restartWithApplication.IsEnabled = delayEnabled.IsChecked == true && targetStartup;
        }
        void UpdateTriggerStates()
        {
            var followsAutomatic = followAutomatic.IsChecked == true;
            var delayed = delayEnabled.IsChecked == true;
            if (followsAutomatic)
            {
                workingSetEnabled.IsChecked = true;
                followProfileThreshold.IsChecked = false;
            }
            delayForm.IsEnabled = delayed;
            repeatIndefinitely.IsEnabled = delayed;
            workingSetEnabled.IsEnabled = delayed;
            followProfileThreshold.IsEnabled = delayed && workingSetEnabled.IsChecked == true;
            workingSetMiB.IsEnabled = followsAutomatic ||
                                      delayed && workingSetEnabled.IsChecked == true &&
                                      followProfileThreshold.IsChecked != true;
            cooldownMinutes.IsEnabled = delayed && workingSetEnabled.IsChecked == true;
            UpdateExecutionIntervalState();
            UpdateRestartState();
        }
        var synchronizingTriggerModes = false;
        void SelectTriggerMode(System.Windows.Controls.CheckBox selected, System.Windows.Controls.CheckBox other)
        {
            if (synchronizingTriggerModes || selected.IsChecked != true) return;
            synchronizingTriggerModes = true;
            other.IsChecked = false;
            synchronizingTriggerModes = false;
            UpdateTriggerStates();
        }
        executionCount.TextChanged += (_, _) => UpdateExecutionIntervalState();
        repeatIndefinitely.Checked += (_, _) => UpdateExecutionIntervalState();
        repeatIndefinitely.Unchecked += (_, _) => UpdateExecutionIntervalState();
        anchor.SelectionChanged += (_, _) => UpdateRestartState();
        followAutomatic.Checked += (_, _) => SelectTriggerMode(followAutomatic, delayEnabled);
        followAutomatic.Unchecked += (_, _) => UpdateTriggerStates();
        delayEnabled.Checked += (_, _) => SelectTriggerMode(delayEnabled, followAutomatic);
        delayEnabled.Unchecked += (_, _) => UpdateTriggerStates();
        workingSetEnabled.Checked += (_, _) => UpdateTriggerStates();
        workingSetEnabled.Unchecked += (_, _) => UpdateTriggerStates();
        followProfileThreshold.Checked += (_, _) => UpdateTriggerStates();
        followProfileThreshold.Unchecked += (_, _) => UpdateTriggerStates();

        var delaySectionContent = new StackPanel();
        delaySectionContent.Children.Add(delayForm);
        repeatIndefinitely.Margin = new Thickness(0, 10, 0, 0);
        delaySectionContent.Children.Add(repeatIndefinitely);
        restartWithApplication.Margin = new Thickness(0, 10, 0, 0);
        delaySectionContent.Children.Add(restartWithApplication);
        var delaySection = new Border
        {
            Background = (MediaBrush)FindResource("SurfaceRaisedBrush"),
            BorderBrush = (MediaBrush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Child = delaySectionContent
        };

        var workingSetSectionContent = new StackPanel();
        workingSetEnabled.Margin = new Thickness(0, 0, 0, 9);
        workingSetSectionContent.Children.Add(workingSetEnabled);
        followProfileThreshold.Margin = new Thickness(0, 0, 0, 9);
        workingSetSectionContent.Children.Add(followProfileThreshold);
        workingSetSectionContent.Children.Add(workingSetForm);
        var workingSetSection = new Border
        {
            Background = (MediaBrush)FindResource("SurfaceRaisedBrush"),
            BorderBrush = (MediaBrush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Child = workingSetSectionContent
        };

        var triggerSections = new Grid();
        triggerSections.ColumnDefinitions.Add(new ColumnDefinition());
        triggerSections.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        triggerSections.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(delaySection, 0);
        Grid.SetColumn(workingSetSection, 2);
        triggerSections.Children.Add(delaySection);
        triggerSections.Children.Add(workingSetSection);
        var triggerModes = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10)
        };
        followAutomatic.Margin = new Thickness(0, 0, 24, 0);
        triggerModes.Children.Add(followAutomatic);
        triggerModes.Children.Add(delayEnabled);
        UpdateTriggerStates();
        var warning = new TextBlock
        {
            Text = T("ApplicationRuleDescription"),
            Foreground = (MediaBrush)FindResource("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 14)
        };
        var content = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        content.Children.Add(new TextBlock
        {
            Text = TF("ApplicationRuleTitleFormat", displayName),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        });
        content.Children.Add(targetEditor);
        content.Children.Add(warning);
        content.Children.Add(new TextBlock
        {
            Text = T("ApplicationRuleTriggerSettings"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(triggerModes);
        content.Children.Add(triggerSections);
        var validation = new TextBlock
        {
            Foreground = (MediaBrush)FindResource("UltimateBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 12, 0, 0)
        };
        content.Children.Add(validation);
        void ClearValidation() => validation.Visibility = Visibility.Collapsed;
        foreach (var box in new[]
                 {
                     followAutomatic, delayEnabled, repeatIndefinitely, workingSetEnabled,
                     followProfileThreshold, restartWithApplication
                 })
        {
            box.Checked += (_, _) => ClearValidation();
            box.Unchecked += (_, _) => ClearValidation();
        }
        foreach (var box in new[]
                 {
                     delayMinutes, executionCount, executionInterval, workingSetMiB, cooldownMinutes
                 })
            box.TextChanged += (_, _) => ClearValidation();
        var save = new Button
        {
            Content = T("CreateApplicationRule"),
            MinWidth = 128,
            IsDefault = true,
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        var cancel = new Button
        {
            Content = T("Cancel"),
            MinWidth = 88,
            IsCancel = true,
            Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("ButtonStyle")
        };
        var delete = new Button
        {
            Content = T("DeleteApplicationRule"),
            MinWidth = 108,
            Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible,
            Style = (Style)FindResource("DangerButtonStyle")
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(delete, 0);
        Grid.SetColumn(buttons, 2);
        footer.Children.Add(delete);
        footer.Children.Add(buttons);
        content.Children.Add(footer);
        var dialog = new Window
        {
            Owner = this,
            Title = T("ApplicationRuleTitle"),
            Width = 720,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush"),
            Content = content
        };
        ApplicationOptimizationRule? result = null;
        delete.Click += (_, _) =>
        {
            if (existing is null) return;
            var summary = string.Join(" + ", existing.Targets.Select(target => target.Path));
            if (!ShowRiskConfirmation(
                    T("ApplicationRuleDeleteConfirmTitle"),
                    TF("ApplicationRuleDeleteConfirmFormat", summary),
                    T("DeleteApplicationRule"),
                    dialog)) return;
            if (!TryUpdateSettings(settings => ApplicationOptimizationRuleSettings.Remove(settings, existing.Id))) return;
            _applicationRuleRuntime.ResetExecutionForRule(existing.Id);
            _applicationRuleStates.Remove(existing.Id);
            RefreshProtectedList();
            RefreshApplicationRuleList();
            dialog.DialogResult = false;
        };
        save.Click += (_, _) =>
        {
            void ShowValidation(string message)
            {
                validation.Text = message;
                validation.Visibility = Visibility.Visible;
            }
            var followsAutomatic = followAutomatic.IsChecked == true;
            var delayed = delayEnabled.IsChecked == true;
            if (!followsAutomatic && !delayed)
            {
                ShowValidation(T("ApplicationRuleTriggerRequired"));
                return;
            }
            if (!TryRuleInt(delayMinutes, 1, 1440, out var delay) ||
                !TryRuleInt(executionCount, 1, 10, out var count) ||
                !TryRuleInt(executionInterval, 1, 1440, out var interval) ||
                !TryRuleInt(cooldownMinutes, 1, 1440, out var cooldown) ||
                ((followsAutomatic || workingSetEnabled.IsChecked == true) &&
                 followProfileThreshold.IsChecked != true &&
                 !TryRuleInt(workingSetMiB, 1, 1024 * 1024, out _)))
            {
                ShowValidation(T("ApplicationRuleInvalidValue"));
                return;
            }
            if (draftTargets.Count == 0)
            {
                ShowValidation(T("ApplicationRuleTargetRequired"));
                return;
            }
            if (draftTargets.SelectMany((target, index) => draftTargets
                    .Skip(index + 1)
                    .Select(other => (target, other)))
                .Any(pair => ApplicationOptimizationRulePolicy.TargetsOverlap(pair.target, pair.other)))
            {
                ShowValidation(T("ApplicationRuleTargetConflictInRule"));
                return;
            }
            var thresholdMiB = int.TryParse(
                workingSetMiB.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedThresholdMiB)
                ? Math.Clamp(parsedThresholdMiB, 1, 1024 * 1024)
                : 512;
            result = existing ?? new ApplicationOptimizationRule();
            result.Enabled = existing?.Enabled ?? true;
            result.Targets = draftTargets.Select(CloneApplicationRuleTarget).ToList();
            result.TriggerMode = followsAutomatic
                ? ApplicationOptimizationRuleTriggerMode.FollowAutomatic
                : ApplicationOptimizationRuleTriggerMode.Delayed;
            result.DelayTriggerEnabled = delayed;
            result.DelayAnchor = (anchor.SelectedItem as ComboBoxItem)?.Tag is ApplicationOptimizationDelayAnchor selectedAnchor
                ? selectedAnchor
                : ApplicationOptimizationDelayAnchor.TargetApplicationStartup;
            result.DelayMinutes = delay;
            result.ExecutionCount = count;
            result.ExecutionIntervalMinutes = interval;
            result.RepeatIndefinitely = delayed && repeatIndefinitely.IsChecked == true;
            result.RestartWithApplication = delayed && restartWithApplication.IsChecked == true;
            result.WorkingSetTriggerEnabled = followsAutomatic || workingSetEnabled.IsChecked == true;
            result.WorkingSetThresholdFollowsProfile = delayed &&
                                                       workingSetEnabled.IsChecked == true &&
                                                       followProfileThreshold.IsChecked == true;
            result.WorkingSetThresholdBytes = checked((long)thresholdMiB * 1024 * 1024);
            result.CooldownMinutes = cooldown;
            result.BypassProtection = false;
            dialog.DialogResult = true;
        };
        ApplyDialogTheme(dialog);
        _ = dialog.ShowDialog();
        return result;
    }

    private System.Windows.Controls.TextBox RuleTextBox(object value, string? toolTip = null) => new()
    {
        Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        Style = (Style)FindResource("EditorTextBoxStyle"),
        MinWidth = 150,
        ToolTip = toolTip
    };

    private static ApplicationOptimizationRuleTarget CloneApplicationRuleTarget(
        ApplicationOptimizationRuleTarget target) => new()
    {
        TargetType = target.TargetType,
        Path = target.Path,
        ExecutablePaths = target.ExecutablePaths?.ToList() ?? new List<string>(),
        BypassProtectionConfirmed = target.BypassProtectionConfirmed
    };

    private string ApplicationRuleTargetTypeText(ApplicationOptimizationRuleTarget target) => target.TargetType switch
    {
        ApplicationOptimizationTargetType.ApplicationFamily => T("ApplicationRuleTargetFamily"),
        ApplicationOptimizationTargetType.ExecutableGroup =>
            TF("ApplicationRuleTargetProtectedGroupFormat", target.ExecutablePaths.Count),
        _ => T("ApplicationRuleTargetExecutable")
    };

    private static string ApplicationRuleTargetSummary(ApplicationOptimizationRuleTarget target) =>
        target.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
            ? string.Join(" + ", target.ExecutablePaths.Select(IOPath.GetFileName))
            : target.Path;

    private static string ApplicationRuleTargetDetail(ApplicationOptimizationRuleTarget target) =>
        target.TargetType == ApplicationOptimizationTargetType.ExecutableGroup
            ? string.Join(Environment.NewLine, target.ExecutablePaths)
            : target.Path;

    private bool HasApplicationRuleTargetConflict(
        ApplicationOptimizationRule candidate,
        string? excludedRuleId = null)
    {
        var existingTargets = ApplicationOptimizationRuleSettings.Resolve(_settings)
            .Where(rule => string.IsNullOrWhiteSpace(excludedRuleId) ||
                           !string.Equals(rule.Id, excludedRuleId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(rule => rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .ToArray();
        var conflictingTarget = (candidate.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .FirstOrDefault(target => existingTargets.Any(existing =>
                ApplicationOptimizationRulePolicy.TargetsOverlap(target, existing)));
        if (conflictingTarget is null) return false;
        ShowThemedMessage(
            T("ApplicationRules"),
            TF("ApplicationRuleTargetConflictFormat", ApplicationRuleTargetSummary(conflictingTarget)));
        return true;
    }

    private static bool TryRuleInt(System.Windows.Controls.TextBox box, int minimum, int maximum, out int value) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
        value >= minimum && value <= maximum;

    private bool ShowSelectedApplicationOptimizationPrompt(
        string applicationName,
        bool bypassProtection = false)
    {
        var confirmed = false;
        var icon = new TextBlock
        {
            Text = "\uE7BA",
            FontFamily = new MediaFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 24,
            Foreground = (MediaBrush)FindResource("WarningBrush"),
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        };
        var message = new TextBlock
        {
            Text = TF(
                bypassProtection
                    ? "ProtectedOptimizationPromptFormat"
                    : "SelectedOptimizationPromptFormat",
                applicationName),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Foreground = (MediaBrush)FindResource("TextBrush")
        };
        var suppress = new System.Windows.Controls.CheckBox
        {
            Content = T("SelectedOptimizationDoNotRemind"),
            Style = (Style)FindResource("ThemedCheckBoxStyle"),
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button
        {
            Content = T("Cancel"),
            MinWidth = 92,
            IsCancel = true,
            IsDefault = true,
            Style = (Style)FindResource("ButtonStyle")
        };
        var optimize = new Button
        {
            Content = T("OptimizeThisApplication"),
            MinWidth = 128,
            Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(optimize);
        var details = new StackPanel();
        details.Children.Add(message);
        details.Children.Add(suppress);
        details.Children.Add(buttons);
        var root = new Grid { Margin = new Thickness(24) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(icon);
        Grid.SetColumn(details, 2);
        root.Children.Add(details);
        var dialog = new Window
        {
            Owner = this,
            Title = T("SelectedOptimizationPromptTitle"),
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush"),
            Content = root
        };
        optimize.Click += (_, _) =>
        {
            confirmed = true;
            if (suppress.IsChecked == true)
            {
                _ = TryUpdateSettings(settings =>
                    settings.SelectedApplicationOptimizationPromptSuppressed = true);
            }
            dialog.DialogResult = true;
        };
        ApplyDialogTheme(dialog);
        _ = dialog.ShowDialog();
        return confirmed;
    }

    private async Task RunOptimizationAsync(
        bool manual,
        bool scheduled = false,
        bool snapshotAlreadyRefreshed = false,
        ProcessRow? target = null,
        bool longIdle = false,
        ProtectedOptimizationTarget? protectedTarget = null,
        ApplicationOptimizationRule? applicationRule = null)
    {
        if (_state.IsBusy) return;
        var runId = Guid.NewGuid().ToString("N");
        var runStartedAt = DateTimeOffset.UtcNow;
        var runContext = CreateOptimizationRunContext(
            _settings,
            manual,
            scheduled,
            longIdle,
            applicationRule is not null,
            runId);
        var unattended = !manual || scheduled;
        var busyStateEntered = false;
        OptimizationResourceSampler? resourceSampler = null;
        OptimizationResourceSample? resourceSample = null;
        var snapshotMilliseconds = 0d;
        var planningMilliseconds = 0d;
        var executionMilliseconds = 0d;
        var completionMilliseconds = 0d;
        var maximumUiDispatchDelayMilliseconds = 0d;
        var candidateCount = 0;
        var targetProcessCount = 0;
        var succeeded = 0;
        var skipped = 0;
        var failed = 0;
        MemorySnapshot? telemetryBefore = null;
        MemorySnapshot? telemetryAfter = null;
        long? executionStarted = null;
        long? completionStarted = null;
        var startAutomaticSafetyWindow = false;
        var recordOptimizationRun = false;
        if (!unattended)
        {
            SetBusyState(true);
            busyStateEntered = true;
            _state.Status = T("OptimizationPreparing");
            await Dispatcher.Yield(DispatcherPriority.Render);
        }
        try
        {
            var snapshotStarted = Stopwatch.GetTimestamp();
            if (!snapshotAlreadyRefreshed &&
                !await RefreshSnapshotAsync(waitForCurrentRefresh: true)) return;
            snapshotMilliseconds = Stopwatch.GetElapsedTime(snapshotStarted).TotalMilliseconds;
            var before = _currentMemory;
            telemetryBefore = before;
            var runSettings = _settings;
            var settings = runSettings.ResolveOptimizationSettings(manual);
            settings = settings with
            {
                EnhancedSafety = runSettings.EnhancedSafety,
                IntelligentCandidateSelection = runSettings.IntelligentCandidateSelection,
                QuickCandidateSelection = !longIdle && (!manual || scheduled) && runSettings.QuickCandidateSelection,
                IgnoreMemoryPressureThreshold = longIdle || settings.IgnoreMemoryPressureThreshold
            };
            var planningSettings = longIdle
                ? settings with { MaxApplications = 0 }
                : settings;
            var planningStarted = Stopwatch.GetTimestamp();
            var targetFamilies = target is not null
                ? ResolveCurrentTargetFamily(target) is { } selectedFamily
                    ? new[] { selectedFamily }
                    : Array.Empty<ProcessFamilySnapshot>()
                : protectedTarget is not null
                    ? ResolveProtectedTargetFamilies(protectedTarget)
                    : Array.Empty<ProcessFamilySnapshot>();
            var targetDisplayName = target?.Name ?? protectedTarget?.DisplayName;
            if ((target is not null || protectedTarget is not null) && targetFamilies.Length == 0)
            {
                _state.Status = T("SelectedApplicationNoLongerRunning");
                return;
            }
            if (targetFamilies.Any(family => family.HasForegroundProcess))
            {
                if (runSettings.EnhancedSafety)
                {
                    _state.Status = T("ForegroundOptimizationEnhancedSafetyBlocked");
                    return;
                }
                if (!ShowRiskConfirmation(
                        T("ForegroundOptimizationConfirmTitle"),
                        TF("ForegroundOptimizationConfirmFormat", targetDisplayName),
                        T("OptimizeThisApplication")))
                {
                    return;
                }
                settings = settings with { AllowForegroundProcessTrim = true };
            }
            if (targetFamilies.Length > 0)
                settings = SelectedApplicationOptimizationPolicy.Apply(settings);
            var reboundSettings = runSettings.ResolveReboundSettings();
            var planNow = DateTimeOffset.Now;
            var learningFilters = CurrentLearningFilters(planNow);
            runContext = CreateOptimizationRunContext(
                runSettings,
                manual,
                scheduled,
                longIdle,
                applicationRule is not null,
                runId);
            var plan = applicationRule is not null
                ? CreateApplicationRulePlan(applicationRule, planningSettings, planNow)
                : _planner.CreatePlan(
                    before,
                    _families,
                    planningSettings,
                    protectedTarget is null
                        ? CurrentProtectionRules()
                        : new ProtectionRules(Array.Empty<ApplicationProtectionRule>()),
                    _lastTrimTimes,
                    planNow,
                    manual,
                    _activity,
                    automaticBackoffFamilies: null,
                    outcomeMultipliers: _applicationBackoffTracker.OutcomeMultipliers,
                    learningConfidences: _applicationBackoffTracker.LearningConfidences,
                    candidateIdleReadiness: _candidateIdleReadiness,
                    enforceUnattendedSafety: scheduled || longIdle,
                    pendingReboundObservationFamilies: null,
                    lastTrimProcessStartTimes: _lastTrimProcessStartTimes,
                    automaticBackoffComponents: learningFilters.BlockedComponents,
                    pendingReboundObservationComponents: learningFilters.PendingComponents,
                    stableSuppressedComponents: learningFilters.StableComponents,
                    automaticThresholdOverrides: !manual && !scheduled && !longIdle
                        ? ApplicationOptimizationRulePolicy.CreateAutomaticThresholdOverrides(
                            ApplicationOptimizationRuleSettings.Resolve(_settings),
                            _families,
                            _settings.AutoOptimization)
                        : null);
            if (longIdle)
            {
                var minimumIdle = TimeSpan.FromMinutes(
                    LongIdleOptimizationPolicy.NormalizeMinutes(runSettings.LongIdleOptimizationMinutes));
                plan = CandidatePlanCalibrationPolicy.ApplyLongIdleFilter(
                    plan,
                    _activity,
                    minimumIdle,
                    settings.MaxApplications);
            }
            if (targetFamilies.Length > 0)
            {
                var targetFamilyKeys = targetFamilies
                    .Select(family => family.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var targetExecutablePaths = protectedTarget?.ExecutablePaths
                    .Select(NormalizeExecutablePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var targetCandidates = plan.Candidates
                    .Where(candidate => targetFamilyKeys.Contains(candidate.Family.Key))
                    .Select(candidate =>
                    {
                        if (targetExecutablePaths is null) return candidate;
                        var targetProcesses = candidate.TargetProcesses
                            .Where(process => !string.IsNullOrWhiteSpace(process.ExecutablePath) &&
                                targetExecutablePaths.Contains(
                                    NormalizeExecutablePath(process.ExecutablePath)))
                            .ToArray();
                        return targetProcesses.Length == 0
                            ? null
                            : candidate with
                            {
                                TargetProcesses = targetProcesses,
                                PotentialReleaseBytes = targetProcesses.Sum(process =>
                                    Math.Max(0, process.WorkingSetBytes))
                            };
                    })
                    .Where(candidate => candidate is not null)
                    .Select(candidate => candidate!)
                    .ToArray();
                plan = plan with
                {
                    ShouldRun = targetCandidates.Length > 0,
                    Candidates = targetCandidates,
                    Outcome = targetCandidates.Length == 0
                        ? OptimizationPlanOutcome.NoCandidates
                        : OptimizationPlanOutcome.CandidatesFound,
                    CandidateEvaluations = plan.CandidateEvaluations.Where(evaluation =>
                        targetFamilyKeys.Contains(evaluation.FamilyKey)).ToArray()
                };
            }
            planningMilliseconds = Stopwatch.GetElapsedTime(planningStarted).TotalMilliseconds;
            candidateCount = plan.Candidates.Count;
            targetProcessCount = plan.Candidates.Sum(candidate => candidate.TargetProcesses.Count);
            RecordCandidateCalibration(plan, settings, runContext, before);
            RecordLargeMemoryOpportunityIfDue(plan, settings, runContext, before, planNow);
            if (!plan.ShouldRun)
            {
                if (applicationRule is not null)
                    RecordApplicationRuleSkip(applicationRule, FormatApplicationRuleSkipReason(plan));
                _state.Status = FormatPlan(plan);
                _lastOptimizationResult = null;
                _lastResultFallbackKey = "NoAction";
                _state.LastResult = T("NoAction");
                if (manual && !scheduled)
                {
                    AddHistory(
                        plan.Outcome switch
                        {
                            OptimizationPlanOutcome.LowMemoryPressure => "PlanLowPressure",
                            OptimizationPlanOutcome.NoCandidates => "PlanNoCandidates",
                            _ => "PlanCandidatesFormat"
                        },
                        plan.Candidates.Count);
                    if (plan.Outcome == OptimizationPlanOutcome.NoCandidates)
                        ShowNoCandidatesDialog(targetFamilies.Length == 0
                            ? null
                            : FormatSelectedApplicationExclusion(
                                plan,
                                targetDisplayName ?? targetFamilies[0].DisplayName));
                }
                return;
            }
            if (!busyStateEntered)
            {
                if (_state.IsBusy) return;
                SetBusyState(true);
                busyStateEntered = true;
            }
            recordOptimizationRun = runSettings.DiagnosticDataCollectionEnabled;
            if (recordOptimizationRun)
            {
                _activeOptimizationRunId = runId;
                try
                {
                    resourceSampler = OptimizationResourceSampler.Start();
                }
                catch (Exception exception)
                {
                    _diagnosticLog.Warning("Unable to start optimization resource sampling.", exception);
                }
            }
            startAutomaticSafetyWindow = applicationRule is null &&
                                         AutomaticOptimizationSafetyWindow.ShouldStart(manual, scheduled);

            long workingSetReduction = 0;
            var reboundStartedAt = DateTimeOffset.Now;
            var historyFormatKey = applicationRule is not null
                ? "HistoryApplicationRuleOptimizationFormat"
                : scheduled
                ? "HistoryScheduledOptimizationFormat"
                : longIdle
                    ? "HistoryLongIdleOptimizationFormat"
                : manual
                    ? "HistoryManualOptimizationFormat"
                    : "HistoryAutomaticOptimizationFormat";
            var reboundRunKind = applicationRule is not null
                ? OptimizationRunKind.ApplicationRule
                : longIdle
                ? OptimizationRunKind.LongIdle
                : scheduled
                    ? OptimizationRunKind.Scheduled
                    : manual
                        ? OptimizationRunKind.Manual
                        : OptimizationRunKind.Automatic;
            var sampledProcesses = _families.SelectMany(family => family.Processes).ToArray();
            var processIndex = 0;
            var successfulApplicationRuleProcesses = new List<ProcessSnapshot>();
            var applicationRuleReleasedBytesByProcess = new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase);
            executionStarted = Stopwatch.GetTimestamp();
            _state.Status = TF("OptimizationProgressFormat", processIndex, targetProcessCount);
            await Dispatcher.Yield(DispatcherPriority.Render);
            foreach (var candidate in plan.Candidates)
            {
                long familyBefore = 0;
                long familyAfter = 0;
                var familyHasReliableMeasurement = false;
                var succeededProcessIds = new List<int>();
                var componentResults = new Dictionary<string, ComponentTrimAccumulator>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var process in candidate.TargetProcesses)
                {
                    processIndex++;
                    var safetyScope = ProcessRelationshipPolicy.BuildSafetyScope(
                        process.ProcessId,
                        sampledProcesses);
                    var result = await _trimmer.TrimAsync(
                        process,
                        safetyScope,
                        enhancedSafety: settings.EnhancedSafety,
                        allowForegroundProcessTrim: settings.AllowForegroundProcessTrim);
                    var activityAssessment = _activity.GetValueOrDefault(candidate.Family.Key);
                    _state.Status = TF("OptimizationProgressFormat", processIndex, targetProcessCount);
                    var uiDispatchStarted = Stopwatch.GetTimestamp();
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    var uiDispatchDelayMilliseconds = Stopwatch.GetElapsedTime(uiDispatchStarted).TotalMilliseconds;
                    maximumUiDispatchDelayMilliseconds = Math.Max(
                        maximumUiDispatchDelayMilliseconds,
                        uiDispatchDelayMilliseconds);
                    var processMetric = new OptimizationProcessCalibrationMetric(
                        runContext,
                        runId,
                        CurrentBuildId,
                        DateTimeOffset.UtcNow,
                        string.Empty,
                        processIndex,
                        targetProcessCount,
                        result.Success,
                        result.Skipped,
                        result.SetProcessWorkingSetSucceeded,
                        result.SetProcessWorkingSetErrorCode,
                        result.EmptyWorkingSetSucceeded,
                        result.EmptyWorkingSetErrorCode,
                        candidate.Family.IdleConfidenceScore,
                        activityAssessment?.State.ToString() ?? BackgroundActivityState.Observing.ToString(),
                        Math.Max(0, activityAssessment?.IdleFor.TotalSeconds ?? 0),
                        process.IsForeground,
                        process.HasVisibleWindow,
                        safetyScope.Count,
                        result.WorkingSetBeforeBytes,
                        result.WorkingSetAfterBytes,
                        result.PageFaultCountDelta,
                        result.TotalMilliseconds,
                        result.OpenProcessMilliseconds,
                        result.IdentityCheckMilliseconds,
                        result.RelationshipCheckMilliseconds,
                        result.SetProcessWorkingSetMilliseconds,
                        result.EmptyWorkingSetMilliseconds,
                        result.MeasurementMilliseconds,
                        uiDispatchDelayMilliseconds);
                    var familyKey = candidate.Family.Key;
                    QueueCalibrationWrite(() =>
                        _calibrationMetricsStore.AppendOptimizationProcess(familyKey, processMetric));
                    if (result.Skipped)
                    {
                        skipped++;
                        _diagnosticLog.Info($"Working-set trim skipped for PID {process.ProcessId}: {result.Error}");
                        continue;
                    }

                    if (!result.Success)
                    {
                        failed++;
                        _diagnosticLog.Warning($"Working-set trim failed for PID {process.ProcessId}: {result.Error}");
                        continue;
                    }
                    if (succeeded == 0) BeginApplicationReboundRun(reboundStartedAt, reboundRunKind);
                    succeeded++;
                    if (applicationRule is not null)
                        successfulApplicationRuleProcesses.Add(process);
                    if (process.StartTimeFileTimeUtc is { } startTimeFileTimeUtc)
                    {
                        _lastTrimTimes[process.ProcessId] = DateTimeOffset.Now;
                        _lastTrimProcessStartTimes[process.ProcessId] = startTimeFileTimeUtc;
                    }
                    if (!string.IsNullOrWhiteSpace(result.Warning))
                    {
                        _diagnosticLog.Warning($"Working-set trim warning for PID {process.ProcessId}: {result.Warning}");
                    }
                    if (!result.HasReliableWorkingSetMeasurement)
                    {
                        _diagnosticLog.Warning($"Working-set trim for PID {process.ProcessId} had no reliable before/after measurement.");
                        continue;
                    }
                    familyHasReliableMeasurement = true;
                    if (applicationRule is not null)
                    {
                        applicationRuleReleasedBytesByProcess[
                            ApplicationOptimizationRuleRuntime.ProcessIdentity(process)] =
                            Math.Max(0, result.WorkingSetReductionBytes);
                    }
                    succeededProcessIds.Add(process.ProcessId);
                    familyBefore += Math.Max(0, result.WorkingSetBeforeBytes);
                    familyAfter += Math.Max(0, result.WorkingSetAfterBytes);
                    var componentKey = ApplicationComponentIdentity.ForProcess(
                        candidate.Family.Key,
                        process);
                    if (!componentResults.TryGetValue(componentKey, out var component))
                    {
                        component = new ComponentTrimAccumulator(
                            componentKey,
                            ExecutablePathIdentity.TryNormalize(process.ExecutablePath, out var componentPath)
                                ? componentPath
                                : null);
                        componentResults.Add(componentKey, component);
                    }
                    component.WorkingSetBeforeBytes += Math.Max(0, result.WorkingSetBeforeBytes);
                    component.WorkingSetAfterBytes += Math.Max(0, result.WorkingSetAfterBytes);
                    component.ProcessIds.Add(process.ProcessId);
                    if (process.StartTimeFileTimeUtc is { } componentStartTime)
                        component.ProcessStartTimes.Add(componentStartTime);
                    workingSetReduction += result.WorkingSetReductionBytes;
                }
                if (familyHasReliableMeasurement)
                {
                    var releasedBytes = Math.Max(0, familyBefore - familyAfter);
                    _applicationReboundDetailTracker.Track(
                        candidate.Family.Key,
                        candidate.Family.DisplayName,
                        familyAfter,
                        releasedBytes,
                        succeededProcessIds,
                        candidate.Family.Processes.Select(process => process.ProcessId).ToArray(),
                        componentResults.Values
                            .Select(component => component.ExecutablePath)
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .Cast<string>()
                            .ToArray());
                    SynchronizeReboundRunHistory(DateTimeOffset.Now);
                    foreach (var component in componentResults.Values)
                    {
                        _applicationBackoffTracker.BeginComponent(
                            candidate.Family.Key,
                            component.ComponentKey,
                            component.ExecutablePath,
                            component.WorkingSetBeforeBytes,
                            component.WorkingSetAfterBytes,
                            reboundSettings,
                            DateTimeOffset.Now,
                            learnOutcome: applicationRule is null &&
                                          (!manual || scheduled) &&
                                          settings.IntelligentCandidateSelection,
                            wasForegroundBeforeTrim: candidate.TargetProcesses.Any(process =>
                                component.ProcessIds.Contains(process.ProcessId) && process.IsForeground),
                            targetProcessIds: component.ProcessIds,
                            runContext: runContext,
                            baselineFamilyProcessIds: candidate.Family.Processes
                                .Select(process => process.ProcessId)
                                .ToArray(),
                            launchSignature: component.ProcessStartTimes.Count == 0
                                ? null
                                : string.Join(',', component.ProcessStartTimes.OrderBy(value => value)));
                    }
                }
            }
            SaveReboundHistoryIfDue(DateTimeOffset.Now, force: true);
            executionMilliseconds = Stopwatch.GetElapsedTime(executionStarted.Value).TotalMilliseconds;
            executionStarted = null;
            if (applicationRule is not null)
                RecordApplicationRuleExecution(
                    applicationRule,
                    plan,
                    successfulApplicationRuleProcesses,
                    workingSetReduction,
                    reboundStartedAt,
                    applicationRuleReleasedBytesByProcess);
            if (succeeded > 0) _lastSuccessfulOptimizationAt = DateTimeOffset.UtcNow;
            completionStarted = Stopwatch.GetTimestamp();

            if (!OptimizationResultAttributionPolicy.CanAttributeSystemMemoryChange(succeeded))
            {
                _lastOptimizationResult = null;
                _lastResultFallbackKey = "OptimizationNoSuccessfulRequests";
                _state.LastResult = T(_lastResultFallbackKey);
                _state.RecentTrim = "--";
                _state.BoostNetGain = "--";
                _state.Status = TF("OptimizationRunStatusFormat", plan.Candidates.Count, succeeded, skipped, failed);
                _diagnosticLog.Warning(
                    $"Optimization completed without a successful trim request; skipped {skipped}, failed {failed}.");
                AddHistoryNested(historyFormatKey, _lastResultFallbackKey, Array.Empty<object?>());
                return;
            }

            var postTrimDelay = EnhancedSafetyBehavior.PostTrimSamplingDelay(settings.EnhancedSafety);
            if (postTrimDelay > TimeSpan.Zero) await Task.Delay(postTrimDelay);
            if (!_memoryStatus.TryGetSnapshot(out var after))
            {
                _state.Status = T("OptimizationAfterUnavailable");
                _diagnosticLog.Warning("Optimization completed without an after-memory snapshot.");
                return;
            }
            telemetryAfter = after;
            var netAvailable = checked((long)after.AvailablePhysicalBytes - (long)before.AvailablePhysicalBytes);
            _cumulativeTrimBytes = checked(_cumulativeTrimBytes + workingSetReduction);
            _cumulativeNetGainBytes = checked(_cumulativeNetGainBytes + netAvailable);
            _state.RecentTrim = FormatMetricBytes(workingSetReduction);
            _state.CumulativeTrim = FormatMetricBytes(_cumulativeTrimBytes);
            _state.BoostNetGain = FormatMetricBytes(netAvailable);
            _state.CumulativeNetGain = FormatMetricBytes(_cumulativeNetGainBytes);
            _reboundTracker.Begin(before, after, reboundStartedAt);
            UpdateApplicationReboundSummary();
            _lastOptimizationResult = new OptimizationResultDisplay(workingSetReduction, netAvailable);
            _state.LastResult = FormatOptimizationResult(_lastOptimizationResult);
            _state.Status = TF("OptimizationRunStatusFormat", plan.Candidates.Count, succeeded, skipped, failed);
            AddHistoryNested(
                historyFormatKey,
                "OptimizationResultFormat",
                new object?[] { DisplayFormat.Bytes(workingSetReduction), SignedBytes(netAvailable) });
            if (runSettings.ScheduledOptimizationEnabled) _scheduledOptimizationAnchor = DateTimeOffset.UtcNow;
            UpdateMetrics(after);
            completionMilliseconds = Stopwatch.GetElapsedTime(completionStarted.Value).TotalMilliseconds;
            completionStarted = null;
        }
        finally
        {
            if (executionStarted.HasValue)
                executionMilliseconds = Stopwatch.GetElapsedTime(executionStarted.Value).TotalMilliseconds;
            if (completionStarted.HasValue)
                completionMilliseconds = Stopwatch.GetElapsedTime(completionStarted.Value).TotalMilliseconds;
            try
            {
                if (resourceSampler is not null)
                    resourceSample = await resourceSampler.StopAsync();
            }
            catch (Exception exception)
            {
                _diagnosticLog.Warning("Unable to complete optimization resource sampling.", exception);
            }
            if (recordOptimizationRun)
            {
                var completedAt = DateTimeOffset.UtcNow;
                var beforeMetric = telemetryBefore ?? _currentMemory;
                var runMetric = new OptimizationRunCalibrationMetric(
                    runContext,
                    runId,
                    CurrentBuildId,
                    runStartedAt,
                    completedAt,
                    snapshotAlreadyRefreshed,
                    candidateCount,
                    targetProcessCount,
                    succeeded,
                    skipped,
                    failed,
                    beforeMetric.LoadPercent,
                    beforeMetric.AvailablePhysicalBytes,
                    telemetryAfter?.LoadPercent,
                    telemetryAfter?.AvailablePhysicalBytes,
                    snapshotMilliseconds,
                    planningMilliseconds,
                    executionMilliseconds,
                    completionMilliseconds,
                    maximumUiDispatchDelayMilliseconds,
                    resourceSample?.AppAverageCpuPercent,
                    resourceSample?.AppPeakCpuPercent,
                    resourceSample?.SystemPageFaultCountDelta,
                    resourceSample?.SystemPageReadCountDelta,
                    resourceSample?.SystemPageReadIoCountDelta);
                QueueCalibrationWrite(() => _calibrationMetricsStore.AppendOptimizationRun(runMetric));
            }
            if (string.Equals(_activeOptimizationRunId, runId, StringComparison.Ordinal))
                _activeOptimizationRunId = null;
            if (startAutomaticSafetyWindow)
                _automaticOptimizationSafetyAnchor = DateTimeOffset.Now;
            if (busyStateEntered) SetBusyState(false);
            UpdateMonitoringInterval();
        }
    }

    private string FormatSelectedApplicationExclusion(
        OptimizationPlan plan,
        string applicationName)
    {
        var reasons = plan.CandidateEvaluations
            .SelectMany(evaluation => evaluation.ExclusionReasons)
            .ToHashSet();
        if (reasons.Contains(CandidateExclusionReason.Protected))
            return TF("SelectedOptimizationProtectedFormat", applicationName);
        if (reasons.Contains(CandidateExclusionReason.UnreliableActivitySample))
            return TF("SelectedOptimizationSamplingFormat", applicationName);
        if (reasons.Contains(CandidateExclusionReason.BelowFamilyWorkingSet))
            return TF("SelectedOptimizationProcessableWorkingSetInsufficientFormat", applicationName);
        if (reasons.Contains(CandidateExclusionReason.Foreground) ||
            reasons.Contains(CandidateExclusionReason.CurrentCpuActivity) ||
            reasons.Contains(CandidateExclusionReason.CurrentIoActivity) ||
            reasons.Contains(CandidateExclusionReason.ActiveProcessRelationship))
        {
            return TF("SelectedOptimizationActiveFormat", applicationName);
        }
        return TF("SelectedOptimizationSafetyFormat", applicationName);
    }

    private void ShowNoCandidatesDialog(string? messageText = null)
    {
        var title = new TextBlock
        {
            Text = T("NoCandidatesDialogTitle"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = (MediaBrush)FindResource("TextBrush")
        };
        var titleBar = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        titleBar.Children.Add(title);
        var message = new TextBlock
        {
            Text = messageText ?? T("NoCandidatesDialogMessage"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (MediaBrush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 14, 0, 20),
            LineHeight = 21
        };
        var confirm = new Button
        {
            Content = T("NoCandidatesDialogConfirm"),
            MinWidth = 108,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            IsDefault = true,
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(titleBar);
        content.Children.Add(message);
        content.Children.Add(confirm);
        var frame = new Border
        {
            Background = (MediaBrush)FindResource("SurfaceBrush"),
            BorderBrush = (MediaBrush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content
        };
        var dialog = new Window
        {
            Owner = this,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Content = frame
        };
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) dialog.DragMove();
        };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) dialog.Close();
        };
        ApplyDialogTheme(dialog);
        _ = dialog.ShowDialog();
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e) => await RefreshSnapshotAsync();

    private void SchedulePopup_OnClosed(object? sender, EventArgs e)
    {
    }

    private void ScheduleMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ConsumeSuppressedPopupTriggerClick(sender)) return;
        if (IsScheduledOptimizationUnavailable()) return;
        CandidateModePopup.IsOpen = false;
        CandidateDisplayPopup.IsOpen = false;
        SchedulePopup.IsOpen = !SchedulePopup.IsOpen;
    }

    private void CandidateModePopup_OnClosed(object? sender, EventArgs e)
    {
    }

    private void UpdateFrequencyPopup_OnClosed(object? sender, EventArgs e)
    {
    }

    private void UpdateFrequencyMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ConsumeSuppressedPopupTriggerClick(sender)) return;
        CloseManagedPopups(UpdateFrequencyPopup);
        UpdateFrequencyPopup.IsOpen = !UpdateFrequencyPopup.IsOpen;
    }

    private void UpdateFrequencyOption_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } ||
            !Enum.TryParse<UpdateCheckFrequency>(value, out var frequency)) return;
        if (!TryUpdateSettings(settings => settings.UpdateCheckFrequency = frequency)) return;
        UpdateFrequencyPopup.IsOpen = false;
        SynchronizeUpdateFrequencyPresentation();
    }

    private void SynchronizeUpdateFrequencyPresentation()
    {
        var key = _settings.UpdateCheckFrequency switch
        {
            UpdateCheckFrequency.Daily => "UpdateDaily",
            UpdateCheckFrequency.Weekly => "UpdateWeekly",
            UpdateCheckFrequency.ManualOnly => "UpdateManualOnly",
            _ => "UpdateEveryStartup"
        };
        UpdateFrequencyMenuButton.ToolTip = $"{T("UpdateFrequency")}: {T(key)}";
        if (UpdateFrequencyPopup.Child is Border { Child: StackPanel panel })
        {
            foreach (var button in panel.Children.OfType<Button>())
            {
                var selected = string.Equals(button.Tag?.ToString(), _settings.UpdateCheckFrequency.ToString(),
                    StringComparison.Ordinal);
                button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                button.SetResourceReference(
                    ForegroundProperty,
                    selected ? "AccentBrush" : "TextBrush");
            }
        }
    }

    private void ApplicationRulePopup_OnOpened(object? sender, EventArgs e)
    {
        if (sender is Popup popup) _openApplicationRulePopups.Add(popup);
    }

    private void ApplicationRulePopup_OnClosed(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        _openApplicationRulePopups.Remove(popup);
    }

    private void ApplicationRuleMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ConsumeSuppressedPopupTriggerClick(sender)) return;
        if (sender is not Button button || button.Parent is not Grid parent) return;
        var popup = parent.Children.OfType<Popup>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.PlacementTarget, button));
        if (popup is not null) popup.IsOpen = !popup.IsOpen;
    }

    private static void CloseContainingPopup(object sender)
    {
        DependencyObject? current = sender as DependencyObject;
        while (current is not null)
        {
            if (current is Popup popup)
            {
                popup.IsOpen = false;
                return;
            }

            current = LogicalTreeHelper.GetParent(current) ??
                      (current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                          ? VisualTreeHelper.GetParent(current)
                          : null);
        }
    }

    private void CandidateModeMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ConsumeSuppressedPopupTriggerClick(sender)) return;
        SchedulePopup.IsOpen = false;
        CandidateDisplayPopup.IsOpen = false;
        CandidateModePopup.IsOpen = !CandidateModePopup.IsOpen;
    }

    private void StandardCandidateMode_OnClick(object sender, RoutedEventArgs e) =>
        SetQuickCandidateSelection(false);

    private void QuickCandidateMode_OnClick(object sender, RoutedEventArgs e) =>
        SetQuickCandidateSelection(true);

    private void SetQuickCandidateSelection(bool enabled)
    {
        if (!TryUpdateSettings(settings => settings.QuickCandidateSelection = enabled))
        {
            SynchronizeCandidateModeControls();
            return;
        }

        CandidateModePopup.IsOpen = false;
        SynchronizeCandidateModeControls();
        UpdatePreviewRows();
    }

    private void SynchronizeCandidateModeControls()
    {
        if (StandardCandidateModeCheck is null || QuickCandidateModeCheck is null) return;
        StandardCandidateModeCheck.Visibility = _settings.QuickCandidateSelection
            ? Visibility.Hidden
            : Visibility.Visible;
        QuickCandidateModeCheck.Visibility = _settings.QuickCandidateSelection
            ? Visibility.Visible
            : Visibility.Hidden;
        StandardCandidateModeButton.FontWeight = _settings.QuickCandidateSelection
            ? FontWeights.Normal
            : FontWeights.SemiBold;
        QuickCandidateModeButton.FontWeight = _settings.QuickCandidateSelection
            ? FontWeights.SemiBold
            : FontWeights.Normal;
        ApplyPopupSelectionVisual(StandardCandidateModeButton, !_settings.QuickCandidateSelection);
        ApplyPopupSelectionVisual(QuickCandidateModeButton, _settings.QuickCandidateSelection);
        CandidateModeMenuButton.ToolTip = T(_settings.QuickCandidateSelection
            ? "QuickCandidateModeHelp"
            : "StandardCandidateModeHelp");
    }

    private void OverviewStableStateSuppressionModeBox_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingControls ||
            OverviewStableStateSuppressionModeBox.SelectedItem is not StableSuppressionChoice choice) return;
        if (!TryUpdateSettings(settings =>
            {
                settings.StableStateSuppressionMode = choice.Mode;
                if (choice.CustomProfileId is not null)
                    settings.ActiveCustomStableStateSuppressionProfileId = choice.CustomProfileId;
            }))
        {
            SynchronizeStableStateSuppressionControls();
            return;
        }

        SynchronizeStableStateSuppressionControls();
        UpdateProcessRows();
        UpdatePreviewRows();
    }

    private void SynchronizeStableStateSuppressionControls()
    {
        if (OverviewStableStateSuppressionModeBox is null) return;
        var choices = new List<StableSuppressionChoice>
        {
            new(TF(
                    "StableSuppressionFollowCurrentFormat",
                    _settings.ActiveCustomProfile?.Name ?? T(ProfileResourceKey(_settings.Profile))),
                StableStateSuppressionMode.FollowBaseProfile,
                null)
        };
        if (_settings.ShowBuiltInStableStateSuppressionProfiles ||
            _settings.CustomStableStateSuppressionProfiles.Count == 0)
        {
            choices.Add(new(T("StableSuppressionReduceRepeated"), StableStateSuppressionMode.ReduceRepeatedOptimization, null));
            choices.Add(new(T("StableSuppressionBalanced"), StableStateSuppressionMode.Balanced, null));
            choices.Add(new(T("StableSuppressionFasterReevaluation"), StableStateSuppressionMode.FasterReevaluation, null));
        }
        choices.AddRange(_settings.CustomStableStateSuppressionProfiles
            .OrderBy(profile => profile.SortOrder)
            .Select(profile => new StableSuppressionChoice(
                profile.Name,
                StableStateSuppressionMode.Custom,
                profile.Id)));
        choices.Add(new(T("StableSuppressionDisabled"), StableStateSuppressionMode.Disabled, null));
        OverviewStableStateSuppressionModeBox.IsEnabled = _settings.IntelligentCandidateSelection;
        OverviewStableStateSuppressionModeBox.ToolTip = T(_settings.IntelligentCandidateSelection
            ? "StableStateSuppressionHelp"
            : "StableSuppressionPausedWithoutLearning");
        var previous = _syncingControls;
        _syncingControls = true;
        try
        {
            OverviewStableStateSuppressionModeBox.ItemsSource = choices;
            OverviewStableStateSuppressionModeBox.SelectedItem = choices.FirstOrDefault(choice =>
                choice.Mode == _settings.StableStateSuppressionMode &&
                (choice.Mode != StableStateSuppressionMode.Custom ||
                 string.Equals(
                     choice.CustomProfileId,
                     _settings.ActiveCustomStableStateSuppressionProfileId,
                     StringComparison.OrdinalIgnoreCase))) ?? choices[0];
        }
        finally
        {
            _syncingControls = previous;
        }
    }

    private void CandidateDisplayPopup_OnClosed(object? sender, EventArgs e)
    {
    }

    private void CandidateDisplayMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ConsumeSuppressedPopupTriggerClick(sender)) return;
        SchedulePopup.IsOpen = false;
        CandidateModePopup.IsOpen = false;
        CandidateDisplayPopup.IsOpen = !CandidateDisplayPopup.IsOpen;
    }

    private void CandidateDisplayLimit_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested)) return;
        requested = CandidateDisplayLimitPolicy.Normalize(requested);
        if (!TryUpdateSettings(settings => settings.CandidateDisplayLimit = requested))
        {
            SynchronizeCandidateDisplayLimitControls();
            return;
        }

        CandidateDisplayPopup.IsOpen = false;
        SynchronizeCandidateDisplayLimitControls();
        UpdatePreviewRows();
    }

    private void SynchronizeCandidateDisplayLimitControls()
    {
        if (CandidateDisplay10Check is null) return;
        var selected = CandidateDisplayLimitPolicy.Normalize(_settings.CandidateDisplayLimit);
        foreach (var (limit, check, button) in new[]
                 {
                     (10, CandidateDisplay10Check, CandidateDisplay10Button),
                     (20, CandidateDisplay20Check, CandidateDisplay20Button),
                     (40, CandidateDisplay40Check, CandidateDisplay40Button),
                     (CandidateDisplayLimitPolicy.Unlimited, CandidateDisplayUnlimitedCheck, CandidateDisplayUnlimitedButton)
                 })
        {
            var active = limit == selected;
            check.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            ApplyPopupSelectionVisual(button, active);
        }
        CandidateDisplayMenuButton.ToolTip = TF(
            "CandidateDisplayLimitCurrentFormat",
            selected == CandidateDisplayLimitPolicy.Unlimited
                ? T("CandidateDisplayUnlimited")
                : selected.ToString(CultureInfo.CurrentCulture));
    }

    private void ApplyPopupSelectionVisual(Button button, bool selected)
    {
        if (!selected)
        {
            button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            button.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            button.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            button.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
            return;
        }
        button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "AccentSoftBrush");
        button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentBrush");
        button.BorderBrush = System.Windows.Media.Brushes.Transparent;
    }

    private void MainWindow_OnDeactivated(object? sender, EventArgs e)
    {
        CloseManagedPopups();
    }

    private void AutoToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        SetAutoOptimization(AutoToggle.IsChecked == true);
    }

    private void CompactAutoToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        SetAutoOptimization(CompactAutoToggle.IsChecked == true);
    }

    private void SetAutoOptimization(bool enabled)
    {
        if (!TryUpdateSettings(settings => settings.AutoOptimization = enabled))
        {
            _syncingControls = true;
            AutoToggle.IsChecked = _settings.AutoOptimization;
            CompactAutoToggle.IsChecked = _settings.AutoOptimization;
            _syncingControls = false;
            return;
        }
        _state.AutoStatus = enabled ? T("Enabled") : T("Disabled");
        _syncingControls = true;
        AutoToggle.IsChecked = enabled;
        CompactAutoToggle.IsChecked = enabled;
        _syncingControls = false;
        AddHistory(enabled ? "HistoryAutoEnabled" : "HistoryAutoDisabled");
        UpdateScheduledOptimizationAvailability(resetAnchor: true);
        UpdateMonitoringInterval();
    }

    private void ProfileBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || sender is not System.Windows.Controls.ComboBox box || box.SelectedItem is not ProfileChoice choice) return;
        if (choice.CustomProfileId is null && choice.BuiltInProfile is null) return;
        var selectedBaseProfile = choice.BuiltInProfile ?? _settings.CustomProfiles
            .FirstOrDefault(profile => string.Equals(
                profile.Id,
                choice.CustomProfileId,
                StringComparison.OrdinalIgnoreCase))
            ?.BaseProfile;
        if (selectedBaseProfile == OptimizationProfile.Ultimate &&
            !_settings.UltimateRiskPromptSuppressed &&
            !ShowUltimateRiskDialog())
        {
            RefreshProfileSelectors();
            return;
        }
        if (!TryUpdateSettings(settings =>
            {
                if (choice.CustomProfileId is { } customId)
                {
                    settings.ActiveCustomProfileId = customId;
                }
                else if (choice.BuiltInProfile is { } profile)
                {
                    settings.Profile = profile;
                    settings.ActiveCustomProfileId = null;
                }
            }))
        {
            RefreshProfileSelectors();
            return;
        }
        _syncingControls = true;
        try { SetProfileControls(choice); }
        finally { _syncingControls = false; }
        SynchronizeStableStateSuppressionControls();
        UpdateScheduledOptimizationAvailability(resetAnchor: true);
        UpdatePreviewRows();
        if (choice.BuiltInProfile is { } profile)
            AddHistoryNested("HistoryProfileFormat", ProfileResourceKey(profile), Array.Empty<object?>());
        else
            AddHistory("HistoryProfileFormat", choice.Name);
    }

    private bool ShowUltimateRiskDialog()
    {
        var accepted = false;
        var suppress = new System.Windows.Controls.CheckBox
        {
            Content = T("UltimateRiskDoNotRemind"),
            Style = (Style)FindResource("ThemedCheckBoxStyle"),
            Margin = new Thickness(0, 18, 0, 0)
        };
        var message = new TextBlock
        {
            Text = T("UltimateRiskMessage"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (MediaBrush)FindResource("TextBrush"),
            LineHeight = 21
        };
        var confirm = new Button
        {
            Content = T("UltimateRiskConfirm"),
            MinWidth = 108,
            IsDefault = true,
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        buttons.Children.Add(confirm);
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(message);
        root.Children.Add(suppress);
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = this,
            Title = T("UltimateRiskTitle"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush"),
            Content = root
        };
        confirm.Click += (_, _) =>
        {
            accepted = true;
            if (suppress.IsChecked == true)
            {
                _ = TryUpdateSettings(settings => settings.UltimateRiskPromptSuppressed = true);
            }
            dialog.DialogResult = true;
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) dialog.Close();
        };
        ApplyDialogTheme(dialog);
        _ = dialog.ShowDialog();
        return accepted;
    }

    private void RefreshProfileSelectors()
    {
        var choices = new List<ProfileChoice>();
        if (_settings.ShowBuiltInProfiles || _settings.CustomProfiles.Count == 0)
        {
            choices.Add(new ProfileChoice(T("Lite"), OptimizationProfile.Lite, null, "LiteBrush"));
            choices.Add(new ProfileChoice(T("Turbo"), OptimizationProfile.Turbo, null, "TurboBrush"));
            choices.Add(new ProfileChoice(T("Ultimate"), OptimizationProfile.Ultimate, null, "UltimateBrush"));
        }
        choices.AddRange(_settings.CustomProfiles
            .OrderBy(profile => profile.SortOrder)
            .Select(profile => new ProfileChoice(profile.Name, null, profile.Id, "AccentBrush")));

        var selected = _settings.ActiveCustomProfile is { } custom
            ? choices.FirstOrDefault(choice => string.Equals(choice.CustomProfileId, custom.Id, StringComparison.OrdinalIgnoreCase))
            : choices.FirstOrDefault(choice => choice.BuiltInProfile == _settings.Profile);
        selected ??= choices.FirstOrDefault();

        var wasSyncing = _syncingControls;
        _syncingControls = true;
        try
        {
            foreach (var box in new[] { ProfileBox, OverviewProfileBox, CompactProfileBox }) box.ItemsSource = choices;
            if (selected is not null) SetProfileControls(selected);
        }
        finally
        {
            _syncingControls = wasSyncing;
        }
    }

    private void SetProfileControls(ProfileChoice choice)
    {
        var foreground = (MediaBrush)FindResource(choice.BrushKey);
        foreach (var box in new[] { ProfileBox, OverviewProfileBox, CompactProfileBox })
        {
            box.SelectedItem = box.Items.OfType<ProfileChoice>().FirstOrDefault(candidate =>
                candidate.BuiltInProfile == choice.BuiltInProfile &&
                string.Equals(candidate.CustomProfileId, choice.CustomProfileId, StringComparison.OrdinalIgnoreCase));
            box.Foreground = foreground;
        }
        var profileIgnoresMemoryPressure = _settings.ActiveProfileIgnoresMemoryPressureThreshold;
        IgnoreMemoryPressureThresholdCheckBox.IsEnabled = !profileIgnoresMemoryPressure;
        IgnoreMemoryPressureThresholdCheckBox.IsChecked =
            _settings.ResolveOptimizationSettings(manual: false).IgnoreMemoryPressureThreshold;
        UpdateScheduledOptimizationAvailability();
    }

    private static string ProfileResourceKey(OptimizationProfile profile) => profile switch
    {
        OptimizationProfile.Lite => "Lite",
        OptimizationProfile.Turbo => "Turbo",
        OptimizationProfile.Ultimate => "Ultimate",
        _ => "Turbo"
    };

    private void AddProtected_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        var dialog = new OpenFileDialog
        {
            Filter = T("ExecutableFilesFilter"),
            Multiselect = true,
            Title = T("ProtectFileTitle")
        };
        if (dialog.ShowDialog(this) != true) return;
        if (_state.IsBusy) return;
        if (!TryUpdateSettings(settings =>
            {
                foreach (var path in dialog.FileNames)
                    ApplicationProtectionSettings.ProtectSelectedExecutables(
                        settings,
                        path,
                        new[] { path });
            })) return;
        RefreshProtectedList();
        UpdateProcessRows();
    }

    private async void AddRunningProtected_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        if (!await RefreshSnapshotAsync(waitForCurrentRefresh: true)) return;
        if (_state.IsBusy) return;
        var currentRules = ApplicationProtectionSettings.Resolve(_settings);
        var applications = RunningProtectionCandidateCatalog.Create(_families, currentRules);

        if (applications.Count == 0)
        {
            ShowThemedMessage(T("ApplicationProtection"), T("ProtectionNoCandidates"));
            return;
        }

        var selections = ShowRunningProtectionDialog(applications);
        if (selections is null) return;
        if (_state.IsBusy) return;

        var mergedRules = RunningProtectionCandidateCatalog.MergeSelections(currentRules, selections);
        if (!TryUpdateSettings(settings => ApplicationProtectionSettings.Replace(settings, mergedRules))) return;
        RefreshProtectedList();
        UpdateProcessRows();
        UpdatePreviewRows();
        AddHistory(
            "ProtectionUpdatedFormat",
            selections.Count(selection => selection.ProtectionState != ApplicationProtectionState.None));
    }

    private IReadOnlyList<RunningProtectionSelection>? ShowRunningProtectionDialog(
        IReadOnlyList<RunningProtectionCandidate> applications)
    {
        var selectionReaders = new List<Func<RunningProtectionSelection>>();
        var minimumProcessWorkingSetBytes = _settings
            .ResolveOptimizationSettings(manual: false)
            .MinimumProcessWorkingSetBytes;
        var panel = new StackPanel();
        foreach (var application in applications)
        {
            var state = application.ProtectionState;
            var updating = false;
            var parent = new System.Windows.Controls.CheckBox
            {
                IsThreeState = true,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            ApplyProgrammaticCheckBoxTheme(parent);
            AutomationProperties.SetName(parent, application.DisplayName);
            parent.ToolTip = T("RunningProtectionFamilyCheckHelp");
            var title = new TextBlock
            {
                Text = TF(
                    "RunningProtectionApplicationFormat",
                    application.DisplayName,
                    application.ProcessCount,
                    DisplayFormat.Bytes(application.WorkingSetBytes)),
                FontWeight = FontWeights.SemiBold
            };
            var path = new TextBlock
            {
                Text = application.ApplicationExecutablePath,
                Margin = new Thickness(0, 3, 12, 0),
                FontSize = 12,
                Foreground = (MediaBrush)FindResource("MutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            path.ToolTip = path.Text;
            var titlePanel = new StackPanel();
            titlePanel.Children.Add(title);
            titlePanel.Children.Add(path);
            var status = new TextBlock
            {
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Foreground = (MediaBrush)FindResource("MutedBrush"),
                Margin = new Thickness(12, 0, 8, 0)
            };
            status.ToolTip = T("RunningProtectionStatusHelp");
            var expandIcon = new TextBlock
            {
                FontFamily = new MediaFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13,
                Text = "\uE76C"
            };
            var expand = new ToggleButton
            {
                Style = (Style)FindResource("ExpandButtonStyle"),
                Content = expandIcon,
                Visibility = application.ProcessCount > 1
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                ToolTip = T("ExpandProtectedExecutables")
            };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            header.Children.Add(parent);
            Grid.SetColumn(titlePanel, 1);
            header.Children.Add(titlePanel);
            Grid.SetColumn(status, 2);
            header.Children.Add(status);
            Grid.SetColumn(expand, 3);
            header.Children.Add(expand);

            var childPanel = new StackPanel
            {
                Margin = new Thickness(30, 10, 0, 0),
                Visibility = Visibility.Collapsed
            };
            var childBoxes = new List<System.Windows.Controls.CheckBox>();
            foreach (var executable in application.Executables)
            {
                var details = new StackPanel();
                details.Children.Add(new TextBlock
                {
                    Text = TF(
                        "RunningProtectionExecutableFormat",
                        executable.Name,
                        executable.InstanceCount,
                        DisplayFormat.Bytes(executable.WorkingSetBytes)),
                    FontWeight = FontWeights.SemiBold
                });
                var executablePath = new TextBlock
                {
                    Text = executable.ExecutablePath,
                    Margin = new Thickness(0, 2, 0, 0),
                    FontSize = 12,
                    Foreground = (MediaBrush)FindResource("MutedBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                executablePath.ToolTip = executablePath.Text;
                details.Children.Add(executablePath);
                var child = new System.Windows.Controls.CheckBox
                {
                    Content = details,
                    Tag = executable.ExecutablePath,
                    IsChecked = application.ProtectionState == ApplicationProtectionState.EntireFamily ||
                                executable.IsProtected,
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
                };
                child.ToolTip = T("RunningProtectionExecutableCheckHelp");
                ApplyProgrammaticCheckBoxTheme(child);
                childBoxes.Add(child);

                var processPanel = new StackPanel
                {
                    Margin = new Thickness(28, 5, 0, 0),
                    Visibility = Visibility.Collapsed
                };
                foreach (var process in executable.Processes)
                {
                    processPanel.Children.Add(new TextBlock
                    {
                        Text = process.WorkingSetBytes < minimumProcessWorkingSetBytes
                            ? TF(
                                "RunningProtectionProcessBelowThresholdFormat",
                                process.ProcessId,
                                DisplayFormat.Bytes(process.WorkingSetBytes))
                            : TF(
                                "RunningProtectionProcessFormat",
                                process.ProcessId,
                                DisplayFormat.Bytes(process.WorkingSetBytes)),
                        Margin = new Thickness(0, 0, 0, 4),
                        FontSize = 12,
                        Foreground = (MediaBrush)FindResource("MutedBrush"),
                        ToolTip = T("RunningProtectionProcessHelp")
                    });
                }
                var processExpandIcon = new TextBlock
                {
                    FontFamily = new MediaFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 12,
                    Text = "\uE76C"
                };
                var processExpand = new ToggleButton
                {
                    Style = (Style)FindResource("ExpandButtonStyle"),
                    Content = processExpandIcon,
                    Visibility = executable.Processes.Count > 1
                        ? Visibility.Visible
                        : Visibility.Collapsed,
                    ToolTip = T("ExpandExecutableProcesses")
                };
                processExpand.Checked += (_, _) =>
                {
                    processPanel.Visibility = Visibility.Visible;
                    processExpandIcon.Text = "\uE70D";
                };
                processExpand.Unchecked += (_, _) =>
                {
                    processPanel.Visibility = Visibility.Collapsed;
                    processExpandIcon.Text = "\uE76C";
                };

                var executableHeader = new Grid();
                executableHeader.ColumnDefinitions.Add(new ColumnDefinition());
                executableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                executableHeader.Children.Add(child);
                Grid.SetColumn(processExpand, 1);
                executableHeader.Children.Add(processExpand);

                var executablePanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
                executablePanel.Children.Add(executableHeader);
                executablePanel.Children.Add(processPanel);
                childPanel.Children.Add(executablePanel);
            }

            void SynchronizeSelectionVisuals()
            {
                updating = true;
                parent.IsChecked = state switch
                {
                    ApplicationProtectionState.EntireFamily => true,
                    ApplicationProtectionState.Partial => null,
                    _ => false
                };
                status.Text = T(state switch
                {
                    ApplicationProtectionState.EntireFamily => "ProtectionEntireFamily",
                    ApplicationProtectionState.Partial => "ProtectionPartial",
                    _ => "ProtectionNone"
                });
                updating = false;
            }

            parent.Click += (_, _) =>
            {
                if (updating) return;
                state = state == ApplicationProtectionState.EntireFamily
                    ? ApplicationProtectionState.None
                    : ApplicationProtectionState.EntireFamily;
                updating = true;
                foreach (var child in childBoxes)
                    child.IsChecked = state == ApplicationProtectionState.EntireFamily;
                updating = false;
                SynchronizeSelectionVisuals();
            };
            foreach (var child in childBoxes)
            {
                child.Checked += ChildSelectionChanged;
                child.Unchecked += ChildSelectionChanged;
            }
            expand.Checked += (_, _) =>
            {
                childPanel.Visibility = Visibility.Visible;
                expandIcon.Text = "\uE70D";
            };
            expand.Unchecked += (_, _) =>
            {
                childPanel.Visibility = Visibility.Collapsed;
                expandIcon.Text = "\uE76C";
            };

            void ChildSelectionChanged(object sender, RoutedEventArgs e)
            {
                if (updating) return;
                state = childBoxes.Any(child => child.IsChecked == true)
                    ? ApplicationProtectionState.Partial
                    : ApplicationProtectionState.None;
                SynchronizeSelectionVisuals();
            }

            SynchronizeSelectionVisuals();
            var applicationPanel = new StackPanel();
            applicationPanel.Children.Add(header);
            applicationPanel.Children.Add(childPanel);
            panel.Children.Add(new Border
            {
                Background = (MediaBrush)FindResource("SurfaceRaisedBrush"),
                BorderBrush = (MediaBrush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = applicationPanel
            });
            selectionReaders.Add(() => new RunningProtectionSelection(
                application.ApplicationExecutablePath,
                state,
                childBoxes
                    .Where(child => child.IsChecked == true)
                    .Select(child => (string)child.Tag)
                    .ToArray(),
                application.MatchedRuleApplicationPaths));
        }

        var confirm = new Button { Content = T("ProtectionDialogConfirm"), MinWidth = 120, IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle") };
        var cancel = new Button { Content = T("Cancel"), MinWidth = 88, Margin = new Thickness(10, 0, 0, 0), IsCancel = true, Style = (Style)FindResource("ButtonStyle") };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var dialog = new Window
        {
            Owner = this,
            Title = T("ProtectionDialogTitle"),
            Width = 720,
            Height = 560,
            MinWidth = 580,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush"),
            Content = root,
            ShowInTaskbar = false
        };
        ApplyDialogTheme(dialog);
        IReadOnlyList<RunningProtectionSelection>? selected = null;
        confirm.Click += (_, _) =>
        {
            selected = selectionReaders.Select(read => read()).ToArray();
            dialog.DialogResult = true;
        };
        _ = dialog.ShowDialog();
        return selected;
    }

    private void RemoveProtectedGroup_OnClick(object sender, RoutedEventArgs e)
    {
        CloseContainingPopup(sender);
        if (_state.IsBusy) return;
        var group = (sender as FrameworkElement)?.Tag as ProtectedApplicationGroup;
        if (group is null || group.RuleApplicationPaths.Count == 0) return;
        var message = TF("RemoveProtectedGroupConfirmFormat", group.Name, group.RuleApplicationPaths.Count);
        if (!ConfirmProtectionRemoval(message)) return;
        if (_state.IsBusy) return;
        RemoveProtectionRules(group.RuleApplicationPaths);
    }

    private bool ConfirmProtectionRemoval(string message) => ShowRiskConfirmation(
        T("RemoveProtectionConfirmTitle"),
        message,
        T("RemoveProtection"));

    private bool ShowRiskConfirmation(string title, string message, string confirmText, Window? owner = null)
    {
        var confirmed = false;
        var icon = new TextBlock
        {
            Text = "\uE7BA",
            FontFamily = new MediaFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 24,
            Foreground = (MediaBrush)FindResource("WarningBrush"),
            VerticalAlignment = System.Windows.VerticalAlignment.Top
        };
        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Foreground = (MediaBrush)FindResource("TextBrush")
        };
        var cancel = new Button
        {
            Content = T("Cancel"),
            MinWidth = 92,
            IsCancel = true,
            IsDefault = true,
            Style = (Style)FindResource("ButtonStyle")
        };
        var remove = new Button
        {
            Content = confirmText,
            MinWidth = 118,
            Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("DangerButtonStyle")
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(remove);
        var root = new Grid { Margin = new Thickness(24) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(icon);
        Grid.SetColumn(messageText, 2);
        root.Children.Add(messageText);
        Grid.SetColumn(buttons, 2);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = owner ?? this,
            Title = title,
            Width = 540,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush"),
            Content = root
        };
        remove.Click += (_, _) =>
        {
            confirmed = true;
            dialog.DialogResult = true;
        };
        ApplyDialogTheme(dialog);
        _ = dialog.ShowDialog();
        return confirmed;
    }

    private void RemoveProtectionRules(IReadOnlyCollection<string> applicationPaths)
    {
        if (_state.IsBusy) return;
        if (!TryUpdateSettings(settings =>
            {
                foreach (var path in applicationPaths)
                    ApplicationProtectionSettings.Remove(settings, path);
            })) return;
        RefreshProtectedList();
        UpdateProcessRows();
        UpdatePreviewRows();
    }

    private async void DeepRelease_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        var enhancedSafety = _settings.EnhancedSafety;
        SetBusyState(true);
        try
        {
            if (!await RefreshSnapshotAsync(waitForCurrentRefresh: true)) return;
            var candidates = BackgroundActivityTracker.CreateDeepReleaseCandidates(
                _families,
                _strictActivity,
                CurrentProtectionRules());
            var runningServices = await Task.Run(_serviceManager.CaptureRunningServices);
            var serviceSuggestions = RelatedServiceAdvisor.Find(
                candidates.Select(candidate => candidate.Family).ToArray(),
                runningServices);
            candidates = DeepReleaseCandidateDeduplicator.RemoveServiceDuplicates(candidates, serviceSuggestions);
            if (candidates.Count == 0 && serviceSuggestions.Count == 0)
            {
                ShowThemedMessage(T("DeepRelease"), T("DeepReleaseNoCandidates"));
                return;
            }

            var selected = candidates.Count == 0
                ? Array.Empty<DeepReleaseCandidate>()
                : ShowDeepReleaseDialog(candidates);
            if (!DeepReleaseDialogFlow.ShouldContinueToServices(candidates.Count > 0, selected is not null) || selected is null) return;
            var selectedServices = ShowServiceSuggestionsDialog(serviceSuggestions);
            if (selected.Count == 0 && selectedServices.Count == 0) return;

            if (!await RefreshSnapshotAsync(waitForCurrentRefresh: true)) return;
            var safeCandidates = DeepReleaseExecutionSafetyPolicy.FilterSafeCandidates(
                selected,
                _families,
                CurrentProtectionRules());
            if (safeCandidates.Count != selected.Count)
            {
                _diagnosticLog.Info($"Deep release skipped {selected.Count - safeCandidates.Count} application(s) after the execution safety recheck.");
            }
            if (safeCandidates.Count == 0 && selectedServices.Count == 0)
            {
                ShowThemedMessage(T("DeepRelease"), T("DeepReleaseNoCandidates"));
                return;
            }

            await CloseApplicationsAsync(
                safeCandidates,
                _families,
                selectedServices,
                enhancedSafety);
        }
        finally
        {
            SetBusyState(false);
            UpdateMonitoringInterval();
        }
    }

    private IReadOnlyList<DeepReleaseCandidate>? ShowDeepReleaseDialog(IReadOnlyList<DeepReleaseCandidate> candidates)
    {
        var selected = new List<DeepReleaseCandidate>();
        var boxes = new List<System.Windows.Controls.CheckBox>();
        var panel = new StackPanel();
        foreach (var candidate in candidates)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = DeepReleasePresentation.FormatCandidate(candidate, _uiLanguage),
                IsChecked = DeepReleaseSelectionPolicy.IsCheckedByDefault(candidate),
                Margin = new Thickness(0, 0, 0, 10),
                Tag = candidate,
                Foreground = (MediaBrush)FindResource(candidate.Activity.State == BackgroundActivityState.Idle ? "SuccessBrush" : "TextBrush")
            };
            ApplyProgrammaticCheckBoxTheme(box);
            boxes.Add(box);
            panel.Children.Add(box);
        }

        var summary = new TextBlock
        {
            Style = (Style)FindResource("CaptionStyle"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        void UpdateSummary() => summary.Text = DeepReleasePresentation.FormatSelection(
            boxes.Where(box => box.IsChecked == true).Select(box => (DeepReleaseCandidate)box.Tag).ToArray(),
            _uiLanguage);
        foreach (var box in boxes)
        {
            box.Checked += (_, _) => UpdateSummary();
            box.Unchecked += (_, _) => UpdateSummary();
        }
        UpdateSummary();

        var confirm = new Button { Content = T("DeepReleaseConfirm"), MinWidth = 150, IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle") };
        var cancel = new Button { Content = T("Cancel"), MinWidth = 88, Margin = new Thickness(10, 0, 0, 0), IsCancel = true, Style = (Style)FindResource("ButtonStyle") };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(confirm); buttons.Children.Add(cancel);
        var root = new DockPanel { Margin = new Thickness(18) };
        var warning = new TextBlock { Text = T("DeepReleaseWarning"), TextWrapping = TextWrapping.Wrap, Foreground = (MediaBrush)FindResource("WarningBrush"), Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(warning, Dock.Top); DockPanel.SetDock(summary, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(warning); root.Children.Add(summary); root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new Window { Owner = this, Title = T("DeepRelease"), Width = 680, Height = 520, MinWidth = 560, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (MediaBrush)FindResource("WindowBrush"), Foreground = (MediaBrush)FindResource("TextBrush"), Content = root, ShowInTaskbar = false };
        ApplyDialogTheme(dialog);
        confirm.Click += (_, _) => { selected.AddRange(boxes.Where(box => box.IsChecked == true).Select(box => (DeepReleaseCandidate)box.Tag)); dialog.DialogResult = true; };
        return dialog.ShowDialog() == true ? selected : null;
    }

    private IReadOnlyList<ServiceSuggestion> ShowServiceSuggestionsDialog(IReadOnlyList<ServiceSuggestion> suggestions)
    {
        if (suggestions.Count == 0) return Array.Empty<ServiceSuggestion>();

        var boxes = new List<System.Windows.Controls.CheckBox>();
        var panel = new StackPanel();
        foreach (var suggestion in suggestions)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = $"{suggestion.Service.DisplayName} ({suggestion.Service.Name}) · {suggestion.RelatedApplication}",
                FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(new TextBlock
            {
                Text = suggestion.ImpactResourceKey is { } key ? T(key) : suggestion.Impact,
                Style = (Style)FindResource("CaptionStyle"),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            var box = new System.Windows.Controls.CheckBox
            {
                Content = content,
                Tag = suggestion,
                IsChecked = suggestion.IsRecommended,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = (MediaBrush)FindResource(suggestion.Service.IsSystemService ? "WarningBrush" : "TextBrush")
            };
            ApplyProgrammaticCheckBoxTheme(box);
            boxes.Add(box);
            panel.Children.Add(box);
        }

        var confirm = new Button { Content = T("ServiceDialogConfirm"), MinWidth = 130, IsDefault = true, Style = (Style)FindResource("PrimaryButtonStyle") };
        var skip = new Button { Content = T("ServiceDialogSkip"), MinWidth = 130, Margin = new Thickness(10, 0, 0, 0), IsCancel = true, Style = (Style)FindResource("ButtonStyle") };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(confirm);
        buttons.Children.Add(skip);
        var root = new DockPanel { Margin = new Thickness(18) };
        var warning = new TextBlock { Text = T("ServiceDialogWarning"), Foreground = (MediaBrush)FindResource("WarningBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(warning, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(warning);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new Window { Owner = this, Title = T("ServiceDialogTitle"), Width = 720, Height = 520, MinWidth = 580, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (MediaBrush)FindResource("WindowBrush"), Foreground = (MediaBrush)FindResource("TextBrush"), Content = root, ShowInTaskbar = false };
        ApplyDialogTheme(dialog);
        var selected = new List<ServiceSuggestion>();
        confirm.Click += (_, _) =>
        {
            selected.AddRange(boxes.Where(box => box.IsChecked == true).Select(box => (ServiceSuggestion)box.Tag));
            dialog.DialogResult = true;
        };
        _ = dialog.ShowDialog();
        return selected;
    }

    private void ApplyProgrammaticCheckBoxTheme(System.Windows.Controls.CheckBox box)
    {
        box.Style = (Style)FindResource("ThemedCheckBoxStyle");
        box.Background = (MediaBrush)FindResource("SurfaceRaisedBrush");
        box.BorderBrush = (MediaBrush)FindResource("BorderBrush");
    }

    private void ApplyDialogTheme(Window dialog)
    {
        dialog.WindowStyle = WindowStyle.None;
        dialog.AllowsTransparency = false;
        dialog.ResizeMode = ResizeMode.NoResize;
        InputModality.Attach(dialog);
        CopyThemeResources(dialog.Resources);
        
        if (dialog.Content is UIElement content &&
            content is not Border { BorderThickness.Left: > 0 })
        {
            dialog.Content = null;
            var frame = new Border
            {
                Background = (MediaBrush)FindResource("WindowBrush"),
                BorderBrush = (MediaBrush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1)
            };
            frame.Child = content;
            dialog.Content = frame;
        }

        dialog.SourceInitialized += (_, _) =>
        {
            WindowThemeService.ApplyDarkTitleBar(dialog, !IsLightThemeActive());
            WindowThemeService.EnableNativeWindowAnimations(dialog);
        };
    }

    private void CopyThemeResources(ResourceDictionary resources)
    {
        foreach (var key in new[]
                 {
                     "WindowBrush", "SurfaceBrush", "SurfaceRaisedBrush", "BorderBrush", "TextBrush",
                     "MutedBrush", "AccentBrush", "AccentSoftBrush", "SuccessBrush", "WarningBrush",
                     "WarningHoverBrush", "WarningPressedBrush", "ActionTextBrush", "NavigationHoverBrush",
                     "BrandLogoBrush", "BrandLogoTextBrush", "BrandLogoBorderBrush",
                     "UltimateBrush", "AlternateRowBrush", "ScrollTrackBrush", "ScrollThumbBrush",
                     "ScrollThumbHoverBrush"
                 })
        {
            resources[key] = FindResource(key);
        }
    }

    private MessageBoxResult ShowThemedMessage(
        string title,
        string message,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.Information,
        string? affirmativeText = null,
        string? negativeText = null,
        string? cancelText = null,
        bool destructiveAffirmative = false)
    {
        var result = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush")
        };
        var icon = new TextBlock
        {
            Text = image switch
            {
                MessageBoxImage.Warning => "\uE7BA",
                MessageBoxImage.Error => "\uE783",
                MessageBoxImage.Question => "\uE897",
                _ => "\uE946"
            },
            FontFamily = new MediaFontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 24,
            Foreground = (MediaBrush)FindResource(image switch
            {
                MessageBoxImage.Warning => "WarningBrush",
                MessageBoxImage.Error => "UltimateBrush",
                _ => "AccentBrush"
            }),
            VerticalAlignment = VerticalAlignment.Top
        };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(icon);
        var messageScroll = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
                Foreground = (MediaBrush)FindResource("TextBrush")
            }
        };
        Grid.SetColumn(messageScroll, 2);
        body.Children.Add(messageScroll);

        var buttonPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        Button AddButton(string text, MessageBoxResult buttonResult, bool primary = false, bool danger = false)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 96,
                Margin = buttonPanel.Children.Count == 0 ? new Thickness(0) : new Thickness(10, 0, 0, 0),
                Style = (Style)FindResource(danger
                    ? "DangerButtonStyle"
                    : primary
                        ? "PrimaryButtonStyle"
                        : "ButtonStyle")
            };
            button.Click += (_, _) =>
            {
                result = buttonResult;
                dialog.Close();
            };
            buttonPanel.Children.Add(button);
            return button;
        }
        if (buttons == MessageBoxButton.OK)
        {
            var ok = AddButton(affirmativeText ?? T("DialogOk"), MessageBoxResult.OK, primary: true);
            ok.IsDefault = true;
        }
        else
        {
            var yes = AddButton(
                affirmativeText ?? T("DialogYes"),
                MessageBoxResult.Yes,
                primary: !destructiveAffirmative,
                danger: destructiveAffirmative);
            yes.IsDefault = true;
            AddButton(negativeText ?? T("DialogNo"), MessageBoxResult.No);
            if (buttons == MessageBoxButton.YesNoCancel)
                AddButton(cancelText ?? T("Cancel"), MessageBoxResult.Cancel);
        }

        var root = new Grid { Margin = new Thickness(22, 18, 22, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(body);
        Grid.SetRow(buttonPanel, 1);
        root.Children.Add(buttonPanel);
        dialog.Content = root;
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) dialog.Close();
        };
        ApplyDialogTheme(dialog);
        _ = dialog.ShowDialog();
        return result;
    }

    private async Task CloseApplicationsAsync(
        IReadOnlyList<DeepReleaseCandidate> candidates,
        IReadOnlyList<ProcessFamilySnapshot> currentFamilies,
        IReadOnlyList<ServiceSuggestion> services,
        bool enhancedSafety)
    {
        var currentByKey = currentFamilies
            .GroupBy(family => family.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var targets = candidates
            .SelectMany(candidate =>
            {
                var relatedProcessIds = currentByKey.TryGetValue(candidate.Family.Key, out var currentFamily)
                    ? currentFamily.Processes.Select(process => process.ProcessId).ToHashSet()
                    : candidate.Family.Processes.Select(process => process.ProcessId).ToHashSet();
                return candidate.Family.Processes.Select(process =>
                    new DeepReleaseTarget(process, relatedProcessIds));
            })
            .GroupBy(target => target.Process.ProcessId)
            .Select(group => group.First())
            .ToArray();
        var remaining = new List<DeepReleaseTarget>();
        foreach (var target in targets)
        {
            try
            {
                using var process = TryOpenSafeDeepReleaseProcess(target);
                if (process is null) continue;
                if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow();
                else remaining.Add(target);
            }
            catch { }
        }
        await Task.Delay(EnhancedSafetyBehavior.DeepReleaseGracePeriod(enhancedSafety));
        foreach (var target in targets)
        {
            try
            {
                using var process = TryOpenSafeDeepReleaseProcess(target);
                if (process is not null && !process.HasExited) remaining.Add(target);
            }
            catch { }
        }
        remaining = remaining
            .GroupBy(target => target.Process.ProcessId)
            .Select(group => group.First())
            .ToList();
        var shouldTerminate = remaining.Count > 0 && (!EnhancedSafetyBehavior.RequiresForceTerminationConfirmation(enhancedSafety) || ShowThemedMessage(
            T("ForceTerminateTitle"),
            TF("ForceTerminatePromptFormat", remaining.Count),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            affirmativeText: T("DialogForceTerminate"),
            negativeText: T("Cancel"),
            destructiveAffirmative: true) == MessageBoxResult.Yes);
        if (shouldTerminate)
        {
            foreach (var target in remaining)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        using var process = TryOpenSafeDeepReleaseProcess(target);
                        if (process is null) return;
                        process.Kill(entireProcessTree: false);
                        process.WaitForExit(800);
                    }
                    catch { }
                });
            }
        }
        var stoppedServices = 0;
        foreach (var suggestion in services)
        {
            var result = await Task.Run(() => _serviceManager.Stop(suggestion.Service.Name));
            if (result.Success)
            {
                stoppedServices++;
            }
            else
            {
                _diagnosticLog.Warning($"Unable to stop service {result.ServiceName}: {result.Error}");
            }
        }
        AddHistory("DeepReleaseHistoryFormat", candidates.Count, stoppedServices);
        await RefreshSnapshotAsync();
    }

    private static Process? TryOpenSafeDeepReleaseProcess(DeepReleaseTarget target)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(target.Process.ProcessId);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            long? actualStartTime;
            try { actualStartTime = process.StartTime.ToUniversalTime().ToFileTimeUtc(); }
            catch { actualStartTime = null; }
            var safety = DeepReleaseProcessSafetyPolicy.Evaluate(
                target.Process.StartTimeFileTimeUtc,
                actualStartTime,
                target.RelatedProcessIds,
                ForegroundProcessProbe.GetProcessId());
            if (safety.CanTrim) return process;

            process.Dispose();
            return null;
        }
        catch
        {
            process?.Dispose();
            return null;
        }
    }

    private void RefreshCustomProfileCatalog(
        string? selectCustomId = null,
        OptimizationProfile? selectBuiltInProfile = null)
    {
        var items = new List<ProfileCatalogItem>
        {
            new(T("Lite"), T("BuiltInProfile"), OptimizationProfile.Lite, null),
            new(T("Turbo"), T("BuiltInProfile"), OptimizationProfile.Turbo, null),
            new(T("Ultimate"), T("BuiltInProfile"), OptimizationProfile.Ultimate, null)
        };
        items.AddRange(_settings.CustomProfiles
            .OrderBy(profile => profile.SortOrder)
            .Select(profile => new ProfileCatalogItem(profile.Name, T("CustomProfileKind"), null, profile.Id)));
        CustomProfileCatalogList.ItemsSource = items;
        var wantedId = selectCustomId ?? _editingCustomProfileId;
        CustomProfileCatalogList.SelectedItem = wantedId is not null
            ? items.FirstOrDefault(item => string.Equals(
                item.CustomProfileId,
                wantedId,
                StringComparison.OrdinalIgnoreCase))
            : selectBuiltInProfile.HasValue
                ? items.FirstOrDefault(item => item.BuiltInProfile == selectBuiltInProfile)
                : items.FirstOrDefault();
        CustomProfileCatalogList.SelectedItem ??= items.FirstOrDefault();
        ShowBuiltInProfilesCheckBox.IsEnabled = _settings.CustomProfiles.Count > 0;
    }

    private void CustomProfileCatalogList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomProfileCatalogList.SelectedItem is not ProfileCatalogItem item) return;
        var custom = item.CustomProfileId is null
            ? null
            : _settings.CustomProfiles.FirstOrDefault(profile => string.Equals(profile.Id, item.CustomProfileId, StringComparison.OrdinalIgnoreCase));
        _editingCustomProfileId = custom?.Id;
        SynchronizeProfileCopyButton(item);
        DeleteProfileButton.IsEnabled = custom is not null;
        MoveProfileUpButton.IsEnabled = custom is not null && custom.SortOrder > 0;
        MoveProfileDownButton.IsEnabled = custom is not null && custom.SortOrder < _settings.CustomProfiles.Count - 1;
        CustomEditorPlaceholder.Visibility = Visibility.Collapsed;
        CustomEditorScroll.Visibility = Visibility.Visible;
        var displayed = custom ?? CustomProfilePolicy.Create(item.BuiltInProfile!.Value, item.Name, 0);
        LoadCustomProfileEditor(displayed);
        SetCustomProfileEditorEditable(custom is not null, displayed.BaseProfile);
    }

    private void SynchronizeProfileCopyButton(ProfileCatalogItem? item)
    {
        var validSelection = item is { BuiltInProfile: not null } ||
                             (item?.CustomProfileId is not null && _settings.CustomProfiles.Any(profile =>
                                 string.Equals(profile.Id, item.CustomProfileId, StringComparison.OrdinalIgnoreCase)));
        var reason = !validSelection
            ? T("CustomProfileCopySelectionInvalid")
            : _state.IsBusy
                ? T("CustomProfileCopyBusy")
                : !_settingsWriteAvailable
                    ? T("CustomProfileCopyUnavailable")
                    : _settings.CustomProfiles.Count >= CustomProfilePolicy.MaximumCustomProfiles
                        ? T("CustomProfileCopyLimit")
                        : null;
        CopyProfileButton.IsEnabled = reason is null;
        CopyProfileButton.ToolTip = reason ?? T("CopyProfile");
    }

    private void CopyProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy || !_settingsWriteAvailable ||
            CustomProfileCatalogList.SelectedItem is not ProfileCatalogItem item)
            return;
        if (_settings.CustomProfiles.Count >= CustomProfilePolicy.MaximumCustomProfiles)
        {
            ShowThemedMessage(T("Custom"), T("CustomProfileLimit"));
            return;
        }

        var custom = item.CustomProfileId is null
            ? null
            : _settings.CustomProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, item.CustomProfileId, StringComparison.OrdinalIgnoreCase));
        CustomOptimizationProfile source;
        if (custom is not null)
        {
            source = custom;
            if (_customProfileDraftDirty)
            {
                var choice = ShowThemedMessage(
                    T("CopyProfileDraftChoiceTitle"),
                    T("CopyProfileDraftChoiceMessage"),
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question,
                    affirmativeText: T("DialogUseCurrentChanges"),
                    negativeText: T("DialogUseSavedVersion"),
                    cancelText: T("Cancel"));
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes)
                {
                    if (!TryBuildCustomProfileDraft(custom, out var draft)) return;
                    source = draft;
                }
            }
        }
        else if (item.BuiltInProfile is { } baseProfile)
        {
            source = CustomProfilePolicy.Create(baseProfile, item.Name, 0);
        }
        else
        {
            return;
        }

        var baseName = string.IsNullOrWhiteSpace(source.Name) ? item.Name : source.Name;
        var name = TF("ProfileCopyNameFormat", baseName);
        for (var suffix = 2; !CustomProfilePolicy.IsUniqueName(_settings.CustomProfiles, name); suffix++)
            name = TF("ProfileCopyNumberedNameFormat", baseName, suffix);
        string? createdProfileId = null;
        if (!TryUpdateSettings(settings =>
            {
                createdProfileId = CustomProfileSettingsOperations
                    .AddCopy(settings, source, name)
                    .Id;
            })) return;
        RefreshCustomProfileCatalog(createdProfileId);
        RefreshProfileSelectors();
    }

    private void MoveProfileUp_OnClick(object sender, RoutedEventArgs e) => MoveEditingProfile(-1);

    private void MoveProfileDown_OnClick(object sender, RoutedEventArgs e) => MoveEditingProfile(1);

    private void MoveEditingProfile(int offset)
    {
        var currentOrder = _settings.CustomProfiles.OrderBy(profile => profile.SortOrder).ToList();
        var currentIndex = currentOrder.FindIndex(profile =>
            string.Equals(profile.Id, _editingCustomProfileId, StringComparison.OrdinalIgnoreCase));
        var currentTarget = currentIndex + offset;
        if (currentIndex < 0 || currentTarget < 0 || currentTarget >= currentOrder.Count) return;
        if (!TryUpdateSettings(settings =>
            {
                var ordered = settings.CustomProfiles.OrderBy(profile => profile.SortOrder).ToList();
                var index = ordered.FindIndex(profile =>
                    string.Equals(profile.Id, _editingCustomProfileId, StringComparison.OrdinalIgnoreCase));
                var target = index + offset;
                (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
                for (var itemIndex = 0; itemIndex < ordered.Count; itemIndex++)
                    ordered[itemIndex].SortOrder = itemIndex;
                settings.CustomProfiles = ordered;
            })) return;
        RefreshCustomProfileCatalog(_editingCustomProfileId);
        RefreshProfileSelectors();
    }

    private void DeleteProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var orderedProfiles = _settings.CustomProfiles
            .OrderBy(candidate => candidate.SortOrder)
            .ToList();
        var profileIndex = orderedProfiles.FindIndex(candidate =>
            string.Equals(candidate.Id, _editingCustomProfileId, StringComparison.OrdinalIgnoreCase));
        if (profileIndex < 0) return;
        var profile = orderedProfiles[profileIndex];
        var previousProfileId = profileIndex > 0 ? orderedProfiles[profileIndex - 1].Id : null;
        var removedActiveProfile = string.Equals(
            profile.Id,
            _settings.ActiveCustomProfileId,
            StringComparison.OrdinalIgnoreCase);
        if (!TryUpdateSettings(settings =>
            {
                CustomProfileSettingsOperations.Remove(settings, profile.Id);
            })) return;
        if (_settings.CustomProfiles.Count == 0)
        {
            _syncingControls = true;
            ShowBuiltInProfilesCheckBox.IsChecked = true;
            _syncingControls = false;
        }
        _editingCustomProfileId = previousProfileId;
        RefreshCustomProfileCatalog(
            previousProfileId,
            previousProfileId is null ? OptimizationProfile.Ultimate : null);
        RefreshProfileSelectors();
        UpdateScheduledOptimizationAvailability(resetAnchor: removedActiveProfile);
    }

    private void ShowBuiltInProfilesCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        if (_settings.CustomProfiles.Count == 0)
        {
            _syncingControls = true;
            ShowBuiltInProfilesCheckBox.IsChecked = true;
            _syncingControls = false;
            return;
        }
        var requested = ShowBuiltInProfilesCheckBox.IsChecked == true;
        var previousActiveCustomProfileId = _settings.ActiveCustomProfileId;
        if (!TryUpdateSettings(settings =>
            {
                settings.ShowBuiltInProfiles = requested;
                if (!settings.ShowBuiltInProfiles && settings.ActiveCustomProfile is null)
                    settings.ActiveCustomProfileId = settings.CustomProfiles
                        .OrderBy(profile => profile.SortOrder)
                        .First()
                        .Id;
            }))
        {
            _syncingControls = true;
            ShowBuiltInProfilesCheckBox.IsChecked = _settings.ShowBuiltInProfiles;
            _syncingControls = false;
            return;
        }
        RefreshProfileSelectors();
        UpdateScheduledOptimizationAvailability(resetAnchor: !string.Equals(
            previousActiveCustomProfileId,
            _settings.ActiveCustomProfileId,
            StringComparison.OrdinalIgnoreCase));
    }

    private void AdvancedProfileModeCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        AdvancedProfilePanel.Visibility = AdvancedProfileModeCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void CustomOptimizationProfilesTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmStableSuppressionDraft())
        {
            e.Handled = true;
            return;
        }

        ShowCustomConfigurationSection(showStableSuppression: false);
    }

    private void CustomStableSuppressionTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmStableSuppressionDraft())
        {
            e.Handled = true;
            return;
        }
        RefreshStableSuppressionCatalog(_settings.ActiveCustomStableStateSuppressionProfileId);
        EnsureStableSuppressionEditorSelection();
        ShowCustomConfigurationSection(showStableSuppression: true);
    }

    private void ShowCustomConfigurationSection(bool showStableSuppression)
    {
        CustomOptimizationProfilesPanel.Visibility = showStableSuppression
            ? Visibility.Collapsed
            : Visibility.Visible;
        CustomStableSuppressionPanel.Visibility = showStableSuppression
            ? Visibility.Visible
            : Visibility.Collapsed;
        CustomOptimizationProfilesTabButton.FontWeight = FontWeights.SemiBold;
        CustomStableSuppressionTabButton.FontWeight = FontWeights.SemiBold;
        ApplyPopupSelectionVisual(CustomOptimizationProfilesTabButton, !showStableSuppression);
        ApplyPopupSelectionVisual(CustomStableSuppressionTabButton, showStableSuppression);
        CustomOptimizationProfilesTabButton.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            "TextBrush");
        CustomStableSuppressionTabButton.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            "TextBrush");
    }

    private void RefreshStableSuppressionCatalog(
        string? selectCustomId = null,
        OptimizationProfile? selectBuiltInProfile = null)
    {
        var items = new List<StableSuppressionCatalogItem>
        {
            new(T("Lite"), T("BuiltInProfile"), OptimizationProfile.Lite, null),
            new(T("Turbo"), T("BuiltInProfile"), OptimizationProfile.Turbo, null),
            new(T("Ultimate"), T("BuiltInProfile"), OptimizationProfile.Ultimate, null)
        };
        items.AddRange(_settings.CustomStableStateSuppressionProfiles
            .OrderBy(profile => profile.SortOrder)
            .Select(profile => new StableSuppressionCatalogItem(
                profile.Name,
                T("CustomProfileKind"),
                null,
                profile.Id)));
        StableSuppressionCatalogList.ItemsSource = items;
        var wantedId = selectCustomId ?? _editingCustomStableSuppressionProfileId;
        StableSuppressionCatalogList.SelectedItem = wantedId is not null
            ? items.FirstOrDefault(item => string.Equals(
                item.CustomProfileId,
                wantedId,
                StringComparison.OrdinalIgnoreCase))
            : selectBuiltInProfile.HasValue
                ? items.FirstOrDefault(item => item.BuiltInProfile == selectBuiltInProfile)
                : items.FirstOrDefault();
        StableSuppressionCatalogList.SelectedItem ??= items.FirstOrDefault();
        var previous = _syncingControls;
        _syncingControls = true;
        ShowBuiltInStableSuppressionProfilesCheckBox.IsChecked =
            _settings.ShowBuiltInStableStateSuppressionProfiles;
        ShowBuiltInStableSuppressionProfilesCheckBox.IsEnabled =
            _settings.CustomStableStateSuppressionProfiles.Count > 0;
        _syncingControls = previous;
    }

    private void StableSuppressionCatalogList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || StableSuppressionCatalogList.SelectedItem is not StableSuppressionCatalogItem item) return;
        if (_stableSuppressionDraftDirty &&
            _selectedStableSuppressionCatalogItem is not null &&
            !SameStableSuppressionCatalogItem(_selectedStableSuppressionCatalogItem, item) &&
            !ConfirmStableSuppressionDraft())
        {
            var previous = _selectedStableSuppressionCatalogItem;
            _syncingControls = true;
            StableSuppressionCatalogList.SelectedItem = previous;
            _syncingControls = false;
            return;
        }

        DisplayStableSuppressionCatalogItem(item);
    }

    private void EnsureStableSuppressionEditorSelection()
    {
        if (StableSuppressionCatalogList.SelectedItem is not StableSuppressionCatalogItem item) return;
        if (_selectedStableSuppressionCatalogItem is not null &&
            SameStableSuppressionCatalogItem(_selectedStableSuppressionCatalogItem, item) &&
            StableSuppressionEditorScroll.Visibility == Visibility.Visible) return;

        DisplayStableSuppressionCatalogItem(item);
    }

    private void DisplayStableSuppressionCatalogItem(StableSuppressionCatalogItem item)
    {

        var custom = item.CustomProfileId is null
            ? null
            : _settings.CustomStableStateSuppressionProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, item.CustomProfileId, StringComparison.OrdinalIgnoreCase));
        if (item.CustomProfileId is not null && custom is null) return;
        _editingCustomStableSuppressionProfileId = custom?.Id;
        _selectedStableSuppressionCatalogItem = item;
        SynchronizeStableSuppressionCopyButton(item);
        DeleteStableSuppressionProfileButton.IsEnabled = custom is not null;
        MoveStableSuppressionProfileUpButton.IsEnabled = custom is not null && custom.SortOrder > 0;
        MoveStableSuppressionProfileDownButton.IsEnabled = custom is not null &&
            custom.SortOrder < _settings.CustomStableStateSuppressionProfiles.Count - 1;
        StableSuppressionEditorPlaceholder.Visibility = Visibility.Collapsed;
        StableSuppressionEditorScroll.Visibility = Visibility.Visible;
        var displayed = custom ?? CustomStableStateSuppressionProfilePolicy.Create(
            item.BuiltInProfile!.Value,
            item.Name,
            0);
        _loadingStableSuppressionEditor = true;
        try
        {
            StableSuppressionProfileNameTextBox.Text = displayed.Name;
            StableSuppressionTemplateBox.SelectedIndex = (int)displayed.BaseProfile;
            LoadCustomStableSuppressionEditor(displayed.Settings);
        }
        finally
        {
            _loadingStableSuppressionEditor = false;
        }
        _stableSuppressionDraftDirty = false;
        SetStableSuppressionEditorEditable(custom is not null);
    }

    private static bool SameStableSuppressionCatalogItem(
        StableSuppressionCatalogItem left,
        StableSuppressionCatalogItem right) =>
        string.Equals(left.CustomProfileId, right.CustomProfileId, StringComparison.OrdinalIgnoreCase) &&
        left.BuiltInProfile == right.BuiltInProfile;

    private void SynchronizeStableSuppressionCopyButton(StableSuppressionCatalogItem? item)
    {
        var validSelection = item is { BuiltInProfile: not null } ||
                             (item?.CustomProfileId is not null &&
                              _settings.CustomStableStateSuppressionProfiles.Any(profile =>
                                  string.Equals(profile.Id, item.CustomProfileId, StringComparison.OrdinalIgnoreCase)));
        var reason = !validSelection
            ? T("CustomProfileCopySelectionInvalid")
            : _state.IsBusy
                ? T("CustomProfileCopyBusy")
                : !_settingsWriteAvailable
                    ? T("CustomProfileCopyUnavailable")
                    : _settings.CustomStableStateSuppressionProfiles.Count >=
                      CustomStableStateSuppressionProfilePolicy.MaximumCustomProfiles
                        ? T("CustomProfileCopyLimit")
                        : null;
        CopyStableSuppressionProfileButton.IsEnabled = reason is null;
        CopyStableSuppressionProfileButton.ToolTip = reason ?? T("CopyProfile");
    }

    private void SetStableSuppressionEditorEditable(bool editable)
    {
        StableSuppressionProfileNameTextBox.IsReadOnly = !editable;
        StableSuppressionProfileNameTextBox.IsTabStop = editable;
        StableSuppressionTemplateBox.IsEnabled = editable;
        StableSuppressionTemplateBox.IsTabStop = editable;
        StableSuppressionTemplatePanel.Visibility = editable
            ? Visibility.Visible
            : Visibility.Collapsed;
        foreach (var slider in new[]
                 {
                     StableMinimumSamplesSlider,
                     StableRecordAgeDaysSlider,
                     StableRelativeMarginSlider,
                     StableAbsoluteMarginSlider,
                     StableObservationMinutesSlider,
                     StableSampleIntervalMinutesSlider,
                     StableMaximumSamplesPerLaunchSlider,
                     StableSamplePoolSlider,
                     StableMaximumWorkingSetSlider
                 })
        {
            slider.IsEnabled = editable;
            slider.IsTabStop = editable;
        }
        StableMaximumWorkingSetUnlimitedCheckBox.IsEnabled = editable;
        StableMaximumWorkingSetUnlimitedCheckBox.IsTabStop = editable;
        StableMaximumWorkingSetSlider.IsEnabled = editable &&
            StableMaximumWorkingSetUnlimitedCheckBox.IsChecked != true;
        StableMaximumWorkingSetSlider.IsTabStop = StableMaximumWorkingSetSlider.IsEnabled;
        StableIgnorePressureCheckBox.IsEnabled = editable;
        StableIgnorePressureCheckBox.IsTabStop = editable;
        StableSuppressionEditorStateText.Text = T(editable
            ? "CustomProfileEditableState"
            : "BuiltInProfileReadOnlyState");
        StableSuppressionEditorStateText.Foreground = (MediaBrush)FindResource(
            editable ? "AccentBrush" : "MutedBrush");
        foreach (var label in new[]
                 {
                     StableSuppressionProfileNameLabel,
                     StableMinimumSamplesLabel,
                     StableRecordAgeDaysLabel,
                     StableRelativeMarginLabel,
                     StableAbsoluteMarginLabel,
                     StableObservationMinutesLabel,
                     StableSampleIntervalMinutesLabel,
                     StableMaximumSamplesPerLaunchLabel,
                     StableSamplePoolLabel,
                     StableMaximumWorkingSetLabel,
                     StableIgnorePressureLabel
                 })
        {
            label.SetResourceReference(
                TextBlock.ForegroundProperty,
                editable ? "TextBrush" : "MutedBrush");
        }
        AutomationProperties.SetName(
            StableSuppressionProfileNameTextBox,
            $"{T("ProfileName")} - {StableSuppressionEditorStateText.Text}");
        SaveCustomStableSuppressionButton.IsEnabled = editable;
        SaveCustomStableSuppressionButton.Visibility = editable
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CopyStableSuppressionProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy || !_settingsWriteAvailable ||
            StableSuppressionCatalogList.SelectedItem is not StableSuppressionCatalogItem item) return;
        if (_settings.CustomStableStateSuppressionProfiles.Count >=
            CustomStableStateSuppressionProfilePolicy.MaximumCustomProfiles) return;
        var custom = item.CustomProfileId is null
            ? null
            : _settings.CustomStableStateSuppressionProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, item.CustomProfileId, StringComparison.OrdinalIgnoreCase));
        CustomStableStateSuppressionProfile source;
        if (custom is not null)
        {
            source = custom;
            if (_stableSuppressionDraftDirty)
            {
                var choice = ShowThemedMessage(
                    T("CopyProfileDraftChoiceTitle"),
                    T("CopyProfileDraftChoiceMessage"),
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question,
                    affirmativeText: T("DialogUseCurrentChanges"),
                    negativeText: T("DialogUseSavedVersion"),
                    cancelText: T("Cancel"));
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes)
                {
                    if (!TryReadStableSuppressionDraft(custom, out var draft)) return;
                    source = draft;
                }
            }
        }
        else if (item.BuiltInProfile is { } baseProfile)
        {
            source = CustomStableStateSuppressionProfilePolicy.Create(baseProfile, item.Name, 0);
        }
        else
        {
            return;
        }

        var baseName = string.IsNullOrWhiteSpace(source.Name) ? item.Name : source.Name;
        var name = TF("ProfileCopyNameFormat", baseName);
        for (var suffix = 2;
             !CustomStableStateSuppressionProfilePolicy.IsUniqueName(
                 _settings.CustomStableStateSuppressionProfiles,
                 name);
             suffix++)
        {
            name = TF("ProfileCopyNumberedNameFormat", baseName, suffix);
        }
        string? createdId = null;
        if (!TryUpdateSettings(settings =>
            {
                createdId = CustomStableStateSuppressionProfileSettingsOperations
                    .AddCopy(settings, source, name)
                    .Id;
            })) return;
        _stableSuppressionDraftDirty = false;
        RefreshStableSuppressionCatalog(createdId);
        SynchronizeStableStateSuppressionControls();
    }

    private void MoveStableSuppressionProfileUp_OnClick(object sender, RoutedEventArgs e) =>
        MoveEditingStableSuppressionProfile(-1);

    private void MoveStableSuppressionProfileDown_OnClick(object sender, RoutedEventArgs e) =>
        MoveEditingStableSuppressionProfile(1);

    private void MoveEditingStableSuppressionProfile(int offset)
    {
        var ordered = _settings.CustomStableStateSuppressionProfiles
            .OrderBy(profile => profile.SortOrder)
            .ToList();
        var index = ordered.FindIndex(profile => string.Equals(
            profile.Id,
            _editingCustomStableSuppressionProfileId,
            StringComparison.OrdinalIgnoreCase));
        var target = index + offset;
        if (index < 0 || target < 0 || target >= ordered.Count) return;
        if (!TryUpdateSettings(settings =>
            {
                var profiles = settings.CustomStableStateSuppressionProfiles
                    .OrderBy(profile => profile.SortOrder)
                    .ToList();
                (profiles[index], profiles[target]) = (profiles[target], profiles[index]);
                for (var itemIndex = 0; itemIndex < profiles.Count; itemIndex++)
                    profiles[itemIndex].SortOrder = itemIndex;
                settings.CustomStableStateSuppressionProfiles = profiles;
            })) return;
        RefreshStableSuppressionCatalog(_editingCustomStableSuppressionProfileId);
        SynchronizeStableStateSuppressionControls();
    }

    private void DeleteStableSuppressionProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var orderedProfiles = _settings.CustomStableStateSuppressionProfiles
            .OrderBy(candidate => candidate.SortOrder)
            .ToList();
        var profileIndex = orderedProfiles.FindIndex(candidate => string.Equals(
            candidate.Id,
            _editingCustomStableSuppressionProfileId,
            StringComparison.OrdinalIgnoreCase));
        if (profileIndex < 0) return;
        var profile = orderedProfiles[profileIndex];
        var previousProfileId = profileIndex > 0 ? orderedProfiles[profileIndex - 1].Id : null;
        if (!TryUpdateSettings(settings =>
                CustomStableStateSuppressionProfileSettingsOperations.Remove(settings, profile.Id))) return;
        _editingCustomStableSuppressionProfileId = previousProfileId;
        RefreshStableSuppressionCatalog(
            previousProfileId,
            previousProfileId is null ? OptimizationProfile.Ultimate : null);
        SynchronizeStableStateSuppressionControls();
        UpdatePreviewRows();
    }

    private void ShowBuiltInStableSuppressionProfilesCheckBox_OnChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (_syncingControls) return;
        if (_settings.CustomStableStateSuppressionProfiles.Count == 0)
        {
            _syncingControls = true;
            ShowBuiltInStableSuppressionProfilesCheckBox.IsChecked = true;
            _syncingControls = false;
            return;
        }
        var requested = ShowBuiltInStableSuppressionProfilesCheckBox.IsChecked == true;
        if (!TryUpdateSettings(settings =>
            {
                settings.ShowBuiltInStableStateSuppressionProfiles = requested;
                if (!requested && settings.StableStateSuppressionMode is
                    StableStateSuppressionMode.ReduceRepeatedOptimization or
                    StableStateSuppressionMode.Balanced or
                    StableStateSuppressionMode.FasterReevaluation)
                {
                    settings.ActiveCustomStableStateSuppressionProfileId = settings
                        .CustomStableStateSuppressionProfiles
                        .OrderBy(profile => profile.SortOrder)
                        .First()
                        .Id;
                    settings.StableStateSuppressionMode = StableStateSuppressionMode.Custom;
                }
            }))
        {
            _syncingControls = true;
            ShowBuiltInStableSuppressionProfilesCheckBox.IsChecked =
                _settings.ShowBuiltInStableStateSuppressionProfiles;
            _syncingControls = false;
            return;
        }
        SynchronizeStableStateSuppressionControls();
    }

    private void StableSuppressionTemplateBox_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingStableSuppressionEditor ||
            _editingCustomStableSuppressionProfileId is null ||
            StableSuppressionTemplateBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<OptimizationProfile>(tag, out var profile)) return;

        LoadCustomStableSuppressionEditor(StableStateSuppressionSettings.For(profile));
        _stableSuppressionDraftDirty = true;
        CustomStableSuppressionValidationText.Text = T("CustomStableSuppressionDraftUnsaved");
    }

    private void StableSuppressionDraft_OnChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingStableSuppressionEditor ||
            _editingCustomStableSuppressionProfileId is null) return;
        _stableSuppressionDraftDirty = true;
        CustomStableSuppressionValidationText.Text = T("CustomStableSuppressionDraftUnsaved");
    }

    private void StableSuppressionDraftSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (ReferenceEquals(sender, StableMaximumWorkingSetSlider))
            UpdateStableMaximumWorkingSetValueText();
        if (_loadingStableSuppressionEditor ||
            _editingCustomStableSuppressionProfileId is null) return;
        if (ReferenceEquals(sender, StableMinimumSamplesSlider) &&
            StableMinimumSamplesSlider.Value > StableSamplePoolSlider.Value)
        {
            StableSamplePoolSlider.Value = StableMinimumSamplesSlider.Value;
        }
        else if (ReferenceEquals(sender, StableSamplePoolSlider) &&
                 StableSamplePoolSlider.Value < StableMinimumSamplesSlider.Value)
        {
            StableMinimumSamplesSlider.Value = StableSamplePoolSlider.Value;
        }
        _stableSuppressionDraftDirty = true;
        CustomStableSuppressionValidationText.Text = T("CustomStableSuppressionDraftUnsaved");
    }

    private void StableMaximumWorkingSetUnlimitedCheckBox_OnChanged(
        object sender,
        RoutedEventArgs e)
    {
        var editable = _editingCustomStableSuppressionProfileId is not null;
        StableMaximumWorkingSetSlider.IsEnabled = editable &&
            StableMaximumWorkingSetUnlimitedCheckBox.IsChecked != true;
        StableMaximumWorkingSetSlider.IsTabStop = StableMaximumWorkingSetSlider.IsEnabled;
        UpdateStableMaximumWorkingSetValueText();
        if (_loadingStableSuppressionEditor || !editable) return;
        _stableSuppressionDraftDirty = true;
        CustomStableSuppressionValidationText.Text = T("CustomStableSuppressionDraftUnsaved");
    }

    private void StableSuppressionDraftCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingStableSuppressionEditor ||
            _editingCustomStableSuppressionProfileId is null) return;
        _stableSuppressionDraftDirty = true;
        CustomStableSuppressionValidationText.Text = T("CustomStableSuppressionDraftUnsaved");
    }

    private bool ConfirmStableSuppressionDraft()
    {
        if (!_stableSuppressionDraftDirty) return true;
        var current = _settings.CustomStableStateSuppressionProfiles.FirstOrDefault(profile =>
            string.Equals(
                profile.Id,
                _editingCustomStableSuppressionProfileId,
                StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            _stableSuppressionDraftDirty = false;
            return true;
        }

        var choice = ShowThemedMessage(
            T("UnsavedChangesTitle"),
            T("StableSuppressionDraftChoiceMessage"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            affirmativeText: T("DialogSave"),
            negativeText: T("DialogDiscard"),
            cancelText: T("DialogStay"));
        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.No)
        {
            _stableSuppressionDraftDirty = false;
            CustomStableSuppressionValidationText.Text = string.Empty;
            return true;
        }

        return TrySaveStableSuppressionDraft(current);
    }

    private void LoadCustomStableSuppressionEditor(StableStateSuppressionSettings settings)
    {
        settings = settings.Normalize();
        StableMinimumSamplesSlider.Value = settings.MinimumSamples;
        StableRecordAgeDaysSlider.Value = settings.MaximumRecordAge.TotalDays;
        StableRelativeMarginSlider.Value = settings.RelativeGrowthMargin * 100d;
        StableAbsoluteMarginSlider.Value = settings.AbsoluteGrowthMarginBytes / (1024d * 1024d);
        StableObservationMinutesSlider.Value = settings.MaximumStableValidationDuration.TotalMinutes;
        StableSampleIntervalMinutesSlider.Value = settings.NaturalStableSampleInterval.TotalMinutes;
        StableMaximumSamplesPerLaunchSlider.Value = settings.MaximumStableSamplesPerLaunch;
        StableSamplePoolSlider.Value = settings.MaximumStableSamplePool;
        var unlimitedMaximum = settings.MaximumStableWorkingSetBytes == long.MaxValue;
        StableMaximumWorkingSetSlider.Value = unlimitedMaximum
            ? 512
            : settings.MaximumStableWorkingSetBytes / (1024d * 1024d);
        StableMaximumWorkingSetUnlimitedCheckBox.IsChecked = unlimitedMaximum;
        StableIgnorePressureCheckBox.IsChecked =
            settings.IgnoreRegularObservationUnderSeverePressure;
        UpdateStableMaximumWorkingSetValueText();
        CustomStableSuppressionValidationText.Text = string.Empty;
    }

    private void UpdateStableMaximumWorkingSetValueText()
    {
        if (StableMaximumWorkingSetValueText is null ||
            StableMaximumWorkingSetSlider is null ||
            StableMaximumWorkingSetUnlimitedCheckBox is null) return;
        StableMaximumWorkingSetValueText.Text =
            StableMaximumWorkingSetUnlimitedCheckBox.IsChecked == true
                ? T("StableMaximumWorkingSetUnlimitedValue")
                : StableMaximumWorkingSetSlider.Value.ToString("0");
    }

    private void SaveCustomStableSuppression_OnClick(object sender, RoutedEventArgs e)
    {
        var current = _settings.CustomStableStateSuppressionProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, _editingCustomStableSuppressionProfileId, StringComparison.OrdinalIgnoreCase));
        if (current is null) return;
        TrySaveStableSuppressionDraft(current);
    }

    private bool TryReadStableSuppressionDraft(
        CustomStableStateSuppressionProfile current,
        out CustomStableStateSuppressionProfile draft)
    {
        draft = null!;
        var baseProfile = current.BaseProfile;
        if (StableSuppressionTemplateBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<OptimizationProfile>(tag, out var selectedTemplate))
        {
            baseProfile = selectedTemplate;
        }
        var name = StableSuppressionProfileNameTextBox.Text.Trim();
        if (!CustomStableStateSuppressionProfilePolicy.IsUniqueName(
                _settings.CustomStableStateSuppressionProfiles,
                name,
                current.Id))
        {
            CustomStableSuppressionValidationText.Text = T("CustomProfileNameInvalid");
            return false;
        }
        var minimumSamples = (int)Math.Round(StableMinimumSamplesSlider.Value);
        var recordAgeDays = (int)Math.Round(StableRecordAgeDaysSlider.Value);
        var relativeMarginPercent = (int)Math.Round(StableRelativeMarginSlider.Value);
        var absoluteMarginMiB = (int)Math.Round(StableAbsoluteMarginSlider.Value);
        var observationMinutes = (int)Math.Round(StableObservationMinutesSlider.Value);
        var sampleIntervalMinutes = (int)Math.Round(StableSampleIntervalMinutesSlider.Value);
        var maximumSamplesPerLaunch = (int)Math.Round(StableMaximumSamplesPerLaunchSlider.Value);
        var maximumStableSamplePool = (int)Math.Round(StableSamplePoolSlider.Value);
        var maximumStableWorkingSetBytes = StableMaximumWorkingSetUnlimitedCheckBox.IsChecked == true
            ? long.MaxValue
            : MiB((int)Math.Round(StableMaximumWorkingSetSlider.Value));

        StableStateSuppressionSettings requested;
        try
        {
            requested = new StableStateSuppressionSettings(
                minimumSamples,
                TimeSpan.FromDays(recordAgeDays),
                relativeMarginPercent / 100d,
                MiB(absoluteMarginMiB))
            {
                MaximumStableValidationDuration = TimeSpan.FromMinutes(observationMinutes),
                IgnoreRegularObservationUnderSeverePressure =
                    StableIgnorePressureCheckBox.IsChecked == true,
                NaturalStableSampleInterval = TimeSpan.FromMinutes(sampleIntervalMinutes),
                MaximumStableSamplesPerLaunch = maximumSamplesPerLaunch,
                MaximumStableSamplePool = maximumStableSamplePool,
                MaximumStableWorkingSetBytes = maximumStableWorkingSetBytes
            }.Normalize();
        }
        catch (OverflowException)
        {
            CustomStableSuppressionValidationText.Text = T("CustomProfileNumberInvalid");
            return false;
        }

        draft = CustomStableStateSuppressionProfilePolicy.Normalize(new CustomStableStateSuppressionProfile
        {
            Id = current.Id,
            Name = name,
            BaseProfile = baseProfile,
            SortOrder = current.SortOrder,
            Settings = requested
        });
        return true;
    }

    private bool TrySaveStableSuppressionDraft(CustomStableStateSuppressionProfile current)
    {
        if (!TryReadStableSuppressionDraft(current, out var updated)) return false;
        if (!TryUpdateSettings(settings =>
            {
                var index = settings.CustomStableStateSuppressionProfiles.FindIndex(profile =>
                    string.Equals(profile.Id, current.Id, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return;
                settings.CustomStableStateSuppressionProfiles[index] = updated;
                settings.CustomStableStateSuppression = updated.Settings;
            }))
        {
            CustomStableSuppressionValidationText.Text = _state.Status;
            return false;
        }

        _stableSuppressionDraftDirty = false;
        RefreshStableSuppressionCatalog(current.Id);
        SynchronizeStableStateSuppressionControls();
        UpdatePreviewRows();
        CustomStableSuppressionValidationText.Text = T("CustomStableSuppressionSaved");
        return true;
    }

    private void LoadCustomProfileEditor(CustomOptimizationProfile profile)
    {
        _loadingCustomProfileEditor = true;
        try
        {
            var settings = profile.Settings;
            var rebound = profile.Rebound;
            ApplyCustomProfileBounds(profile.BaseProfile);
            CustomProfileNameTextBox.Text = profile.Name;
            MaxApplicationsSlider.Value = settings.MaxApplications;
            TriggerPercentSlider.Value = MemoryTriggerPresentation.ToUsagePercent(settings.TriggerAvailablePercent);
            MinFamilyMemorySlider.Value = settings.MinimumFamilyWorkingSetBytes / (1024d * 1024d);
            VisibleWindowIdleDelaySlider.Value = settings.VisibleWindowIdleDelay.TotalMinutes;
            ProcessCooldownSlider.Value = settings.ProcessCooldown.TotalSeconds;
            AutoCooldownSlider.Value = settings.AutoCooldown.TotalSeconds;
            EarlyReboundSlider.Value = rebound.EarlyReboundPercent;
            LateReboundSlider.Value = rebound.LateReboundPercent;
            FirstBackoffSlider.Value = rebound.FirstBackoff.TotalMinutes;
            SecondBackoffSlider.Value = rebound.SecondBackoff.TotalMinutes;
            MinProcessMemoryTextBox.Text = (settings.MinimumProcessWorkingSetBytes / (1024d * 1024d)).ToString("0.##", CultureInfo.CurrentCulture);
            TriggerGiBTextBox.Text = (settings.TriggerAvailableBytes / (1024d * 1024d * 1024d)).ToString("0.##", CultureInfo.CurrentCulture);
            EarlyWindowTextBox.Text = rebound.EarlyWindow.TotalSeconds.ToString("0", CultureInfo.CurrentCulture);
            LateWindowTextBox.Text = rebound.LateWindow.TotalSeconds.ToString("0", CultureInfo.CurrentCulture);
            ActiveCpuThresholdSlider.Value = settings.ActiveCpuThresholdPercent;
            ActiveIoThresholdSlider.Value = settings.ActiveIoThresholdBytesPerSecond / (1024d * 1024d);
            IgnorePressureCheckBox.IsChecked = settings.IgnoreMemoryPressureThreshold;
            UpdateTriggerThresholdPresentation();
            AllowForegroundCheckBox.IsChecked = settings.AllowForegroundProcessTrim;
            AllowIndependentBackgroundProcessTrimCheckBox.IsChecked = settings.AllowIndependentBackgroundProcessTrim;
            CustomProfileValidationText.Text = string.Empty;
        }
        finally
        {
            _loadingCustomProfileEditor = false;
            _customProfileDraftDirty = false;
        }
    }

    private void MarkCustomProfileDraftDirty()
    {
        if (_loadingCustomProfileEditor || _editingCustomProfileId is null) return;
        _customProfileDraftDirty = true;
        CustomProfileValidationText.Text = T("CustomProfileDraftUnsaved");
    }

    private void CustomProfileDraft_OnChanged(object sender, TextChangedEventArgs e) =>
        MarkCustomProfileDraftDirty();

    private void CustomProfileDraftSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e) =>
        MarkCustomProfileDraftDirty();

    private void CustomProfileCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        MarkCustomProfileDraftDirty();

    private void TriggerPercentSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTriggerThresholdPresentation();
        MarkCustomProfileDraftDirty();
    }

    private void ActivityThresholdSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_currentCustomProfileBounds is null || sender is not Slider slider) return;
        var bounds = ReferenceEquals(slider, ActiveCpuThresholdSlider)
            ? (_currentCustomProfileBounds.MinActiveCpuPercent, _currentCustomProfileBounds.MaxActiveCpuPercent)
            : (_currentCustomProfileBounds.MinActiveIoMiBPerSecond, _currentCustomProfileBounds.MaxActiveIoMiBPerSecond);
        var clamped = Math.Clamp(e.NewValue, bounds.Item1, bounds.Item2);
        if (Math.Abs(slider.Value - clamped) > double.Epsilon) slider.Value = clamped;
        MarkCustomProfileDraftDirty();
    }

    private void IgnorePressureCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        UpdateTriggerThresholdPresentation();
        MarkCustomProfileDraftDirty();
    }

    private void UpdateTriggerThresholdPresentation()
    {
        if (TriggerPercentValueRun is null || TriggerPercentSlider is null || IgnorePressureCheckBox is null) return;
        var ignored = IgnorePressureCheckBox.IsChecked == true;
        TriggerPercentValueRun.Text = ignored
            ? T("TriggerThresholdIgnored")
            : $"{TriggerPercentSlider.Value:0}%";
        TriggerPercentSlider.Visibility = ignored ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetCustomProfileEditorEditable(bool editable, OptimizationProfile baseProfile)
    {
        CustomProfileNameTextBox.IsReadOnly = !editable;
        foreach (var slider in new[]
        {
            TriggerPercentSlider, MaxApplicationsSlider, MinFamilyMemorySlider,
            VisibleWindowIdleDelaySlider,
            ProcessCooldownSlider, AutoCooldownSlider, EarlyReboundSlider, LateReboundSlider,
            FirstBackoffSlider, SecondBackoffSlider, ActiveCpuThresholdSlider, ActiveIoThresholdSlider
        })
        {
            slider.IsHitTestVisible = editable;
            slider.Focusable = editable;
            slider.Opacity = editable ? 1 : 0.42;
        }
        foreach (var textBox in new[]
        {
            MinProcessMemoryTextBox, TriggerGiBTextBox, EarlyWindowTextBox, LateWindowTextBox
        })
        {
            textBox.IsReadOnly = !editable;
            textBox.IsTabStop = editable;
            textBox.Opacity = editable ? 1 : 0.68;
        }
        foreach (var checkBox in new[]
                 {
                     AllowIndependentBackgroundProcessTrimCheckBox
                 })
        {
            SetCustomProfileCheckBoxEditable(checkBox, editable);
        }
        var sourceAllowsRiskOverrides = baseProfile == OptimizationProfile.Ultimate;
        SetCustomProfileCheckBoxEditable(IgnorePressureCheckBox, editable && sourceAllowsRiskOverrides);
        SetCustomProfileCheckBoxEditable(AllowForegroundCheckBox, editable && sourceAllowsRiskOverrides);
        ResetProfileButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveCustomProfileButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        CustomProfileValidationText.Foreground = (MediaBrush)FindResource(editable ? "UltimateBrush" : "MutedBrush");
        CustomProfileValidationText.Text = editable ? string.Empty : T("BuiltInProfileReadOnly");
    }

    private static void SetCustomProfileCheckBoxEditable(
        System.Windows.Controls.CheckBox checkBox,
        bool editable)
    {
        checkBox.IsEnabled = editable;
        checkBox.IsHitTestVisible = editable;
        checkBox.Focusable = editable;
        checkBox.Opacity = editable ? 1 : 0.55;
    }

    private void ApplyCustomProfileBounds(OptimizationProfile baseProfile)
    {
        var bounds = baseProfile switch
        {
            OptimizationProfile.Lite => new SliderBounds(1, 40, 96, 1024, 45, 90, 5, 48, 18, 600, 90, 900, 3, 15, 2, 15, 1, 8),
            OptimizationProfile.Turbo => new SliderBounds(2, 40, 2, 280, 20, 65, 10, 70, 5, 120, 30, 300, 1, 10, 4, 25, 2, 16),
            OptimizationProfile.Ultimate => new SliderBounds(7, 40, 2, 96, 5, 45, 1, 95, 1, 18, 15, 120, 0, 5, 8, 50, 4, 32),
            _ => throw new ArgumentOutOfRangeException(nameof(baseProfile), baseProfile, null)
        };
        _currentCustomProfileBounds = bounds;
        (MaxApplicationsSlider.Minimum, MaxApplicationsSlider.Maximum) = (bounds.MinApplications, bounds.MaxApplications);
        (MinFamilyMemorySlider.Minimum, MinFamilyMemorySlider.Maximum) = (bounds.MinFamilyMiB, bounds.MaxFamilyMiB);
        (TriggerPercentSlider.Minimum, TriggerPercentSlider.Maximum) =
            MemoryTriggerPresentation.UsageBounds(bounds.MinTriggerPercent, bounds.MaxTriggerPercent);
        (ProcessCooldownSlider.Minimum, ProcessCooldownSlider.Maximum) = (bounds.MinProcessCooldown, bounds.MaxProcessCooldown);
        (AutoCooldownSlider.Minimum, AutoCooldownSlider.Maximum) = (bounds.MinAutoCooldown, bounds.MaxAutoCooldown);
        (VisibleWindowIdleDelaySlider.Minimum, VisibleWindowIdleDelaySlider.Maximum) =
            (bounds.MinVisibleWindowIdleMinutes, bounds.MaxVisibleWindowIdleMinutes);
    }

    private void ResetProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var current = _settings.CustomProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, _editingCustomProfileId, StringComparison.OrdinalIgnoreCase));
        if (current is null) return;
        var reset = CustomProfilePolicy.Create(current.BaseProfile, current.Name, current.SortOrder);
        reset.Id = current.Id;
        LoadCustomProfileEditor(reset);
        _customProfileDraftDirty = true;
        CustomProfileValidationText.Text = T("CustomProfileDraftUnsaved");
    }

    private void SaveCustomProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var index = _settings.CustomProfiles.FindIndex(profile =>
            string.Equals(profile.Id, _editingCustomProfileId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var current = _settings.CustomProfiles[index];
        var name = CustomProfileNameTextBox.Text.Trim();
        if (!CustomProfilePolicy.IsUniqueName(_settings.CustomProfiles, name, current.Id))
        {
            CustomProfileValidationText.Text = T("CustomProfileNameInvalid");
            return;
        }
        if (!TryReadNumber(MinProcessMemoryTextBox.Text, out var minimumProcessMiB) ||
            !TryReadNumber(TriggerGiBTextBox.Text, out var triggerGiB) ||
            !TryReadNumber(EarlyWindowTextBox.Text, out var earlyWindowSeconds) ||
            !TryReadNumber(LateWindowTextBox.Text, out var lateWindowSeconds))
        {
            CustomProfileValidationText.Text = T("CustomProfileNumberInvalid");
            return;
        }

        long minimumProcessWorkingSetBytes;
        ulong triggerAvailableBytes;
        TimeSpan earlyWindow;
        TimeSpan lateWindow;
        try
        {
            minimumProcessWorkingSetBytes = MiB(minimumProcessMiB);
            triggerAvailableBytes = GiB(triggerGiB);
            earlyWindow = TimeSpan.FromSeconds(earlyWindowSeconds);
            lateWindow = TimeSpan.FromSeconds(lateWindowSeconds);
        }
        catch (OverflowException)
        {
            CustomProfileValidationText.Text = T("CustomProfileNumberInvalid");
            return;
        }

        var settings = current.Settings with
        {
            MaxApplications = (int)Math.Round(MaxApplicationsSlider.Value),
            TriggerAvailablePercent = MemoryTriggerPresentation.ToAvailablePercent(TriggerPercentSlider.Value),
            MinimumFamilyWorkingSetBytes = MiB(MinFamilyMemorySlider.Value),
            MinimumProcessWorkingSetBytes = minimumProcessWorkingSetBytes,
            TriggerAvailableBytes = triggerAvailableBytes,
            ProcessCooldown = TimeSpan.FromSeconds(ProcessCooldownSlider.Value),
            AutoCooldown = TimeSpan.FromSeconds(AutoCooldownSlider.Value),
            VisibleWindowIdleDelay = TimeSpan.FromMinutes(VisibleWindowIdleDelaySlider.Value),
            ActiveCpuThresholdPercent = Math.Clamp(
                ActiveCpuThresholdSlider.Value,
                _currentCustomProfileBounds?.MinActiveCpuPercent ?? ActiveCpuThresholdSlider.Minimum,
                _currentCustomProfileBounds?.MaxActiveCpuPercent ?? ActiveCpuThresholdSlider.Maximum),
            ActiveIoThresholdBytesPerSecond = Math.Clamp(
                ActiveIoThresholdSlider.Value,
                _currentCustomProfileBounds?.MinActiveIoMiBPerSecond ?? ActiveIoThresholdSlider.Minimum,
                _currentCustomProfileBounds?.MaxActiveIoMiBPerSecond ?? ActiveIoThresholdSlider.Maximum) * 1024d * 1024d,
            IgnoreMemoryPressureThreshold = IgnorePressureCheckBox.IsChecked == true,
            AllowForegroundProcessTrim = AllowForegroundCheckBox.IsChecked == true,
            AllowIndependentBackgroundProcessTrim = AllowIndependentBackgroundProcessTrimCheckBox.IsChecked == true,
            ProtectGamingProcesses = false,
            EnhancedSafety = false
        };
        var updated = CustomProfilePolicy.Normalize(new CustomOptimizationProfile
        {
            Id = current.Id,
            Name = name,
            BaseProfile = current.BaseProfile,
            SortOrder = current.SortOrder,
            Settings = settings,
            Rebound = new ReboundBackoffSettings(
                earlyWindow,
                EarlyReboundSlider.Value,
                lateWindow,
                LateReboundSlider.Value,
                TimeSpan.FromMinutes(FirstBackoffSlider.Value),
                TimeSpan.FromMinutes(SecondBackoffSlider.Value)),
            StableStateSuppression = current.StableStateSuppression,
            StableStateSuppressionMode = current.StableStateSuppressionMode
        });
        var updatedActiveProfile = string.Equals(
            updated.Id,
            _settings.ActiveCustomProfileId,
            StringComparison.OrdinalIgnoreCase);
        if (!TryUpdateSettings(settings => settings.CustomProfiles[index] = updated))
        {
            CustomProfileValidationText.Text = _state.Status;
            return;
        }
        _customProfileDraftDirty = false;
        RefreshCustomProfileCatalog(updated.Id);
        RefreshProfileSelectors();
        UpdateScheduledOptimizationAvailability(resetAnchor: updatedActiveProfile);
        CustomProfileValidationText.Text = T("CustomProfileSaved");
    }

    private bool TryBuildCustomProfileDraft(
        CustomOptimizationProfile current,
        out CustomOptimizationProfile draft)
    {
        draft = null!;
        var name = CustomProfileNameTextBox.Text.Trim();
        if (!CustomProfilePolicy.IsUniqueName(_settings.CustomProfiles, name, current.Id))
        {
            CustomProfileValidationText.Text = T("CustomProfileNameInvalid");
            return false;
        }
        if (!TryReadNumber(MinProcessMemoryTextBox.Text, out var minimumProcessMiB) ||
            !TryReadNumber(TriggerGiBTextBox.Text, out var triggerGiB) ||
            !TryReadNumber(EarlyWindowTextBox.Text, out var earlyWindowSeconds) ||
            !TryReadNumber(LateWindowTextBox.Text, out var lateWindowSeconds))
        {
            CustomProfileValidationText.Text = T("CustomProfileNumberInvalid");
            return false;
        }

        long minimumProcessWorkingSetBytes;
        ulong triggerAvailableBytes;
        TimeSpan earlyWindow;
        TimeSpan lateWindow;
        try
        {
            minimumProcessWorkingSetBytes = MiB(minimumProcessMiB);
            triggerAvailableBytes = GiB(triggerGiB);
            earlyWindow = TimeSpan.FromSeconds(earlyWindowSeconds);
            lateWindow = TimeSpan.FromSeconds(lateWindowSeconds);
        }
        catch (OverflowException)
        {
            CustomProfileValidationText.Text = T("CustomProfileNumberInvalid");
            return false;
        }

        var settings = current.Settings with
        {
            MaxApplications = (int)Math.Round(MaxApplicationsSlider.Value),
            TriggerAvailablePercent = MemoryTriggerPresentation.ToAvailablePercent(TriggerPercentSlider.Value),
            MinimumFamilyWorkingSetBytes = MiB(MinFamilyMemorySlider.Value),
            MinimumProcessWorkingSetBytes = minimumProcessWorkingSetBytes,
            TriggerAvailableBytes = triggerAvailableBytes,
            ProcessCooldown = TimeSpan.FromSeconds(ProcessCooldownSlider.Value),
            AutoCooldown = TimeSpan.FromSeconds(AutoCooldownSlider.Value),
            VisibleWindowIdleDelay = TimeSpan.FromMinutes(VisibleWindowIdleDelaySlider.Value),
            ActiveCpuThresholdPercent = Math.Clamp(
                ActiveCpuThresholdSlider.Value,
                _currentCustomProfileBounds?.MinActiveCpuPercent ?? ActiveCpuThresholdSlider.Minimum,
                _currentCustomProfileBounds?.MaxActiveCpuPercent ?? ActiveCpuThresholdSlider.Maximum),
            ActiveIoThresholdBytesPerSecond = Math.Clamp(
                ActiveIoThresholdSlider.Value,
                _currentCustomProfileBounds?.MinActiveIoMiBPerSecond ?? ActiveIoThresholdSlider.Minimum,
                _currentCustomProfileBounds?.MaxActiveIoMiBPerSecond ?? ActiveIoThresholdSlider.Maximum) * 1024d * 1024d,
            IgnoreMemoryPressureThreshold = IgnorePressureCheckBox.IsChecked == true,
            AllowForegroundProcessTrim = AllowForegroundCheckBox.IsChecked == true,
            AllowIndependentBackgroundProcessTrim = AllowIndependentBackgroundProcessTrimCheckBox.IsChecked == true,
            ProtectGamingProcesses = false,
            EnhancedSafety = false
        };
        draft = CustomProfilePolicy.Normalize(new CustomOptimizationProfile
        {
            Id = current.Id,
            Name = name,
            BaseProfile = current.BaseProfile,
            SortOrder = current.SortOrder,
            Settings = settings,
            Rebound = new ReboundBackoffSettings(
                earlyWindow,
                EarlyReboundSlider.Value,
                lateWindow,
                LateReboundSlider.Value,
                TimeSpan.FromMinutes(FirstBackoffSlider.Value),
                TimeSpan.FromMinutes(SecondBackoffSlider.Value)),
            StableStateSuppression = current.StableStateSuppression,
            StableStateSuppressionMode = current.StableStateSuppressionMode
        });
        return true;
    }

    private static bool TryReadNumber(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
        double.IsFinite(value) && value >= 0;

    private static bool TryReadInteger(string text, out int value)
    {
        value = 0;
        return TryReadNumber(text, out var number) &&
               number <= int.MaxValue &&
               Math.Abs(number - Math.Round(number)) < double.Epsilon &&
               (value = (int)number) >= 0;
    }

    private static long MiB(double value) => checked((long)Math.Round(value * 1024d * 1024d));
    private static ulong GiB(double value) => checked((ulong)Math.Round(value * 1024d * 1024d * 1024d));

    private void Nav_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string targetName || FindName(targetName) is not Grid page) return;
        var title = targetName switch { "OverviewPage" => "概览", "ProcessesPage" => "进程", "ProtectionPage" => "保护", "ApplicationRulesPage" => "应用优化规则", "HistoryPage" => "历史与分析", "CustomPage" => "自定义", "SettingsPage" => "设置", _ => "MuseRAM" };
        if (page == HistoryPage) _historyAnalysisTab = "Activity";
        SelectNavigation(page, title);
        if (page == CustomPage) ResetCustomPageEntryState();
    }

    private void ResetCustomPageEntryState()
    {
        _editingCustomProfileId = null;
        RefreshCustomProfileCatalog(selectBuiltInProfile: OptimizationProfile.Lite);
        ShowCustomConfigurationSection(showStableSuppression: false);
        _ = Dispatcher.BeginInvoke(() =>
        {
            CustomEditorScroll.ScrollToTop();
            StableSuppressionEditorScroll.ScrollToTop();
            if (CustomProfileCatalogList.SelectedItem is not null)
                CustomProfileCatalogList.ScrollIntoView(CustomProfileCatalogList.SelectedItem);
            if (StableSuppressionCatalogList.SelectedItem is not null)
                StableSuppressionCatalogList.ScrollIntoView(StableSuppressionCatalogList.SelectedItem);
        }, DispatcherPriority.Loaded);
    }

    private void SelectNavigation(Grid page, string title)
    {
        _currentPageName = page.Name;
        foreach (var candidate in new[] { OverviewPage, ProcessesPage, ProtectionPage, ApplicationRulesPage, HistoryPage, CustomPage, SettingsPage })
            candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;
        foreach (var navigation in new[] { OverviewNav, ProcessesNav, ProtectionNav, ApplicationRulesNav, HistoryNav, CustomNav, SettingsNav })
            navigation.IsChecked = string.Equals(navigation.Tag as string, _currentPageName, StringComparison.Ordinal);
        PageTitle.Text = PageResourceKey(_currentPageName) is { } key ? T(key) : title;
        UpdateVisibleProcessCollections();
        if (page == HistoryPage) SelectHistoryAnalysisTab(_historyAnalysisTab);
    }

    private void HistoryAnalysisTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tab }) SelectHistoryAnalysisTab(tab);
    }

    private void SelectHistoryAnalysisTab(string tab)
    {
        _historyAnalysisTab = tab is "Rebound" or "Learning" ? tab : "Activity";
        ActivityHistoryPanel.Visibility = _historyAnalysisTab == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        ReboundHistoryPanel.Visibility = _historyAnalysisTab == "Rebound" ? Visibility.Visible : Visibility.Collapsed;
        BenefitLearningPanel.Visibility = _historyAnalysisTab == "Learning" ? Visibility.Visible : Visibility.Collapsed;
        ActivityHistoryTabButton.IsChecked = _historyAnalysisTab == "Activity";
        ReboundHistoryTabButton.IsChecked = _historyAnalysisTab == "Rebound";
        BenefitLearningTabButton.IsChecked = _historyAnalysisTab == "Learning";

        if (_historyAnalysisTab == "Rebound") RefreshReboundAnalysis();
        if (_historyAnalysisTab == "Learning")
        {
            MarkProtectionSuggestionsViewed();
            RefreshBenefitLearningAnalysis();
        }
    }

    private void NavigateToHistoryAnalysis(string tab)
    {
        _historyAnalysisTab = tab;
        SelectNavigation(HistoryPage, T("History"));
    }

    private void UpdateVisibleProcessCollections()
    {
        if (_families.Count == 0 || !IsVisible) return;
        if (ProcessesPage.Visibility == Visibility.Visible) UpdateProcessRows();
        if (OverviewPage.Visibility == Visibility.Visible) UpdatePreviewRows();
    }

    private void UpdateMonitoringInterval()
    {
        var desired = MonitoringIntervalPolicy.Resolve(
            _settings.AutoOptimization,
            _reboundTracker.IsTracking(DateTimeOffset.Now) ||
            _applicationReboundDetailTracker.IsTracking(DateTimeOffset.Now));
        if (_monitorTimer.Interval != desired) _monitorTimer.Interval = desired;
    }

    private void LanguageBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || LanguageBox.SelectedItem is not UiLanguageOption option) return;
        if (!TryUpdateSettings(settings => settings.LanguageCode = option.Code))
        {
            _syncingControls = true;
            LanguageBox.SelectedItem = UiLanguageCatalog.Options.First(candidate =>
                candidate.Language == _uiLanguage);
            _syncingControls = false;
            return;
        }
        _uiLanguage = option.Language;
        ApplyLanguage(_uiLanguage);
        PageTitle.Text = T(PageResourceKey(_currentPageName) ?? "AppTitle");
    }

    private void ApplyLanguage(UiLanguage language)
    {
        foreach (var pair in UiTextCatalog.For(language)) Resources[pair.Key] = pair.Value;
        RefreshActivityHistory();
        SynchronizeStableStateSuppressionControls();
        _state.Status = _currentMemory.TotalPhysicalBytes == 0 ? T("RuntimeSampling") : _state.Status;
        _state.LastResult = _lastOptimizationResult is not null
            ? FormatOptimizationResult(_lastOptimizationResult)
            : T(_lastResultFallbackKey);
        _state.SelfOverhead = T("RuntimeSelfSampling");
        _state.AutoStatus = _settings.AutoOptimization ? T("Enabled") : T("Disabled");
        _state.ReboundRate = _reboundTracker.HasResult
            ? string.Format(T("ReboundRateFormat"), _reboundTracker.RatePercent)
            : T("ReboundRatePending");
        UpdateSessionUptime();
        UpdateApplicationReboundSummary();
        if (_families.Count > 0) UpdateProcessRows();
        if (_currentMemory.TotalPhysicalBytes > 0) UpdatePreviewRows();
        if (_trayIcon is not null) ApplyTrayLanguage();
        RefreshProfileSelectors();
        RefreshCustomProfileCatalog();
        SynchronizeCandidateModeControls();
        SynchronizeCandidateDisplayLimitControls();
        UpdateBenefitLearningStatus();
        SynchronizeUpdateFrequencyPresentation();
        RefreshOverviewAttention();
    }

    private string T(string key) => Resources[key] as string ?? key;
    private string TF(string key, params object?[] values) => string.Format(T(key), values);

    private string FormatPlan(OptimizationPlan plan) => plan.Outcome switch
    {
        OptimizationPlanOutcome.LowMemoryPressure => T("PlanLowPressure"),
        OptimizationPlanOutcome.NoCandidates => T("PlanNoCandidates"),
        _ => TF("PlanCandidatesFormat", plan.Candidates.Count)
    };

    private string FormatOptimizationResult(OptimizationResultDisplay result) =>
        TF("OptimizationResultFormat", DisplayFormat.Bytes(result.WorkingSetReductionBytes), SignedBytes(result.NetAvailableBytes));

    private void UpdateReboundRate(MemorySnapshot memory)
    {
        if (!_reboundTracker.HasResult) return;
        SetReboundRate(_reboundTracker.Observe(memory, DateTimeOffset.Now));
    }

    private void SetReboundRate(double ratePercent) =>
        _state.ReboundRate = string.Format(T("ReboundRateFormat"), ratePercent);

    private void UpdateApplicationReboundSummary()
    {
        var details = _applicationReboundDetailTracker.Details;
        _state.HasReboundDetails = details.Count > 0 || _reboundRunHistory.Count > 0;
        if (details.Count == 0)
        {
            _state.ReboundSummary = _reboundRunHistory.Count > 0
                ? T("ReboundHistoryAvailable")
                : T("ReboundApplicationsUnavailable");
            if (ReboundHistoryPanel.Visibility == Visibility.Visible) RefreshReboundAnalysis();
            return;
        }

        var top = details[0];
        _state.ReboundSummary = top.RegainedBytes > 0
            ? TF("PrimaryReboundApplicationFormat", top.DisplayName, DisplayFormat.Bytes(top.RegainedBytes))
            : _applicationReboundDetailTracker.IsTracking(DateTimeOffset.Now)
                ? TF("ReboundApplicationsObservingFormat", (int)_applicationReboundDetailTracker.Elapsed(DateTimeOffset.Now).TotalSeconds)
                : T("NoApplicationReboundObserved");
        if (ReboundHistoryPanel.Visibility == Visibility.Visible) RefreshReboundAnalysis();
    }

    private void BeginApplicationReboundRun(DateTimeOffset startedAt, OptimizationRunKind kind)
    {
        SynchronizeReboundRunHistory(startedAt);
        if (_activeReboundRunSequence is { } activeSequence)
        {
            var activeIndex = _reboundRunHistory.FindIndex(run => run.Sequence == activeSequence);
            if (activeIndex >= 0)
            {
                var activeRun = _reboundRunHistory[activeIndex];
                if (activeRun.State == ReboundObservationState.Observing)
                {
                    _reboundRunHistory[activeIndex] = activeRun with
                    {
                        FinishedAt = startedAt,
                        State = ReboundObservationState.Replaced
                    };
                }
            }
        }

        _activeReboundRunSequence = null;
        _pendingReboundRunKind = kind;
        _applicationReboundDetailTracker.BeginRun(startedAt);
    }

    private void SynchronizeReboundRunHistory(DateTimeOffset now)
    {
        var details = _applicationReboundDetailTracker.Details;
        if (details.Count == 0) return;

        if (_activeReboundRunSequence is null)
        {
            var sequence = ++_nextReboundRunSequence;
            _activeReboundRunSequence = sequence;
            _reboundRunHistory.Insert(0, new ReboundHistoryRun(
                sequence,
                _pendingReboundRunKind,
                _applicationReboundDetailTracker.StartedAt ?? now,
                null,
                ReboundObservationState.Observing,
                Array.Empty<ReboundHistoryDetail>()));
        }

        var index = _reboundRunHistory.FindIndex(run => run.Sequence == _activeReboundRunSequence.Value);
        if (index < 0) return;
        var tracking = _applicationReboundDetailTracker.IsTracking(now);
        _reboundRunHistory[index] = _reboundRunHistory[index] with
        {
            FinishedAt = tracking
                ? null
                : _applicationReboundDetailTracker.CompletedAt ??
                  _applicationReboundDetailTracker.ExpectedCompletionAt ?? now,
            State = tracking ? ReboundObservationState.Observing : ReboundObservationState.Completed,
            Details = details.Select(detail => new ReboundHistoryDetail(
                detail.DisplayName,
                detail.ReleasedBytes,
                detail.RegainedBytes)).ToArray()
        };
    }

    private void ReboundDetails_OnClick(object sender, RoutedEventArgs e)
    {
        SynchronizeReboundRunHistory(DateTimeOffset.Now);
        if (_reboundRunHistory.Count == 0) return;
        _selectedReboundRunSequence = _reboundRunHistory[0].Sequence;
        NavigateToHistoryAnalysis("Rebound");
    }

    private string ReboundStateText(ReboundObservationState state) => state switch
    {
        ReboundObservationState.Completed => T("ObservationComplete"),
        ReboundObservationState.Replaced => T("ObservationReplaced"),
        _ => T("ObservationInProgress")
    };

    private string ReboundKindText(OptimizationRunKind kind) => T(kind switch
    {
        OptimizationRunKind.Manual => "ReboundRunManual",
        OptimizationRunKind.Automatic => "ReboundRunAutomatic",
        OptimizationRunKind.Scheduled => "ReboundRunScheduled",
        OptimizationRunKind.LongIdle => "ReboundRunLongIdle",
        OptimizationRunKind.ApplicationRule => "ReboundRunApplicationRule",
        _ => "ReboundRunAutomatic"
    });

    private void RefreshReboundAnalysis()
    {
        var now = DateTimeOffset.Now;
        SynchronizeReboundRunHistory(now);
        var visibleRuns = (_reboundHistoryLimit > 0
                ? _reboundRunHistory.Take(_reboundHistoryLimit)
                : _reboundRunHistory)
            .ToArray();
        if (visibleRuns.Length == 0)
        {
            _state.ReboundRuns.Clear();
            _state.ReboundDetails.Clear();
            ReboundAnalysisSummaryText.Text = T("ReboundHistoryEmpty");
            return;
        }

        if (_selectedReboundRunSequence is null ||
            visibleRuns.All(run => run.Sequence != _selectedReboundRunSequence.Value))
        {
            _selectedReboundRunSequence = visibleRuns[0].Sequence;
        }

        DateTime? previousDate = null;
        var runRows = visibleRuns.Select(run =>
        {
            var localStartedAt = run.StartedAt.LocalDateTime;
            var showDate = localStartedAt.Date != now.LocalDateTime.Date &&
                           previousDate != localStartedAt.Date;
            previousDate = localStartedAt.Date;
            var time = showDate
                ? localStartedAt.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)
                : localStartedAt.ToString("HH:mm", CultureInfo.CurrentCulture);
            return new ReboundHistoryRunRow(
                run.Sequence,
                TF("ReboundHistoryRunCompactFormat", run.Sequence, time, ReboundKindText(run.Kind)),
                TF("ReboundHistoryRunStateFormat", ReboundStateText(run.State), run.Details.Count));
        });
        SynchronizeCollection(_state.ReboundRuns, runRows);

        var selectedRow = _state.ReboundRuns.First(row => row.Sequence == _selectedReboundRunSequence.Value);
        if (ReboundRunsList.SelectedItem is not ReboundHistoryRunRow selected ||
            selected.Sequence != selectedRow.Sequence)
        {
            _syncingReboundRunSelection = true;
            ReboundRunsList.SelectedItem = selectedRow;
            _syncingReboundRunSelection = false;
        }
        RefreshSelectedReboundRun(now);
    }

    private void RefreshSelectedReboundRun(DateTimeOffset now)
    {
        var run = _reboundRunHistory.FirstOrDefault(candidate =>
            candidate.Sequence == _selectedReboundRunSequence);
        if (run is null) return;
        var elapsed = (int)Math.Clamp(
            (now - run.StartedAt).TotalSeconds,
            0,
            ApplicationReboundDetailTracker.TrackingDuration.TotalSeconds);
        ReboundAnalysisSummaryText.Text = run.State switch
        {
            ReboundObservationState.Observing => TF(
                "ReboundRunObservingSummaryFormat",
                run.Sequence,
                ReboundKindText(run.Kind),
                run.StartedAt,
                elapsed,
                (int)ApplicationReboundDetailTracker.TrackingDuration.TotalSeconds,
                run.StartedAt + ApplicationReboundDetailTracker.TrackingDuration),
            ReboundObservationState.Replaced => TF(
                "ReboundRunReplacedSummaryFormat",
                run.Sequence,
                ReboundKindText(run.Kind),
                run.StartedAt,
                run.FinishedAt ?? run.StartedAt),
            _ => TF(
                "ReboundRunCompleteSummaryFormat",
                run.Sequence,
                ReboundKindText(run.Kind),
                run.StartedAt,
                run.FinishedAt ?? run.StartedAt + ApplicationReboundDetailTracker.TrackingDuration)
        };
        SynchronizeCollection(_state.ReboundDetails, run.Details.Select(detail =>
            new ApplicationReboundDetailRow(
                detail.DisplayName,
                DisplayFormat.Bytes(detail.ReleasedBytes),
                DisplayFormat.Bytes(detail.RegainedBytes),
                $"{detail.RegainedBytes / (double)Math.Max(1, detail.ReleasedBytes) * 100d:0.0}%",
                ReboundStateText(run.State))));
    }

    private void ReboundRunsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingReboundRunSelection || ReboundRunsList.SelectedItem is not ReboundHistoryRunRow row) return;
        _selectedReboundRunSequence = row.Sequence;
        RefreshSelectedReboundRun(DateTimeOffset.Now);
    }

    private void ReboundHistoryLimit_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string value } || !int.TryParse(value, out var limit)) return;
        _reboundHistoryLimit = limit;
        ReboundLimit5Button.IsChecked = limit == 5;
        ReboundLimit10Button.IsChecked = limit == 10;
        ReboundLimit20Button.IsChecked = limit == 20;
        ReboundLimitAllButton.IsChecked = limit == 0;
        RefreshReboundAnalysis();
    }

    private void ReboundHistoryBack_OnClick(object sender, RoutedEventArgs e) =>
        SelectNavigation(OverviewPage, T("Overview"));

    private void ShowReboundDetailsDialog()
    {
        SynchronizeReboundRunHistory(DateTimeOffset.Now);
        if (_reboundRunHistory.Count == 0) return;

        var rows = new ObservableCollection<ApplicationReboundDetailRow>();
        var summary = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var historyButton = new Button
        {
            Width = 34,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(12, 0, 0, 0),
            ToolTip = T("ReboundHistory"),
            Style = (Style)FindResource("ButtonStyle"),
            Content = new System.Windows.Shapes.Path
            {
                Style = (Style)FindResource("ButtonIconPathStyle"),
                Width = 16,
                Height = 16,
                Data = (System.Windows.Media.Geometry)FindResource("IconHistory")
            }
        };
        AutomationProperties.SetName(historyButton, T("ReboundHistory"));
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(historyButton, 1);
        header.Children.Add(summary);
        header.Children.Add(historyButton);

        var historyLimitBox = new System.Windows.Controls.ComboBox
        {
            Width = 116,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Style = (Style)FindResource("ThemedComboBoxStyle")
        };
        foreach (var limit in new[] { 3, 5, 10 })
        {
            historyLimitBox.Items.Add(new ComboBoxItem
            {
                Content = TF("ReboundHistoryLimitFormat", limit),
                Tag = limit
            });
        }
        historyLimitBox.Items.Add(new ComboBoxItem
        {
            Content = T("ReboundHistoryAll"),
            Tag = 0
        });
        historyLimitBox.SelectedIndex = 1;
        var historyRunsPanel = new StackPanel();
        var historyPopupContent = new StackPanel();
        var historyPopupHeader = new Grid();
        historyPopupHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        historyPopupHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        historyPopupHeader.Children.Add(new TextBlock
        {
            Text = T("ReboundHistory"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(historyLimitBox, 1);
        historyPopupHeader.Children.Add(historyLimitBox);
        historyPopupContent.Children.Add(historyPopupHeader);
        historyPopupContent.Children.Add(new ScrollViewer
        {
            MaxHeight = 310,
            Margin = new Thickness(0, 10, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = historyRunsPanel
        });
        var historyPopup = new Popup
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = historyButton,
            HorizontalOffset = -286,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = MotionPolicy.Current.IsEnabled
                ? PopupAnimation.Slide
                : PopupAnimation.None,
            Child = new Border
            {
                Width = 320,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                Background = (MediaBrush)FindResource("SurfaceBrush"),
                BorderBrush = (MediaBrush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                Child = historyPopupContent
            }
        };
        var grid = new DataGrid
        {
            ItemsSource = rows,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            CanUserSortColumns = false,
            Focusable = false,
            MinHeight = 260,
            Style = (Style)FindResource(typeof(DataGrid)),
            ColumnHeaderStyle = (Style)FindResource(typeof(DataGridColumnHeader)),
            CellStyle = (Style)FindResource(typeof(DataGridCell)),
            RowStyle = (Style)FindResource("NonSelectableDataGridRowStyle")
        };
        AutomationProperties.SetName(grid, T("ReboundDetailsTitle"));
        grid.Columns.Add(new DataGridTextColumn { Header = T("Application"), Binding = new System.Windows.Data.Binding(nameof(ApplicationReboundDetailRow.Application)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = T("InitialTrim"), Binding = new System.Windows.Data.Binding(nameof(ApplicationReboundDetailRow.InitialTrim)), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = T("RegainedWorkingSet"), Binding = new System.Windows.Data.Binding(nameof(ApplicationReboundDetailRow.Regained)), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = T("ApplicationReboundRate"), Binding = new System.Windows.Data.Binding(nameof(ApplicationReboundDetailRow.ReboundRate)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = T("ObservationStatus"), Binding = new System.Windows.Data.Binding(nameof(ApplicationReboundDetailRow.Status)), Width = 110 });

        var disclaimer = new TextBlock
        {
            Text = T("ReboundDetailsDisclaimer"),
            Style = (Style)FindResource("CaptionStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var close = new Button
        {
            Content = T("ReboundDetailsClose"),
            MinWidth = 100,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            IsDefault = true,
            IsCancel = true,
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        var gridFrame = new Border
        {
            Background = (MediaBrush)FindResource("SurfaceBrush"),
            BorderBrush = (MediaBrush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(1),
            Child = grid
        };
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(gridFrame, 1);
        Grid.SetRow(disclaimer, 2);
        Grid.SetRow(close, 3);
        root.Children.Add(header);
        root.Children.Add(gridFrame);
        root.Children.Add(disclaimer);
        root.Children.Add(close);

        var dialog = new Window
        {
            Owner = this,
            Title = T("ReboundDetailsTitle"),
            Width = 760,
            Height = 500,
            MinWidth = 640,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush"),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Content = root,
            ShowInTaskbar = false
        };
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(dialog, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(dialog, TextHintingMode.Fixed);

        int? selectedSequence = _reboundRunHistory.FirstOrDefault()?.Sequence;

        string StateText(ReboundObservationState state) => state switch
        {
            ReboundObservationState.Completed => T("ObservationComplete"),
            ReboundObservationState.Replaced => T("ObservationReplaced"),
            _ => T("ObservationInProgress")
        };

        string KindText(OptimizationRunKind kind) => T(kind switch
        {
            OptimizationRunKind.Manual => "ReboundRunManual",
            OptimizationRunKind.Automatic => "ReboundRunAutomatic",
            OptimizationRunKind.Scheduled => "ReboundRunScheduled",
            OptimizationRunKind.LongIdle => "ReboundRunLongIdle",
            OptimizationRunKind.ApplicationRule => "ReboundRunApplicationRule",
            _ => "ReboundRunAutomatic"
        });

        void RefreshRows()
        {
            var now = DateTimeOffset.Now;
            SynchronizeReboundRunHistory(now);
            var run = _reboundRunHistory.FirstOrDefault(candidate => candidate.Sequence == selectedSequence) ??
                      _reboundRunHistory.First();
            selectedSequence = run.Sequence;
            var elapsed = (int)Math.Clamp(
                (now - run.StartedAt).TotalSeconds,
                0,
                ApplicationReboundDetailTracker.TrackingDuration.TotalSeconds);
            summary.Text = run.State switch
            {
                ReboundObservationState.Observing => TF(
                    "ReboundRunObservingSummaryFormat",
                    run.Sequence,
                    KindText(run.Kind),
                    run.StartedAt,
                    elapsed,
                    (int)ApplicationReboundDetailTracker.TrackingDuration.TotalSeconds,
                    run.StartedAt + ApplicationReboundDetailTracker.TrackingDuration),
                ReboundObservationState.Replaced => TF(
                    "ReboundRunReplacedSummaryFormat",
                    run.Sequence,
                    KindText(run.Kind),
                    run.StartedAt,
                    run.FinishedAt ?? run.StartedAt),
                _ => TF(
                    "ReboundRunCompleteSummaryFormat",
                    run.Sequence,
                    KindText(run.Kind),
                    run.StartedAt,
                    run.FinishedAt ?? run.StartedAt + ApplicationReboundDetailTracker.TrackingDuration)
            };
            SynchronizeCollection(rows, run.Details.Select(detail =>
                new ApplicationReboundDetailRow(
                    detail.DisplayName,
                    DisplayFormat.Bytes(detail.ReleasedBytes),
                    DisplayFormat.Bytes(detail.RegainedBytes),
                    $"{detail.RegainedBytes / (double)Math.Max(1, detail.ReleasedBytes) * 100d:0.0}%",
                    StateText(run.State))));
        }

        void RefreshHistoryMenu()
        {
            historyRunsPanel.Children.Clear();
            var limit = historyLimitBox.SelectedItem is ComboBoxItem { Tag: int value } ? value : 5;
            var visibleRuns = limit > 0
                ? _reboundRunHistory.Take(limit)
                : _reboundRunHistory;
            foreach (var run in visibleRuns)
            {
                var runButton = new Button
                {
                    Height = 36,
                    Padding = new Thickness(10, 0, 8, 0),
                    Margin = new Thickness(0, historyRunsPanel.Children.Count == 0 ? 0 : 4, 0, 0),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Style = (Style)FindResource("ToolbarButtonStyle"),
                    Content = new TextBlock
                    {
                        Text = TF(
                            "ReboundHistoryRunFormat",
                            run.Sequence,
                            KindText(run.Kind),
                            run.StartedAt,
                            StateText(run.State)),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                if (run.Sequence == selectedSequence)
                {
                    runButton.Background = (MediaBrush)FindResource("AccentSoftBrush");
                    runButton.Foreground = (MediaBrush)FindResource("AccentBrush");
                    runButton.BorderBrush = System.Windows.Media.Brushes.Transparent;
                    runButton.FontWeight = FontWeights.SemiBold;
                }
                var sequence = run.Sequence;
                runButton.Click += (_, _) =>
                {
                    selectedSequence = sequence;
                    historyPopup.IsOpen = false;
                    RefreshRows();
                };
                historyRunsPanel.Children.Add(runButton);
            }
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshRows();
        dialog.Closed += (_, _) =>
        {
            timer.Stop();
            historyPopup.IsOpen = false;
        };
        close.Click += (_, _) => dialog.Close();
        var suppressHistoryButtonClick = false;
        dialog.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (historyPopup.IsOpen &&
                e.OriginalSource is DependencyObject source &&
                IsDescendantOf(source, historyButton))
            {
                suppressHistoryButtonClick = true;
                historyPopup.IsOpen = false;
            }
        };
        historyButton.Click += (_, _) =>
        {
            if (suppressHistoryButtonClick)
            {
                suppressHistoryButtonClick = false;
                return;
            }
            RefreshHistoryMenu();
            historyPopup.IsOpen = !historyPopup.IsOpen;
        };
        historyLimitBox.SelectionChanged += (_, _) => RefreshHistoryMenu();
        ApplyDialogTheme(dialog);
        RefreshRows();
        timer.Start();
        _ = dialog.ShowDialog();
    }

    private static string? PageResourceKey(string pageName) => pageName switch
    {
        "OverviewPage" => "Overview",
        "ProcessesPage" => "Processes",
        "ProtectionPage" => "Protection",
        "ApplicationRulesPage" => "ApplicationRules",
        "HistoryPage" => "History",
        "CustomPage" => "Custom",
        "SettingsPage" => "Settings",
        _ => null
    };

    private void CompactMode_OnClick(object sender, RoutedEventArgs e)
    {
        var currentBounds = new WindowBounds(Left, Top, ActualWidth, ActualHeight);
        var workingArea = CurrentWorkingArea();
        var enterCompactMode = !_compactMode;
        var targetBounds = enterCompactMode
            ? WindowBoundsPolicy.CenterAndClamp(currentBounds, 540, 266, workingArea)
            : WindowBoundsPolicy.CenterAndClamp(currentBounds, 1240, 800, workingArea);
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 0;
        MinHeight = 0;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        Left = targetBounds.Left;
        Top = targetBounds.Top;
        Width = targetBounds.Width;
        Height = targetBounds.Height;
        _compactMode = enterCompactMode;
        ApplyFinalWindowMode(enterCompactMode);
    }

    private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowAnimationMessageHook);
        ApplyNativeWindowTheme(IsLightThemeActive());
        LogWindowAnimationState("source-initialized");
        _ = Dispatcher.BeginInvoke(
            () => LogWindowAnimationState("source-initialized-settled"),
            DispatcherPriority.Loaded);
    }

    private void ApplyNativeWindowTheme(bool light)
    {
        WindowThemeService.ApplyDarkTitleBar(this, !light);
        ApplyCompositionBackground();
    }

    private void ApplyCompositionBackground()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source ||
            source.CompositionTarget is null) return;
        source.CompositionTarget.BackgroundColor =
            ((SolidColorBrush)FindResource("WindowBrush")).Color;
    }

    private void ApplyFinalWindowMode(bool compact)
    {
        FullShell.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactShell.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        if (compact)
        {
            ResizeMode = ResizeMode.CanMinimize;
            MaximizeRestoreButton.Visibility = Visibility.Collapsed;
            MinWidth = 540;
            MaxWidth = 540;
            Width = 540;
            MinHeight = 266;
            MaxHeight = 266;
            Height = 266;
            return;
        }

        ResizeMode = ResizeMode.CanResize;
        MaximizeRestoreButton.Visibility = Visibility.Visible;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        MinWidth = 1040;
        MinHeight = 680;
        Width = 1240;
        Height = 800;
    }

    private WindowBounds CurrentWorkingArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new WindowBounds(area.Left, area.Top, area.Width, area.Height);
        }

        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new System.Windows.Point(area.Left, area.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(area.Right, area.Bottom));
        return new WindowBounds(
            topLeft.X,
            topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
    }

    private void StartupCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        var requested = StartupCheckBox.IsChecked == true;
        var previous = _settings.StartWithWindows;
        var committedSettings = _settings;
        var result = StartupPreferenceTransaction.TryCommit(
            previous,
            requested,
            StartupRegistration.SetEnabled,
            StartupRegistration.IsEnabled,
            value =>
            {
                var settingsResult = LocalSettingsTransaction.TryCommit(
                    _settings,
                    settings => settings.StartWithWindows = value,
                    _settingsStore.Save);
                if (!settingsResult.Succeeded) throw settingsResult.Error!;
                committedSettings = settingsResult.Settings;
            });

        if (result.Succeeded)
        {
            _settings = committedSettings;
        }
        else if (result.RegistrationError is not null)
        {
            _state.Status = TF("StartupFailureFormat", result.RegistrationError.Message);
            _diagnosticLog.Warning("Unable to update startup registration.", result.RegistrationError);
        }
        else if (result.PersistenceError is not null)
        {
            _state.Status = TF("SettingsSaveFailureFormat", result.PersistenceError.Message);
            _diagnosticLog.Error("Unable to save settings.", result.PersistenceError);
        }
        if (result.CompensationError is not null)
        {
            _state.Status = TF("StartupFailureFormat", result.CompensationError.Message);
            _diagnosticLog.Error("Unable to restore the previous startup registration.", result.CompensationError);
        }
        _syncingControls = true;
        StartupCheckBox.IsThreeState = result.CompensationError is not null;
        StartupCheckBox.IsChecked = result.CompensationError is null
            ? _settings.StartWithWindows
            : null;
        _syncingControls = false;
    }

    private void CloseBehaviorBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || CloseBehaviorBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag || !Enum.TryParse<CloseButtonBehavior>(tag, out var behavior)) return;

        if (!TryUpdateSettings(settings => settings.CloseButtonBehavior = behavior))
        {
            _syncingControls = true;
            CloseBehaviorBox.SelectedItem = CloseBehaviorBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag as string,
                    _settings.CloseButtonBehavior.ToString(),
                    StringComparison.Ordinal));
            _syncingControls = false;
        }
    }

    private void EnhancedSafetyCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        var requested = EnhancedSafetyCheckBox.IsChecked == true;
        if (!TryUpdateSettings(settings => settings.EnhancedSafety = requested))
        {
            _syncingControls = true;
            EnhancedSafetyCheckBox.IsChecked = _settings.EnhancedSafety;
            _syncingControls = false;
            return;
        }
        UpdatePreviewRows();
    }

    private void TrayMemoryUsageIconCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        var requested = TrayMemoryUsageIconCheckBox.IsChecked == true;
        if (!TryUpdateSettings(settings => settings.ShowMemoryUsageInTrayIcon = requested))
        {
            _syncingControls = true;
            TrayMemoryUsageIconCheckBox.IsChecked = _settings.ShowMemoryUsageInTrayIcon;
            _syncingControls = false;
            return;
        }

        UpdateTrayMemoryIcon();
    }

    private void DiagnosticDataCollectionCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        var requested = DiagnosticDataCollectionCheckBox.IsChecked == true;
        if (!TryUpdateSettings(settings => settings.DiagnosticDataCollectionEnabled = requested))
        {
            _syncingControls = true;
            DiagnosticDataCollectionCheckBox.IsChecked = _settings.DiagnosticDataCollectionEnabled;
            _syncingControls = false;
            return;
        }

        _lastPreviewCandidateFamilyKeys = null;
        _lastCandidateCalibrations.Clear();
        _lastMonitoringCalibrationAt = null;
        _lastLargeMemoryOpportunityAt = null;
        Interlocked.Increment(ref _calibrationWriteGeneration);
        if (requested) StartResponsivenessMonitoring();
        else
        {
            StopResponsivenessMonitoring();
            _activityThresholdShadowTracker.Reset();
            _activityThresholdShadowStates = Array.Empty<ActivityThresholdShadowState>();
        }
    }

    private void ClearDiagnosticData_OnClick(object sender, RoutedEventArgs e)
    {
        if (ShowThemedMessage(
                T("ClearDiagnosticData"),
                T("ClearDiagnosticDataConfirm"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                affirmativeText: T("DialogClear"),
                negativeText: T("Cancel"),
                destructiveAffirmative: true) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _diagnosticClearInProgress, 1);
            StopResponsivenessMonitoring();
            Interlocked.Increment(ref _calibrationWriteGeneration);
            FlushCalibrationWrites();
            _calibrationMetricsStore.Delete();
            _diagnosticLog.Delete();
            _processIoCalibrationTracker.Reset();
            _processCpuCalibrationTracker.Reset();
            _activityThresholdShadowTracker.Reset();
            _activityThresholdShadowStates = Array.Empty<ActivityThresholdShadowState>();
            _lastPreviewCandidateFamilyKeys = null;
            _lastCandidateCalibrations.Clear();
            _lastMonitoringCalibrationAt = null;
            _lastLargeMemoryOpportunityAt = null;
            _state.Status = T("DiagnosticDataCleared");
        }
        catch (Exception exception)
        {
            _state.Status = TF("ClearDiagnosticDataFailureFormat", exception.Message);
            _diagnosticLog.Warning("Unable to clear diagnostic data.", exception);
        }
        finally
        {
            Interlocked.Exchange(ref _diagnosticClearInProgress, 0);
            if (_settings.DiagnosticDataCollectionEnabled) StartResponsivenessMonitoring();
        }
    }

    private void RuntimeProgressPersistenceCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        var requested = RuntimeProgressPersistenceCheckBox.IsChecked == true;
        if (!TryUpdateSettings(settings => settings.RuntimeProgressPersistenceEnabled = requested))
        {
            _syncingControls = true;
            RuntimeProgressPersistenceCheckBox.IsChecked = _settings.RuntimeProgressPersistenceEnabled;
            _syncingControls = false;
            return;
        }

        _pendingRuntimeActivities.Clear();
        _pendingRuntimeTrimHistory.Clear();
        if (requested)
        {
            _lastRuntimeProgressSaveAt = null;
            SaveRuntimeProgressIfDue(force: true);
            return;
        }

        _restoredSessionUptime = TimeSpan.Zero;
        UpdateSessionUptime();
        try { _runtimeProgressStore.Delete(); }
        catch (Exception exception)
        {
            _diagnosticLog.Warning("Unable to remove runtime progress.", exception);
        }
    }

    private void IgnoreMemoryPressureThresholdCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        var requested = IgnoreMemoryPressureThresholdCheckBox.IsChecked == true;
        if (!TryUpdateSettings(settings => settings.IgnoreMemoryPressureThreshold = requested))
        {
            RefreshProfileSelectors();
            return;
        }
        UpdateScheduledOptimizationAvailability(resetAnchor: true);
        UpdatePreviewRows();
    }

    private void ScheduledOptimizationCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        if (IsScheduledOptimizationUnavailable())
        {
            SynchronizeScheduledOptimizationControls();
            return;
        }
        var requested = ScheduledOptimizationCheckBox.IsChecked == true;
        if (!TryUpdateSettings(settings => settings.ScheduledOptimizationEnabled = requested))
        {
            SynchronizeScheduledOptimizationControls();
            return;
        }
        ScheduledOptimizationIntervalBox.IsEnabled = _settings.ScheduledOptimizationEnabled;
        ScheduledCustomIntervalPanel.IsEnabled = _settings.ScheduledOptimizationEnabled;
        _scheduledOptimizationAnchor = DateTimeOffset.UtcNow;
    }

    private void LongIdleOptimizationCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        var requested = LongIdleOptimizationCheckBox.IsChecked == true;
        if (!TryUpdateSettings(settings => settings.LongIdleOptimizationEnabled = requested))
        {
            SynchronizeLongIdleOptimizationControls();
            return;
        }
        LongIdleMinutesPanel.Visibility = requested ? Visibility.Visible : Visibility.Collapsed;
        _lastSuccessfulOptimizationAt = DateTimeOffset.UtcNow;
        _lastLongIdleEvaluationAt = null;
        UpdateLongIdleOptimizationStatus(DateTimeOffset.UtcNow);
    }

    private void LongIdleMinutesSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (LongIdleMinutesText is null) return;
        var minutes = LongIdleOptimizationPolicy.NormalizeMinutes((int)Math.Round(e.NewValue));
        LongIdleMinutesText.Text = TF("LongIdleMinutesFormat", minutes);
        if (_syncingControls) return;
        if (!TryUpdateSettings(settings => settings.LongIdleOptimizationMinutes = minutes))
        {
            SynchronizeLongIdleOptimizationControls();
            return;
        }
        _lastSuccessfulOptimizationAt = DateTimeOffset.UtcNow;
        _lastLongIdleEvaluationAt = null;
        UpdateLongIdleOptimizationStatus(DateTimeOffset.UtcNow);
    }

    private void SynchronizeLongIdleOptimizationControls()
    {
        var wasSyncing = _syncingControls;
        _syncingControls = true;
        try
        {
            var minutes = LongIdleOptimizationPolicy.NormalizeMinutes(_settings.LongIdleOptimizationMinutes);
            LongIdleOptimizationCheckBox.IsChecked = _settings.LongIdleOptimizationEnabled;
            LongIdleMinutesPanel.Visibility = _settings.LongIdleOptimizationEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;
            LongIdleMinutesSlider.Value = minutes;
            LongIdleMinutesText.Text = TF("LongIdleMinutesFormat", minutes);
            UpdateLongIdleOptimizationStatus(DateTimeOffset.UtcNow);
        }
        finally
        {
            _syncingControls = wasSyncing;
        }
    }

    private void UpdateLongIdleOptimizationStatus(DateTimeOffset now)
    {
        if (LongIdleStatusText is null) return;
        if (!_settings.LongIdleOptimizationEnabled)
        {
            LongIdleStatusText.Text = string.Empty;
            return;
        }

        var interval = TimeSpan.FromMinutes(
            LongIdleOptimizationPolicy.NormalizeMinutes(_settings.LongIdleOptimizationMinutes));
        var remaining = interval - (now - _lastSuccessfulOptimizationAt);
        if (remaining > TimeSpan.Zero)
        {
            LongIdleStatusText.Text = TF(
                "LongIdleStatusWaitingFormat",
                Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
            return;
        }

        if (_currentMemory.TotalPhysicalBytes == 0)
        {
            LongIdleStatusText.Text = T("LongIdleStatusMonitoring");
            return;
        }

        var automaticSettings = _settings.ResolveOptimizationSettings(manual: false);
        if (OptimizationPlanner.HasMemoryPressure(_currentMemory, automaticSettings))
        {
            LongIdleStatusText.Text = T("LongIdleStatusMemoryPressure");
            return;
        }

        if (_automaticOptimizationSafetyAnchor is { } safetyAnchor)
        {
            var cooldownRemaining = automaticSettings.AutoCooldown - (DateTimeOffset.Now - safetyAnchor);
            if (cooldownRemaining > TimeSpan.Zero)
            {
                LongIdleStatusText.Text = TF(
                    "LongIdleStatusCooldownFormat",
                    Math.Max(1, (int)Math.Ceiling(cooldownRemaining.TotalSeconds)));
                return;
            }
        }

        LongIdleStatusText.Text = T("LongIdleStatusEligible");
    }

    private void ScheduledOptimizationIntervalBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingControls || ScheduledOptimizationIntervalBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag as string, out var minutes))
        {
            ScheduledCustomIntervalTextBox.Text = _settings.ScheduledOptimizationIntervalMinutes.ToString(CultureInfo.InvariantCulture);
            UpdateScheduledCustomIntervalPresentation();
            return;
        }

        var normalized = ScheduledOptimizationPolicy.NormalizeInterval(minutes);
        if (!TryUpdateSettings(settings => settings.ScheduledOptimizationIntervalMinutes = normalized))
        {
            SynchronizeScheduledOptimizationControls();
            return;
        }
        _scheduledOptimizationAnchor = DateTimeOffset.UtcNow;
        UpdateScheduledCustomIntervalPresentation();
    }

    private void ScheduledCustomIntervalTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        ApplyScheduledCustomInterval();
        e.Handled = true;
    }

    private void ScheduledCustomIntervalApply_OnClick(object sender, RoutedEventArgs e) =>
        ApplyScheduledCustomInterval();

    private void ApplyScheduledCustomInterval()
    {
        if (!int.TryParse(ScheduledCustomIntervalTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            minutes < ScheduledOptimizationPolicy.MinimumIntervalMinutes ||
            minutes > ScheduledOptimizationPolicy.MaximumIntervalMinutes)
        {
            ScheduledCustomIntervalValidationText.Visibility = Visibility.Visible;
            return;
        }

        if (!TryUpdateSettings(settings => settings.ScheduledOptimizationIntervalMinutes = minutes))
        {
            SynchronizeScheduledOptimizationControls();
            return;
        }
        _scheduledOptimizationAnchor = DateTimeOffset.UtcNow;
        ScheduledCustomIntervalValidationText.Visibility = Visibility.Collapsed;
    }

    private void UpdateScheduledCustomIntervalPresentation()
    {
        if (ScheduledCustomIntervalPanel is null || ScheduledOptimizationIntervalBox is null) return;
        var custom = ScheduledOptimizationIntervalBox.SelectedItem is ComboBoxItem item &&
                     string.Equals(item.Tag as string, "custom", StringComparison.Ordinal);
        ScheduledCustomIntervalPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        ScheduledCustomIntervalPanel.IsEnabled = _settings.ScheduledOptimizationEnabled;
        if (!custom) ScheduledCustomIntervalValidationText.Visibility = Visibility.Collapsed;
    }

    private void SynchronizeScheduledOptimizationControls()
    {
        var wasSyncing = _syncingControls;
        _syncingControls = true;
        try
        {
            ScheduledOptimizationCheckBox.IsChecked = _settings.ScheduledOptimizationEnabled;
            ScheduledOptimizationIntervalBox.IsEnabled = _settings.ScheduledOptimizationEnabled;
            ScheduledOptimizationIntervalBox.SelectedItem = ScheduledOptimizationIntervalBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => int.TryParse(item.Tag as string, out var minutes) &&
                                        minutes == _settings.ScheduledOptimizationIntervalMinutes)
                ?? ScheduledOptimizationIntervalBox.Items
                    .OfType<ComboBoxItem>()
                    .First(item => string.Equals(item.Tag as string, "custom", StringComparison.Ordinal));
            ScheduledCustomIntervalTextBox.Text =
                _settings.ScheduledOptimizationIntervalMinutes.ToString(CultureInfo.InvariantCulture);
            UpdateScheduledCustomIntervalPresentation();
            UpdateScheduledOptimizationAvailability();
        }
        finally
        {
            _syncingControls = wasSyncing;
        }
    }

    private bool IsScheduledOptimizationUnavailable()
    {
        var settings = _settings.ResolveOptimizationSettings(manual: false);
        return ScheduledOptimizationPolicy.IsUnavailable(
            _settings.AutoOptimization,
            settings.IgnoreMemoryPressureThreshold);
    }

    private void UpdateScheduledOptimizationAvailability(bool resetAnchor = false)
    {
        if (ScheduleMenuButton is null) return;
        var unavailable = IsScheduledOptimizationUnavailable();
        ScheduleMenuButton.IsEnabled = !unavailable;
        ScheduledOptimizationCheckBox.IsEnabled = !unavailable;
        ScheduledOptimizationIntervalBox.IsEnabled =
            !unavailable && _settings.ScheduledOptimizationEnabled;
        ScheduledCustomIntervalPanel.IsEnabled =
            !unavailable && _settings.ScheduledOptimizationEnabled;
        ScheduleMenuButton.ToolTip = T(unavailable
            ? "ScheduledOptimizationUnavailableHelp"
            : "ScheduledOptimization");
        if (unavailable)
        {
            SchedulePopup.IsOpen = false;
        }
        if (resetAnchor) _scheduledOptimizationAnchor = DateTimeOffset.UtcNow;
    }

    private void IntelligentCandidateSelectionCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        SetBenefitLearning(IntelligentCandidateSelectionCheckBox.IsChecked == true);
    }

    private void OverviewBenefitLearningCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        SetBenefitLearning(OverviewBenefitLearningCheckBox.IsChecked == true);
    }

    private void SetBenefitLearning(bool enabled)
    {
        if (!TryUpdateSettings(settings => settings.IntelligentCandidateSelection = enabled))
        {
            enabled = _settings.IntelligentCandidateSelection;
            var failedSyncing = _syncingControls;
            _syncingControls = true;
            IntelligentCandidateSelectionCheckBox.IsChecked = enabled;
            OverviewBenefitLearningCheckBox.IsChecked = enabled;
            _syncingControls = failedSyncing;
            return;
        }
        var wasSyncing = _syncingControls;
        _syncingControls = true;
        IntelligentCandidateSelectionCheckBox.IsChecked = enabled;
        OverviewBenefitLearningCheckBox.IsChecked = enabled;
        _syncingControls = wasSyncing;
        if (!enabled) _displayedProtectionSuggestions = Array.Empty<ProtectionSuggestion>();
        UpdateBenefitLearningStatus();
        SynchronizeStableStateSuppressionControls();
        UpdatePreviewRows();
    }

    private void ClearLegacyBenefitLearning_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        var legacyRecordCount = _applicationBackoffTracker.LearningRecords.Count(record =>
            record.ValidSampleCount <= 0);
        if (legacyRecordCount == 0) return;
        if (ShowThemedMessage(
                T("ClearLegacyBenefitLearning"),
                TF("ClearLegacyBenefitLearningConfirmFormat", legacyRecordCount),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                affirmativeText: T("DialogClear"),
                negativeText: T("Cancel"),
                destructiveAffirmative: true) != MessageBoxResult.Yes)
        {
            return;
        }

        var removed = _applicationBackoffTracker.RemoveLegacyOnlyLearning();
        if (removed == 0) return;
        SaveBenefitLearning();
        UpdateBenefitLearningStatus();
        _state.Status = TF("LegacyBenefitLearningClearedFormat", removed);
        UpdatePreviewRows();
    }

    private void ClearBenefitLearning_OnClick(object sender, RoutedEventArgs e)
    {
        var hasAnchors = _settings.StableAnchorSettings.Any(anchor => anchor.Mode == StableAnchorMode.Fixed);
        var confirmation = ShowThemedMessage(
                T("ClearBenefitLearning"),
                T(hasAnchors ? "ClearBenefitLearningWithAnchorsConfirm" : "ClearBenefitLearningConfirm"),
                hasAnchors ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                affirmativeText: T(hasAnchors ? "ClearLearningAndAnchors" : "DialogClear"),
                negativeText: T(hasAnchors ? "KeepAnchorsAndClearLearning" : "Cancel"),
                cancelText: T("Cancel"),
                destructiveAffirmative: true);
        if (confirmation == MessageBoxResult.Cancel || !hasAnchors && confirmation != MessageBoxResult.Yes)
        {
            return;
        }
        if (hasAnchors && !TryUpdateSettings(settings =>
            {
                if (confirmation == MessageBoxResult.Yes)
                {
                    settings.StableAnchorSettings.Clear();
                    return;
                }
                settings.StableAnchorSettings = settings.StableAnchorSettings.ToList();
            })) return;

        _applicationBackoffTracker.ClearLearning();
        _dismissedSuggestionIds.Clear();
        SaveBenefitLearning();
        UpdateBenefitLearningStatus();
        _state.Status = T("BenefitLearningCleared");
        UpdatePreviewRows();
    }

    private void ClearApplicationBenefitLearning_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy || sender is not Button { Tag: BenefitLearningRow row }) return;
        var records = _applicationBackoffTracker.LearningRecords
            .Where(record => string.Equals(
                record.FamilyKey,
                row.FamilyKey,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (records.Length == 0) return;
        var familyAnchors = _settings.StableAnchorSettings
            .Where(anchor => anchor.Mode == StableAnchorMode.Fixed && string.Equals(
                anchor.FamilyKey,
                row.FamilyKey,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var confirmation = ShowThemedMessage(
                T("ClearApplicationBenefitLearning"),
                TF(
                    familyAnchors.Length > 0
                        ? "ClearApplicationBenefitLearningWithAnchorsConfirmFormat"
                        : "ClearApplicationBenefitLearningConfirmFormat",
                    row.Application,
                    records.Length),
                familyAnchors.Length > 0 ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                affirmativeText: T(familyAnchors.Length > 0 ? "ClearLearningAndAnchors" : "DialogClear"),
                negativeText: T(familyAnchors.Length > 0 ? "KeepAnchorsAndClearLearning" : "Cancel"),
                cancelText: T("Cancel"),
                destructiveAffirmative: true);
        if (confirmation == MessageBoxResult.Cancel || familyAnchors.Length == 0 && confirmation != MessageBoxResult.Yes)
        {
            return;
        }
        if (familyAnchors.Length > 0 && !TryUpdateSettings(settings =>
            {
                if (confirmation == MessageBoxResult.Yes)
                {
                    settings.StableAnchorSettings.RemoveAll(anchor =>
                        string.Equals(anchor.FamilyKey, row.FamilyKey, StringComparison.OrdinalIgnoreCase));
                    return;
                }
                settings.StableAnchorSettings = settings.StableAnchorSettings.ToList();
            })) return;

        var suggestionIds = records
            .Where(record => !string.IsNullOrWhiteSpace(record.ComponentKey))
            .Select(ProtectionSuggestionPolicy.SuggestionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = _applicationBackoffTracker.RemoveLearningForFamily(row.FamilyKey);
        if (removed == 0) return;
        _dismissedSuggestionIds.RemoveWhere(suggestionIds.Contains);
        _displayedProtectionSuggestions = _displayedProtectionSuggestions
            .Where(suggestion => !string.Equals(
                suggestion.FamilyKey,
                row.FamilyKey,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        SaveBenefitLearning();
        UpdateBenefitLearningStatus();
        _state.Status = TF("ApplicationBenefitLearningClearedFormat", row.Application, removed);
        UpdatePreviewRows();
    }

    private void UpdateBenefitLearningStatus()
    {
        var records = _applicationBackoffTracker.LearningRecords;
        var learnedFamilyCount = records
            .Where(record => record.ValidSampleCount > 0)
            .Select(record => record.FamilyKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var legacyRecordCount = records.Count(record => record.ValidSampleCount <= 0);
        ClearLegacyBenefitLearningButton.Visibility = legacyRecordCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearLegacyBenefitLearningButton.IsEnabled = legacyRecordCount > 0 && !_state.IsBusy;
        if (!_settings.IntelligentCandidateSelection)
        {
            _state.BenefitLearningStatus = records.Count == 0
                ? T("BenefitLearningOffEmpty")
                : TF("BenefitLearningOffSavedFormat", records.Count);
            RefreshProtectionSuggestions();
            RefreshBenefitLearningAnalysis();
            return;
        }

        _state.BenefitLearningStatus = records.Count == 0
            ? T("BenefitLearningWaiting")
            : TF(
                "BenefitLearningProgressWithLegacyFormat",
                learnedFamilyCount,
                records.Sum(record => record.ValidSampleCount),
                legacyRecordCount);
        RefreshProtectionSuggestions();
        RefreshBenefitLearningAnalysis();
    }

    private void RefreshProtectionSuggestions()
    {
        _currentProtectionSuggestions = CreateCurrentProtectionSuggestions();
        var familyCount = _currentProtectionSuggestions
            .Select(suggestion => suggestion.FamilyKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        RefreshOverviewAttention(familyCount);
        ReviewProtectionSuggestionsButton.IsEnabled =
            !_state.IsBusy && (_displayedProtectionSuggestions.Count > 0 || familyCount > 0);
    }

    private void RefreshOverviewAttention(int? protectionFamilyCount = null)
    {
        var familyCount = protectionFamilyCount ?? _currentProtectionSuggestions
            .Select(suggestion => suggestion.FamilyKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (_availableUpdate is { } update && !string.Equals(
                _settings.SuppressedUpdateVersion,
                update.Version.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            ProtectionSuggestionButtonText.Text = TF("UpdateAvailableVersionFormat", update.Version);
            ProtectionSuggestionButton.IsEnabled = !_state.IsBusy;
            if (!string.Equals(_updateShimmerDismissedVersion, update.Version.ToString(), StringComparison.OrdinalIgnoreCase))
                StartProtectionSuggestionShimmer();
            else
                StopProtectionSuggestionShimmer();
            return;
        }

        ProtectionSuggestionButtonText.Text = familyCount == 0
            ? T("ProtectionSuggestionsNone")
            : TF("ProtectionSuggestionsAvailableFormat", familyCount);
        ProtectionSuggestionButton.IsEnabled = familyCount > 0 && !_state.IsBusy;
        if (familyCount > 0) StartProtectionSuggestionShimmer();
        else StopProtectionSuggestionShimmer();
    }

    private IReadOnlyList<ProtectionSuggestion> CreateCurrentProtectionSuggestions()
    {
        if (!_settings.IntelligentCandidateSelection || _families.Count == 0)
            return Array.Empty<ProtectionSuggestion>();

        var candidates = RunningProtectionCandidateCatalog.Create(
            _families,
            ApplicationProtectionSettings.Resolve(_settings));
        var candidateByFamily = candidates
            .GroupBy(candidate => candidate.FamilyKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return ProtectionSuggestionPolicy.Create(
                _applicationBackoffTracker.LearningRecords,
                _dismissedSuggestionIds)
            .Where(suggestion =>
                !string.IsNullOrWhiteSpace(suggestion.ExecutablePath) &&
                candidateByFamily.TryGetValue(suggestion.FamilyKey, out var candidate) &&
                candidate.ProtectionState != ApplicationProtectionState.EntireFamily &&
                candidate.Executables.Any(executable =>
                    !executable.IsProtected &&
                    string.Equals(
                        NormalizeExecutablePath(executable.ExecutablePath),
                        NormalizeExecutablePath(suggestion.ExecutablePath!),
                        StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private void StartProtectionSuggestionShimmer()
    {
        if (_protectionSuggestionShimmerActive) return;
        if (_protectionSuggestionShimmerStoryboard is null)
        {
            var animation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = -1.4,
                To = 1.4,
                Duration = TimeSpan.FromSeconds(2.4),
                BeginTime = TimeSpan.FromSeconds(0.35),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            System.Windows.Media.Animation.Storyboard.SetTarget(
                animation,
                ProtectionSuggestionShimmerTransform);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(
                animation,
                new PropertyPath(System.Windows.Media.TranslateTransform.XProperty));
            _protectionSuggestionShimmerStoryboard = new System.Windows.Media.Animation.Storyboard();
            _protectionSuggestionShimmerStoryboard.Children.Add(animation);
        }

        ProtectionSuggestionShimmer.Opacity = 1;
        _protectionSuggestionShimmerStoryboard.Begin(this, true);
        _protectionSuggestionShimmerActive = true;
    }

    private void StopProtectionSuggestionShimmer()
    {
        _protectionSuggestionShimmerStoryboard?.Remove(this);
        ProtectionSuggestionShimmerTransform.X = -1.4;
        ProtectionSuggestionShimmer.Opacity = 0;
        _protectionSuggestionShimmerActive = false;
    }

    private async void ProtectionSuggestions_OnClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is { } update)
        {
            if (!_state.IsBusy) await HandleAvailableUpdateAsync(update);
            return;
        }
        if (_state.IsBusy || _currentProtectionSuggestions.Count == 0) return;
        NavigateToHistoryAnalysis("Learning");
    }

    private void MarkProtectionSuggestionsViewed()
    {
        if (_currentProtectionSuggestions.Count == 0) return;
        _displayedProtectionSuggestions = _currentProtectionSuggestions.ToArray();
        foreach (var suggestion in _displayedProtectionSuggestions)
            _dismissedSuggestionIds.Add(suggestion.SuggestionId);
        SaveBenefitLearning();
        _currentProtectionSuggestions = Array.Empty<ProtectionSuggestion>();
        RefreshOverviewAttention(0);
        ReviewProtectionSuggestionsButton.IsEnabled = !_state.IsBusy;
        StopProtectionSuggestionShimmer();
    }

    private void ReviewProtectionSuggestions_OnClick(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy) return;
        var suggestions = (_displayedProtectionSuggestions.Count > 0
                ? _displayedProtectionSuggestions
                : _currentProtectionSuggestions)
            .ToArray();
        if (suggestions.Length == 0) return;
        ShowProtectionSuggestionsDialog(suggestions);
        _displayedProtectionSuggestions = Array.Empty<ProtectionSuggestion>();
        ReviewProtectionSuggestionsButton.IsEnabled = false;
        RefreshBenefitLearningAnalysis();
    }

    private void StableAnchorSummaryScroller_OnMouseEnter(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;
        scrollViewer.UpdateLayout();
        if (scrollViewer.ScrollableWidth <= 0.5d) return;

        if (scrollViewer.Tag is StableAnchorSummaryAutoScrollState existing) existing.Dispose();
        var state = new StableAnchorSummaryAutoScrollState(scrollViewer);
        scrollViewer.Tag = state;
        state.Start();
    }

    private void StableAnchorSummaryScroller_OnMouseLeave(
        object sender,
        System.Windows.Input.MouseEventArgs e) =>
        StopStableAnchorSummaryAutoScroll(sender as ScrollViewer);

    private void StableAnchorSummaryScroller_OnUnloaded(object sender, RoutedEventArgs e) =>
        StopStableAnchorSummaryAutoScroll(sender as ScrollViewer);

    private static void StopStableAnchorSummaryAutoScroll(ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null) return;
        if (scrollViewer.Tag is StableAnchorSummaryAutoScrollState state) state.Dispose();
        scrollViewer.Tag = null;
        scrollViewer.ScrollToLeftEnd();
    }

    private sealed class StableAnchorSummaryAutoScrollState : IDisposable
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(20);
        private static readonly TimeSpan InitialPause = TimeSpan.FromMilliseconds(450);
        private static readonly TimeSpan EdgePause = TimeSpan.FromMilliseconds(700);
        private readonly ScrollViewer _scrollViewer;
        private readonly DispatcherTimer _timer = new() { Interval = TickInterval };
        private DateTimeOffset _resumeAt;
        private double _direction = 1d;

        public StableAnchorSummaryAutoScrollState(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            _direction = 1d;
            _resumeAt = DateTimeOffset.UtcNow + InitialPause;
            _timer.Start();
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var maximum = _scrollViewer.ScrollableWidth;
            if (maximum <= 0.5d)
            {
                _timer.Stop();
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now < _resumeAt) return;

            var next = _scrollViewer.HorizontalOffset + _direction;
            if (next >= maximum)
            {
                _scrollViewer.ScrollToRightEnd();
                _direction = -1d;
                _resumeAt = now + EdgePause;
            }
            else if (next <= 0d)
            {
                _scrollViewer.ScrollToLeftEnd();
                _direction = 1d;
                _resumeAt = now + EdgePause;
            }
            else
            {
                _scrollViewer.ScrollToHorizontalOffset(next);
            }
        }
    }

    private void StableAnchorSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (ConsumeSuppressedPopupTriggerClick(sender)) return;
        if (_state.IsBusy ||
            sender is not Button { Tag: BenefitLearningRow row } button ||
            !row.CanConfigureAnchor ||
            string.IsNullOrWhiteSpace(row.ScopeKey)) return;
        _editingStableAnchorRow = row;
        _loadingStableAnchorEditor = true;
        _stableAnchorValueChanged = false;
        StableAnchorApplicationName.Text = row.Application;
        StableAnchorScopeDescription.Text = row.StableScopeDescription;
        StableAnchorSlider.Minimum = row.AnchorMinimumMiB;
        StableAnchorSlider.Maximum = Math.Max(row.AnchorMinimumMiB, row.AnchorMaximumMiB);
        StableAnchorSlider.Value = Math.Clamp(
            row.IsFixedAnchor ? row.FixedAnchorMiB : row.AdaptiveAnchorMiB,
            StableAnchorSlider.Minimum,
            StableAnchorSlider.Maximum);
        StableAnchorAdaptiveButton.IsChecked = !row.IsFixedAnchor;
        StableAnchorFixedButton.IsChecked = row.IsFixedAnchor;
        StableAnchorSlider.IsEnabled = row.IsFixedAnchor;
        StableAnchorResetButton.Visibility = row.IsFixedAnchor ? Visibility.Collapsed : Visibility.Visible;
        StableAnchorMinimumLabel.Text = TF("StableAnchorValueFormat", StableAnchorSlider.Minimum);
        StableAnchorMaximumLabel.Text = TF("StableAnchorValueFormat", StableAnchorSlider.Maximum);
        StableAnchorPopup.PlacementTarget = button;
        _loadingStableAnchorEditor = false;
        StableAnchorPopup.IsOpen = true;
        Dispatcher.BeginInvoke(UpdateStableAnchorValueLabel, DispatcherPriority.Loaded);
    }

    private void StableAnchorMode_OnClick(object sender, RoutedEventArgs e)
    {
        if (_loadingStableAnchorEditor || _editingStableAnchorRow is null ||
            sender is not ToggleButton { Tag: string mode }) return;

        var fixedMode = string.Equals(mode, "Fixed", StringComparison.Ordinal);
        _loadingStableAnchorEditor = true;
        StableAnchorAdaptiveButton.IsChecked = !fixedMode;
        StableAnchorFixedButton.IsChecked = fixedMode;
        StableAnchorSlider.IsEnabled = fixedMode;
        StableAnchorResetButton.Visibility = fixedMode || _editingStableAnchorRow.IsFixedAnchor
            ? Visibility.Collapsed
            : Visibility.Visible;
        StableAnchorSlider.Value = Math.Clamp(
            fixedMode ? _editingStableAnchorRow.FixedAnchorMiB : _editingStableAnchorRow.AdaptiveAnchorMiB,
            StableAnchorSlider.Minimum,
            StableAnchorSlider.Maximum);
        _loadingStableAnchorEditor = false;
        if (fixedMode && !_editingStableAnchorRow.IsFixedAnchor)
            _stableAnchorValueChanged = true;
        UpdateStableAnchorValueLabel();
    }

    private void StableAnchorSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (StableAnchorValueLabel is null) return;
        if (!_loadingStableAnchorEditor) _stableAnchorValueChanged = true;
        UpdateStableAnchorValueLabel();
    }

    private void StableAnchorSlider_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateStableAnchorValueLabel();

    private void UpdateStableAnchorValueLabel()
    {
        if (StableAnchorSlider is null || StableAnchorValueLabel is null || StableAnchorValueCanvas is null) return;
        var displayedValue = _editingStableAnchorRow is { IsFixedAnchor: true } fixedRow &&
                             StableAnchorFixedButton.IsChecked == true &&
                             !_stableAnchorValueChanged
            ? fixedRow.FixedAnchorMiB
            : StableAnchorSlider.Value;
        StableAnchorValueLabel.Text = TF("StableAnchorValueFormat", displayedValue);
        StableAnchorValueLabel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelWidth = Math.Max(StableAnchorValueLabel.MinWidth, StableAnchorValueLabel.DesiredSize.Width);
        var sliderWidth = StableAnchorSlider.ActualWidth;
        var canvasWidth = StableAnchorValueCanvas.ActualWidth > 0
            ? StableAnchorValueCanvas.ActualWidth
            : sliderWidth;
        if (sliderWidth <= 0 || canvasWidth <= 0) return;
        var range = StableAnchorSlider.Maximum - StableAnchorSlider.Minimum;
        var ratio = range <= 0
            ? 0d
            : (StableAnchorSlider.Value - StableAnchorSlider.Minimum) / range;
        const double thumbWidth = 22d;
        var thumbCenter = thumbWidth / 2d + ratio * Math.Max(0d, sliderWidth - thumbWidth);
        Canvas.SetLeft(
            StableAnchorValueLabel,
            Math.Clamp(thumbCenter - labelWidth / 2d, 0d, Math.Max(0d, canvasWidth - labelWidth)));
    }

    private void StableAnchorCancel_OnClick(object sender, RoutedEventArgs e) =>
        StableAnchorPopup.IsOpen = false;

    private void StableAnchorResetLearning_OnClick(object sender, RoutedEventArgs e)
    {
        var row = _editingStableAnchorRow;
        if (row is null || row.IsFixedAnchor || StableAnchorAdaptiveButton.IsChecked != true ||
            string.IsNullOrWhiteSpace(row.ScopeKey)) return;
        var confirmation = ShowThemedMessage(
            T("StableAnchorResetLearning"),
            TF("StableAnchorResetLearningConfirmFormat", row.Application),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            affirmativeText: T("StableAnchorResetLearningAction"),
            negativeText: T("Cancel"),
            destructiveAffirmative: true);
        if (confirmation != MessageBoxResult.Yes ||
            !_applicationBackoffTracker.ResetStableAnchorLearning(row.ScopeKey)) return;

        SaveBenefitLearning();
        StableAnchorPopup.IsOpen = false;
        RefreshBenefitLearningAnalysis();
        UpdatePreviewRows();
        RefreshProtectedList();
        _state.Status = TF("StableAnchorResetLearningCompletedFormat", row.Application);
    }

    private void StableAnchorApply_OnClick(object sender, RoutedEventArgs e)
    {
        var row = _editingStableAnchorRow;
        if (row is null || string.IsNullOrWhiteSpace(row.ScopeKey)) return;
        var fixedMode = StableAnchorFixedButton.IsChecked == true;
        var currentRow = _state.BenefitLearningRows.FirstOrDefault(candidate =>
            string.Equals(candidate.FamilyKey, row.FamilyKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ScopeKey, row.ScopeKey, StringComparison.OrdinalIgnoreCase));
        if (currentRow is null)
        {
            ShowThemedMessage(T("StableAnchorSettings"), T("StableAnchorScopeChanged"),
                image: MessageBoxImage.Warning);
            StableAnchorPopup.IsOpen = false;
            return;
        }
        var existing = _settings.StableAnchorSettings.LastOrDefault(anchor =>
            string.Equals(anchor.ScopeKey, row.ScopeKey, StringComparison.OrdinalIgnoreCase));
        if (fixedMode && !currentRow.HasAnchorReferenceRange &&
            !(existing is { Mode: StableAnchorMode.Fixed, FixedAnchorBytes: > 0 } &&
              !_stableAnchorValueChanged)) return;

        var fixedBytes = existing is { Mode: StableAnchorMode.Fixed, FixedAnchorBytes: > 0 } &&
                         !_stableAnchorValueChanged
            ? existing.FixedAnchorBytes
            : StableAnchorLearningPolicy.ClampFixedAnchorBytes(
                (long)Math.Clamp(
                    Math.Round(StableAnchorSlider.Value * 1024d * 1024d),
                    1d,
                    long.MaxValue),
                currentRow.AnchorMinimumBytes,
                currentRow.AnchorMaximumBytes);
        if (fixedMode && _stableAnchorValueChanged &&
            _settings.ResolveStableStateSuppressionSettings() is { } suppressionSettings)
        {
            var newLimit = StableStateSuppressionPolicy.SuppressionLimitBytes(fixedBytes, suppressionSettings);
            if (currentRow.CurrentWorkingSetBytes > newLimit &&
                ShowThemedMessage(
                    T("StableAnchorLowerLimitConfirmTitle"),
                    TF(
                        "StableAnchorLowerLimitConfirmFormat",
                        currentRow.Application,
                        DisplayFormat.Bytes(currentRow.CurrentWorkingSetBytes),
                        DisplayFormat.Bytes(newLimit)),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    affirmativeText: T("Apply"),
                    negativeText: T("Cancel")) != MessageBoxResult.Yes) return;
        }
        if (!TryUpdateSettings(settings =>
            {
                settings.StableAnchorSettings.RemoveAll(anchor =>
                    string.Equals(anchor.ScopeKey, row.ScopeKey, StringComparison.OrdinalIgnoreCase));
                settings.StableAnchorSettings.Add(new ApplicationStableAnchorSetting(
                    row.FamilyKey,
                    row.ScopeKey,
                    fixedMode ? StableAnchorMode.Fixed : StableAnchorMode.Adaptive,
                    fixedMode ? fixedBytes : existing?.FixedAnchorBytes ?? fixedBytes));
            })) return;

        StableAnchorPopup.IsOpen = false;
        RefreshBenefitLearningAnalysis();
        UpdatePreviewRows();
        RefreshProtectedList();
        _state.Status = T(fixedMode ? "StableAnchorFixedSaved" : "StableAnchorAdaptiveSaved");
    }

    private void StableAnchorPopup_OnClosed(object? sender, EventArgs e)
    {
        _editingStableAnchorRow = null;
        _stableAnchorValueChanged = false;
        StableAnchorPopup.PlacementTarget = null;
    }

    private void RefreshBenefitLearningAnalysis()
    {
        var stableSettings = _settings.IntelligentCandidateSelection
            ? _settings.ResolveStableStateSuppressionSettings()
            : null;
        var stableSamplesPerLaunch = stableSettings?.MaximumStableSamplesPerLaunch ??
                                     ApplicationReboundBackoffTracker.MaximumStableSamplesPerLaunch;
        var stableSamplePool = stableSettings?.MaximumStableSamplePool ??
                               StableWorkingSetLearningPolicy.DefaultRecentSamples;
        var stableValidationMinutes = stableSettings?.MaximumStableValidationDuration.TotalMinutes ??
                                      StableStateSuppressionSettings
                                          .DefaultMaximumStableValidationDuration.TotalMinutes;
        var stableSampleIntervalMinutes = stableSettings?.NaturalStableSampleInterval.TotalMinutes ??
                                          ApplicationReboundBackoffTracker.NaturalStableSampleInterval.TotalMinutes;
        var stableOptimizationSettings = _settings.ResolveOptimizationSettings(manual: false);
        var stableProtection = CurrentProtectionRules();
        var stableSnapshotObservedAt = DateTimeOffset.Now;
        var stableSnapshotsByScope = StableStateSuppressionPolicy.NaturalStableStateSnapshots(
                _families,
                stableOptimizationSettings,
                stableProtection,
                _candidateIdleReadiness,
                _applicationBackoffTracker.FamilyStableLearningRecords,
                _applicationBackoffTracker.NaturalStableScopeRequests(stableSnapshotObservedAt))
            .ToDictionary(snapshot => snapshot.ScopeKey, StringComparer.OrdinalIgnoreCase);
        var suggestionFamilies = _displayedProtectionSuggestions
            .Concat(_currentProtectionSuggestions)
            .Select(suggestion => suggestion.FamilyKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var metrics = _applicationBackoffTracker.LearningRecords
            .Where(record => record.ValidSampleCount > 0)
            .GroupBy(record => record.FamilyKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var records = group.ToArray();
                var runningFamily = _families.FirstOrDefault(family => string.Equals(
                    family.Key, group.Key, StringComparison.OrdinalIgnoreCase));
                var familyStableRecords = _applicationBackoffTracker.FamilyStableLearningRecords
                    .Where(record => string.Equals(
                        record.FamilyKey,
                        group.Key,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(record => record.StableLastObservedAt)
                    .ToArray();
                var familyStableRecord = runningFamily is null
                    ? null
                    : StableStateSuppressionPolicy.ActiveStableRecord(
                        runningFamily,
                        _families,
                        familyStableRecords,
                        stableOptimizationSettings,
                        stableProtection);
                var displayedStableRecord = familyStableRecord ?? familyStableRecords.FirstOrDefault();
                var currentStableScopeMatched = familyStableRecord is not null;
                NaturalStableStateSnapshot? currentStableSnapshot = null;
                if (familyStableRecord is not null)
                {
                    stableSnapshotsByScope.TryGetValue(
                        ApplicationStableScopeIdentity.For(familyStableRecord),
                        out currentStableSnapshot);
                }
                currentStableSnapshot ??= stableSnapshotsByScope.Values.FirstOrDefault(snapshot =>
                    string.Equals(snapshot.FamilyKey, group.Key, StringComparison.OrdinalIgnoreCase));
                var displayedRecordScopeKey = displayedStableRecord is null
                    ? string.Empty
                    : ApplicationStableScopeIdentity.For(displayedStableRecord);
                var currentScopeKey = currentStableSnapshot?.ScopeKey;
                var anchorSetting = _settings.StableAnchorSettings.LastOrDefault(anchor =>
                                        string.Equals(anchor.ScopeKey, currentScopeKey, StringComparison.OrdinalIgnoreCase)) ??
                                    _settings.StableAnchorSettings.LastOrDefault(anchor =>
                                        string.Equals(anchor.ScopeKey, displayedRecordScopeKey, StringComparison.OrdinalIgnoreCase));
                var displayedScopeKey = anchorSetting?.ScopeKey ?? currentScopeKey ?? displayedRecordScopeKey;
                var anchorScopeMatched = currentStableScopeMatched &&
                                         currentStableSnapshot is not null &&
                                         string.Equals(
                                             currentStableSnapshot.ScopeKey,
                                             displayedScopeKey,
                                             StringComparison.OrdinalIgnoreCase);
                var fixedAnchorConfigured =
                    anchorSetting is { Mode: StableAnchorMode.Fixed, FixedAnchorBytes: > 0 };
                var validSamples = records.Sum(record => record.ValidSampleCount);
                var sampleWeight = records.Sum(record => Math.Max(1, record.ValidSampleCount));
                var lowerWorkingSet = records.Sum(record => LearningPercentile(record, 0.25));
                var upperWorkingSet = records.Sum(record => LearningPercentile(record, 0.75));
                var workingSet = lowerWorkingSet == upperWorkingSet
                    ? DisplayFormat.Bytes(lowerWorkingSet)
                    : $"{DisplayFormat.Bytes(lowerWorkingSet)} - {DisplayFormat.Bytes(upperWorkingSet)}";
                var rebound = records.Sum(record =>
                    record.AverageReboundPercent * Math.Max(1, record.ValidSampleCount)) / sampleWeight;
                var lastObserved = records.Max(record => record.LastObservedAt).LocalDateTime;
                var familyReference = displayedStableRecord is null
                    ? null
                    : StableStateSuppressionPolicy.StableReferenceBytes(
                        displayedStableRecord,
                        stableSamplePool);
                var displayedReferenceRange = displayedStableRecord is null
                    ? null
                    : StableAnchorLearningPolicy.ReferenceRange(
                        displayedStableRecord,
                        stableSamplePool);
                var minimumAnchorSamples = stableSettings?.MinimumSamples ??
                                           StableStateSuppressionSettings.For(OptimizationProfile.Turbo).MinimumSamples;
                var acceptedStableSampleCount = displayedStableRecord is null
                    ? 0
                    : StableAnchorLearningPolicy.AcceptedSampleCount(
                        displayedStableRecord,
                        stableSamplePool);
                var preliminaryReferenceRange = displayedReferenceRange is { } availableRange &&
                                                acceptedStableSampleCount >= minimumAnchorSamples
                    ? availableRange
                    : null;
                var stableRecordFresh = stableSettings is not null &&
                                        displayedStableRecord?.StableLastObservedAt is { } anchorObservedAt &&
                                        DateTimeOffset.UtcNow - anchorObservedAt < stableSettings.MaximumRecordAge;
                var displayedScopeConfigurable = stableSettings is not null &&
                                                 displayedStableRecord is not null &&
                                                 displayedStableRecord.ComponentKeys.Count > 0 &&
                                                 !string.IsNullOrWhiteSpace(displayedRecordScopeKey) &&
                                                 preliminaryReferenceRange is not null &&
                                                 stableRecordFresh;
                var fixedAnchorDisplaySupported = fixedAnchorConfigured &&
                                                  displayedScopeConfigurable &&
                                                  string.Equals(
                                                      anchorSetting!.ScopeKey,
                                                      displayedRecordScopeKey,
                                                      StringComparison.OrdinalIgnoreCase);
                var effectiveAnchor = fixedAnchorDisplaySupported
                    ? anchorSetting!.FixedAnchorBytes
                    : familyReference;
                long? calculatedStableLimit = stableSettings is null || !effectiveAnchor.HasValue
                    ? null
                    : StableStateSuppressionPolicy.SuppressionLimitBytes(
                        effectiveAnchor.Value,
                        stableSettings);
                var stableLimits = calculatedStableLimit.HasValue
                    ? new[] { calculatedStableLimit.Value }
                    : Array.Empty<long>();
                var stableReferences = effectiveAnchor.HasValue
                    ? new[] { effectiveAnchor.Value }
                    : Array.Empty<long>();
                var stableReferenceBytes = SumBytes(stableReferences);
                var stableLimitBytes = stableLimits.Aggregate(
                    0L,
                    (total, value) => value > long.MaxValue - total ? long.MaxValue : total + value);
                var normalizedStableSamples = displayedStableRecord is null
                    ? Array.Empty<ApplicationStableSample>()
                    : StableAnchorLearningPolicy.NormalizeSamples(displayedStableRecord)
                        .TakeLast(stableSamplePool)
                        .ToArray();
                var displayedStableScopeKey = displayedStableRecord is null
                    ? string.Empty
                    : ApplicationStableScopeIdentity.For(displayedStableRecord);
                var historicalStableRecords = familyStableRecords
                    .Where(record => !string.Equals(
                        ApplicationStableScopeIdentity.For(record),
                        displayedStableScopeKey,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var historicalStableSampleCount = historicalStableRecords.Sum(record =>
                    StableAnchorLearningPolicy.NormalizeSamples(record)
                        .TakeLast(stableSamplePool)
                        .Count());
                var historicalStableScopeSuffix = historicalStableRecords.Length == 0
                    ? string.Empty
                    : TF(
                        "LearningStableHistoricalScopesFormat",
                        historicalStableRecords.Length,
                        historicalStableSampleCount);
                var stableSampleCount = displayedStableRecord is null
                    ? 0
                    : StableAnchorLearningPolicy.AcceptedSampleCount(
                        displayedStableRecord,
                        stableSamplePool);
                var pendingClusterSampleCount = normalizedStableSamples.Count(sample =>
                    sample.Generation <= 0 && !sample.PendingHigh);
                var pendingHighSampleCount = normalizedStableSamples.Count(sample => sample.PendingHigh);
                var currentLaunchStableSampleCount = displayedStableRecord is not null &&
                                                     stableSnapshotsByScope.TryGetValue(
                                                         ApplicationStableScopeIdentity.For(displayedStableRecord),
                                                         out var stableSnapshot) &&
                                                     string.Equals(
                                                         displayedStableRecord.LastStableLaunchSignature,
                                                         stableSnapshot.LaunchSignature,
                                                         StringComparison.Ordinal)
                    ? Math.Clamp(StableAnchorLearningPolicy.AcceptedSampleCountForLaunch(
                            displayedStableRecord,
                            stableSnapshot.LaunchSignature,
                            stableSamplePool),
                        0, stableSamplesPerLaunch)
                    : 0;
                var stableSampleLastUpdated = displayedStableRecord?.StableLastObservedAt is { } stableObservedAt
                    ? stableObservedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)
                    : "--";
                var benefitSampleLastUpdated = lastObserved.ToString("g", CultureInfo.CurrentCulture);
                var stableSamplesHelp = stableSettings is null
                    ? TF("LearningStableSamplesHelpDisabledFormat", benefitSampleLastUpdated)
                    : TF(
                        "LearningStableSamplesHelpFormat",
                        currentLaunchStableSampleCount,
                        stableSamplesPerLaunch,
                        T(currentLaunchStableSampleCount >= stableSamplesPerLaunch
                            ? "LearningStableSamplesRolling"
                            : "LearningStableSamplesCollecting"),
                        stableSampleCount,
                        stableSamplePool,
                        T(stableSampleCount >= stableSamplePool
                            ? "LearningStableSamplePoolRolling"
                            : "LearningStableSamplePoolCollecting"),
                        stableSampleIntervalMinutes,
                        stableSampleLastUpdated,
                        benefitSampleLastUpdated,
                        pendingClusterSampleCount,
                        pendingHighSampleCount);
                var candidateSummary = StableCandidateSummary(group.Key, stableValidationMinutes);
                var stableHistory = StableWorkingSetLearningPolicy.NormalizeSampleHistory(
                    displayedStableRecord?.StableWorkingSetSamplesBytes,
                    stableSamplePool);
                var referenceRange = preliminaryReferenceRange;
                var technicalCenter = Math.Max(
                    currentStableSnapshot?.WorkingSetBytes ?? 0,
                    effectiveAnchor ?? familyReference ?? upperWorkingSet);
                const long mib = 1024L * 1024L;
                var technicalMinimum = Math.Max(mib, Math.Min(
                    stableOptimizationSettings.MinimumFamilyWorkingSetBytes,
                    technicalCenter > 0 ? technicalCenter / 2 : stableOptimizationSettings.MinimumFamilyWorkingSetBytes));
                var technicalMaximum = Math.Max(
                    technicalMinimum + 256L * mib,
                    technicalCenter > long.MaxValue / 2 ? long.MaxValue : technicalCenter * 2);
                var anchorMinimum = referenceRange?.MinimumBytes ?? technicalMinimum;
                var anchorMaximum = referenceRange?.MaximumBytes ?? technicalMaximum;
                if (anchorMaximum <= anchorMinimum) anchorMaximum = anchorMinimum + mib;
                var anchorMinimumMiB = Math.Ceiling(anchorMinimum / (double)mib);
                var anchorMaximumMiB = Math.Floor(anchorMaximum / (double)mib);
                if (anchorMaximumMiB < anchorMinimumMiB)
                {
                    anchorMinimumMiB = anchorMinimum / (double)mib;
                    anchorMaximumMiB = anchorMaximum / (double)mib;
                }
                var adaptiveAnchor = familyReference ?? currentStableSnapshot?.WorkingSetBytes ?? anchorMinimum;
                var fixedAnchor = anchorSetting?.FixedAnchorBytes > 0
                    ? anchorSetting.FixedAnchorBytes
                    : adaptiveAnchor;
                var previousHistory = stableHistory.Take(Math.Max(0, stableHistory.Count - 1)).ToArray();
                var previousEstimate = previousHistory.Length == 0
                    ? (long?)null
                    : StableWorkingSetLearningPolicy.Median(previousHistory.OrderBy(value => value).ToArray());
                var currentEstimate = familyReference;
                var trendThreshold = previousEstimate.HasValue
                    ? Math.Max(8L * mib, (long)Math.Round(previousEstimate.Value * 0.05d))
                    : long.MaxValue;
                var trendDelta = currentEstimate.HasValue && previousEstimate.HasValue
                    ? currentEstimate.Value - previousEstimate.Value
                    : 0;
                var trendGlyph = Math.Abs((double)trendDelta) < trendThreshold
                    ? string.Empty
                    : trendDelta > 0 ? "↑" : "↓";
                var trendHelp = trendGlyph.Length == 0
                    ? string.Empty
                    : TF(
                        trendDelta > 0 ? "StableAnchorTrendUpFormat" : "StableAnchorTrendDownFormat",
                        DisplayFormat.Bytes(Math.Abs(trendDelta)),
                        DisplayFormat.Bytes(previousEstimate!.Value),
                        DisplayFormat.Bytes(currentEstimate!.Value));
                var anchorDisplay = stableReferences.Length == 0
                    ? "--"
                    : DisplayFormat.Bytes(stableReferenceBytes);
                var limitDisplay = stableLimits.Length == 0
                    ? "--"
                    : DisplayFormat.Bytes(stableLimitBytes);
                var referenceRangeDisplay = displayedReferenceRange is null
                    ? "--"
                    : displayedReferenceRange.MinimumBytes == displayedReferenceRange.MaximumBytes
                        ? DisplayFormat.Bytes(displayedReferenceRange.MinimumBytes)
                        : $"{DisplayFormat.Bytes(displayedReferenceRange.MinimumBytes)} - {DisplayFormat.Bytes(displayedReferenceRange.MaximumBytes)}";
                var currentFamilyWorkingSet = currentStableSnapshot?.WorkingSetBytes ??
                    runningFamily?.Processes.Aggregate(0L, (total, process) =>
                    {
                        var bytes = Math.Max(0, process.WorkingSetBytes);
                        return bytes > long.MaxValue - total ? long.MaxValue : total + bytes;
                    });
                var currentOccupancyDisplay = currentFamilyWorkingSet is not > 0
                    ? "--"
                    : DisplayFormat.Bytes(currentFamilyWorkingSet.Value);
                var anchorSummaryHelp = TF(
                    currentStableScopeMatched
                        ? "StableAnchorSummaryFormat"
                        : "StableHistoricalAnchorSummaryFormat",
                    currentOccupancyDisplay,
                    anchorDisplay,
                    referenceRangeDisplay,
                    limitDisplay);
                var canConfigureAnchor = displayedScopeConfigurable;
                var anchorSettingsHelp = canConfigureAnchor
                    ? runningFamily is null
                        ? T("StableAnchorSettingsOffline")
                        : !anchorScopeMatched
                            ? T("StableAnchorSettingsHistoricalScope")
                            : T("StableAnchorSettings")
                    : stableSettings is null
                        ? T("StableAnchorSettingsDisabled")
                        : preliminaryReferenceRange is null && displayedStableRecord is not null
                            ? TF(
                                "StableAnchorSamplesRequiredFormat",
                                stableSettings.MinimumSamples,
                                acceptedStableSampleCount)
                            : displayedStableRecord is not null && !stableRecordFresh
                                ? T("StableAnchorRecordExpired")
                                : T("StableAnchorScopeUnavailable");
                var stableLimitHelp = displayedStableRecord is not null && !currentStableScopeMatched
                    ? TF(
                        "StableReferenceInactiveScopeHelpFormat",
                        stableSampleCount,
                        familyReference.HasValue ? DisplayFormat.Bytes(familyReference.Value) : "--",
                        candidateSummary,
                        currentLaunchStableSampleCount,
                        stableSamplesPerLaunch,
                        stableSamplePool,
                        stableSampleLastUpdated)
                    : stableSettings is null
                    ? TF(
                        "StableReferenceHelpDisabledFormat",
                        stableReferences.Length,
                        1,
                        stableSampleCount,
                        candidateSummary,
                        currentLaunchStableSampleCount,
                        stableSamplesPerLaunch,
                        stableSamplePool,
                        stableSampleLastUpdated)
                    : TF(
                        "StableReferenceHelpEnabledFormat",
                        stableReferences.Length,
                        1,
                        stableSampleCount,
                        DisplayFormat.Bytes(stableSettings.AbsoluteGrowthMarginBytes),
                        stableSettings.RelativeGrowthMargin * 100d,
                        stableSettings.MinimumSamples,
                        stableSettings.MaximumRecordAge.TotalDays,
                        stableLimits.Length,
                        candidateSummary,
                        currentLaunchStableSampleCount,
                        stableSamplesPerLaunch,
                        stableSamplePool,
                        stableSampleLastUpdated,
                        stableValidationMinutes,
                        stableSampleIntervalMinutes);
                return new
                {
                    FamilyKey = group.Key,
                    Application = ResolveLearningApplicationName(group.Key, records, displayedStableRecord),
                    StableScopeDescription = TF(
                        "StableAnchorScopeFormat",
                        ResolveLearningScopeName(records, displayedStableRecord)),
                    WorkingSet = workingSet,
                    WorkingSetBytes = upperWorkingSet,
                    StableAnchor = TF(
                        currentStableScopeMatched ? "StableAnchorFormat" : "StableHistoricalAnchorFormat",
                        anchorDisplay),
                    StableUpperLimit = TF(
                        currentStableScopeMatched
                            ? "StableUpperLimitFormat"
                            : "StableHistoricalUpperLimitFormat",
                        limitDisplay),
                    StableAnchorSummaryHelp = anchorSummaryHelp,
                    StableAnchorSettingsHelp = anchorSettingsHelp,
                    ScopeKey = displayedScopeKey,
                    TrendGlyph = trendGlyph,
                    TrendHelp = trendHelp,
                    FixedAnchor = fixedAnchorConfigured,
                    CanConfigureAnchor = canConfigureAnchor,
                    HasAnchorReferenceRange = displayedScopeConfigurable,
                    AdaptiveAnchorMiB = adaptiveAnchor / 1024d / 1024d,
                    FixedAnchorMiB = fixedAnchor / 1024d / 1024d,
                    AnchorMinimumMiB = anchorMinimumMiB,
                    AnchorMaximumMiB = anchorMaximumMiB,
                    AnchorMinimumBytes = anchorMinimum,
                    AnchorMaximumBytes = anchorMaximum,
                    CurrentWorkingSetBytes = currentFamilyWorkingSet ?? 0,
                    SustainedReleaseBytes = records.Sum(record => record.AverageRetainedBytes),
                    Rebound = rebound,
                    BenefitSamples = TF(
                        "LearningBenefitSampleSummaryFormat",
                        validSamples,
                        records.Max(record => record.DistinctLaunchCount)),
                    StableSamples = stableSettings is null
                        ? T("LearningStableSamplesDisabled")
                        : displayedStableRecord is not null && !currentStableScopeMatched
                            ? TF(
                                "LearningStableSampleInactiveScopeFormat",
                                currentLaunchStableSampleCount,
                                stableSamplesPerLaunch,
                                stableSampleCount,
                                stableSamplePool) + historicalStableScopeSuffix
                        : TF(
                            "LearningStableSampleProgressFormat",
                            currentLaunchStableSampleCount,
                            stableSamplesPerLaunch,
                            stableSampleCount,
                            stableSamplePool) + historicalStableScopeSuffix,
                    StableSamplesHelp = stableSamplesHelp,
                    Launches = records.Max(record => record.DistinctLaunchCount),
                    LastObserved = lastObserved,
                    Suggested = suggestionFamilies.Contains(group.Key)
                };
            })
            .ToArray();
        var scaleBytes = Math.Max(
            1L,
            metrics.Select(metric => Math.Max(metric.WorkingSetBytes, metric.SustainedReleaseBytes))
                .DefaultIfEmpty(0)
                .Max());
        var rows = metrics
            .OrderByDescending(metric => metric.WorkingSetBytes)
            .ThenBy(metric => metric.Application, StringComparer.CurrentCultureIgnoreCase)
            .Select(metric => new BenefitLearningRow(
                metric.FamilyKey,
                metric.ScopeKey,
                metric.Application,
                metric.StableScopeDescription,
                metric.WorkingSet,
                metric.StableAnchor,
                metric.StableUpperLimit,
                metric.StableAnchorSummaryHelp,
                metric.StableAnchorSettingsHelp,
                metric.TrendGlyph,
                metric.TrendHelp,
                metric.FixedAnchor,
                metric.CanConfigureAnchor,
                metric.HasAnchorReferenceRange,
                metric.AdaptiveAnchorMiB,
                metric.FixedAnchorMiB,
                metric.AnchorMinimumMiB,
                metric.AnchorMaximumMiB,
                metric.AnchorMinimumBytes,
                metric.AnchorMaximumBytes,
                DisplayFormat.Bytes(metric.SustainedReleaseBytes),
                metric.WorkingSetBytes / (double)scaleBytes * 100d,
                metric.SustainedReleaseBytes / (double)scaleBytes * 100d,
                $"{metric.Rebound:0.0}%",
                metric.BenefitSamples,
                metric.StableSamples,
                metric.StableSamplesHelp,
                metric.LastObserved.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture),
                metric.Suggested ? T("ProtectionSuggested") : T("NoProtectionSuggestion"))
            {
                CurrentWorkingSetBytes = metric.CurrentWorkingSetBytes
            });
        SynchronizeCollection(_state.BenefitLearningRows, rows);
        ReviewProtectionSuggestionsButton.IsEnabled =
            !_state.IsBusy && (_displayedProtectionSuggestions.Count > 0 || _currentProtectionSuggestions.Count > 0);
    }

    private string ResolveLearningApplicationName(
        string familyKey,
        IReadOnlyList<ApplicationBenefitLearningRecord> records,
        ApplicationStableLearningRecord? displayedStableRecord)
    {
        var runningFamily = _families.FirstOrDefault(family =>
            string.Equals(family.Key, familyKey, StringComparison.OrdinalIgnoreCase));
        if (runningFamily is not null) return runningFamily.DisplayName;
        var scopedComponentKeys = displayedStableRecord?.ComponentKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var path = records
            .Where(record => scopedComponentKeys is null ||
                             (!string.IsNullOrWhiteSpace(record.ComponentKey) &&
                              scopedComponentKeys.Contains(record.ComponentKey)))
            .OrderByDescending(record => record.ValidSampleCount)
            .ThenBy(record => record.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(record => record.ExecutablePath)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
            records.OrderByDescending(record => record.ValidSampleCount)
                .Select(record => record.ExecutablePath)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return string.IsNullOrWhiteSpace(path)
            ? T("UnknownApplication")
            : IOPath.GetFileNameWithoutExtension(path);
    }

    private static string ResolveLearningScopeName(
        IReadOnlyList<ApplicationBenefitLearningRecord> records,
        ApplicationStableLearningRecord? displayedStableRecord)
    {
        if (displayedStableRecord is null) return "--";
        var componentKeys = displayedStableRecord.ComponentKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = records
            .Where(record => !string.IsNullOrWhiteSpace(record.ComponentKey) &&
                             componentKeys.Contains(record.ComponentKey))
            .OrderByDescending(record => record.ValidSampleCount)
            .ThenBy(record => record.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(record => IOPath.GetFileName(record.ExecutablePath))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 0 ? "--" : string.Join(" + ", names);
    }

    private static long LearningPercentile(ApplicationBenefitLearningRecord record, double percentile)
    {
        var samples = (record.LateWorkingSetSamplesBytes ?? Array.Empty<long>())
            .Where(value => value >= 0)
            .OrderBy(value => value)
            .ToArray();
        if (samples.Length == 0) return Math.Max(0, record.AverageLateWorkingSetBytes);
        var index = (int)Math.Round((samples.Length - 1) * percentile, MidpointRounding.AwayFromZero);
        return samples[Math.Clamp(index, 0, samples.Length - 1)];
    }

    private string StableCandidateSummary(string familyKey, double observationMinutes)
    {
        var statuses = _applicationBackoffTracker.StableCandidateStatuses
            .Where(status => string.Equals(status.FamilyKey, familyKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (statuses.Length == 0)
        {
            return _families.Any(family => string.Equals(family.Key, familyKey, StringComparison.OrdinalIgnoreCase))
                ? T("StableSessionAwaiting")
                : T("StableSessionTargetNotRunning");
        }

        if (statuses.Length == 1)
        {
            var status = statuses[0];
            return status.State switch
            {
                ApplicationStableCandidateState.Provisional => TF(
                    "StableSessionProvisionalFormat",
                    DisplayFormat.Bytes(status.CandidateBytes),
                    status.ConsecutiveObservationCount),
                ApplicationStableCandidateState.Converged => TF(
                    "StableSessionConvergedFormat",
                    DisplayFormat.Bytes(status.CandidateBytes),
                    observationMinutes),
                _ => T("StableSessionExcluded")
            };
        }

        return TF(
            "StableSessionMultipleFormat",
            statuses.Count(status => status.State == ApplicationStableCandidateState.Converged),
            statuses.Count(status => status.State == ApplicationStableCandidateState.Provisional),
            statuses.Count(status => status.State == ApplicationStableCandidateState.Excluded));
    }

    private static long SumBytes(IEnumerable<long> values) => values.Aggregate(
        0L,
        (total, value) => value > long.MaxValue - total ? long.MaxValue : total + value);

    private void ShowProtectionSuggestionsDialog(IReadOnlyList<ProtectionSuggestion> suggestions)
    {
        var rules = ApplicationProtectionSettings.Resolve(_settings);
        var candidates = RunningProtectionCandidateCatalog.Create(_families, rules)
            .Where(candidate => suggestions.Any(suggestion => string.Equals(
                suggestion.FamilyKey,
                candidate.FamilyKey,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var suggestionPathsByFamily = suggestions
            .Where(suggestion => !string.IsNullOrWhiteSpace(suggestion.ExecutablePath))
            .GroupBy(suggestion => suggestion.FamilyKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(suggestion => NormalizeExecutablePath(suggestion.ExecutablePath!))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var selectionPanel = new StackPanel();
        var familyChoices = new List<(System.Windows.Controls.CheckBox CheckBox, RunningProtectionCandidate Candidate)>();
        var executableChoices = new List<(System.Windows.Controls.CheckBox CheckBox, RunningProtectionCandidate Candidate, string Path)>();
        foreach (var candidate in candidates)
        {
            if (!suggestionPathsByFamily.TryGetValue(candidate.FamilyKey, out var suggestedPaths)) continue;
            selectionPanel.Children.Add(new TextBlock
            {
                Text = candidate.DisplayName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Margin = new Thickness(0, selectionPanel.Children.Count == 0 ? 0 : 16, 0, 6)
            });

            var eligibleExecutables = candidate.Executables
                .Where(executable => !executable.IsProtected)
                .ToArray();
            var entireFamilySuggested = eligibleExecutables.Length > 0 && eligibleExecutables.All(executable =>
                suggestedPaths.Contains(NormalizeExecutablePath(executable.ExecutablePath)));
            var familyCheckBox = new System.Windows.Controls.CheckBox
            {
                Content = T("ProtectEntireSuggestedFamily"),
                IsEnabled = entireFamilySuggested,
                Style = (Style)FindResource("ThemedCheckBoxStyle"),
                Margin = new Thickness(0, 2, 0, 5),
                ToolTip = entireFamilySuggested
                    ? T("ProtectEntireSuggestedFamilyHelp")
                    : T("ProtectEntireSuggestedFamilyUnavailable")
            };
            familyChoices.Add((familyCheckBox, candidate));
            selectionPanel.Children.Add(familyCheckBox);

            var familyExecutableChoices = new List<System.Windows.Controls.CheckBox>();
            foreach (var executable in candidate.Executables)
            {
                var normalizedPath = NormalizeExecutablePath(executable.ExecutablePath);
                var suggested = suggestedPaths.Contains(normalizedPath) && !executable.IsProtected;
                var matchingSuggestion = suggestions.FirstOrDefault(suggestion =>
                    string.Equals(suggestion.FamilyKey, candidate.FamilyKey, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(suggestion.ExecutablePath) &&
                    string.Equals(
                        NormalizeExecutablePath(suggestion.ExecutablePath!),
                        normalizedPath,
                        StringComparison.OrdinalIgnoreCase));
                var checkBox = new System.Windows.Controls.CheckBox
                {
                    Content = TF(
                        "ProtectionSuggestionExecutableFormat",
                        executable.Name,
                        executable.InstanceCount,
                        DisplayFormat.Bytes(executable.WorkingSetBytes)),
                    IsEnabled = suggested,
                    Style = (Style)FindResource("ThemedCheckBoxStyle"),
                    Margin = new Thickness(20, 3, 0, 3),
                    ToolTip = matchingSuggestion is null
                        ? T("ProtectionSuggestionNotRecommended")
                        : TF(
                            "ProtectionSuggestionReasonFormat",
                            matchingSuggestion.SampleCount,
                            matchingSuggestion.AverageReboundPercent)
                };
                familyExecutableChoices.Add(checkBox);
                executableChoices.Add((checkBox, candidate, normalizedPath));
                selectionPanel.Children.Add(checkBox);
            }

            familyCheckBox.Checked += (_, _) =>
            {
                foreach (var checkBox in familyExecutableChoices) checkBox.IsEnabled = false;
            };
            familyCheckBox.Unchecked += (_, _) =>
            {
                foreach (var choice in executableChoices.Where(choice =>
                             ReferenceEquals(choice.Candidate, candidate)))
                {
                    choice.CheckBox.IsEnabled = suggestedPaths.Contains(choice.Path) &&
                                                !candidate.Executables.Any(executable =>
                                                    executable.IsProtected &&
                                                    string.Equals(
                                                        NormalizeExecutablePath(executable.ExecutablePath),
                                                        choice.Path,
                                                        StringComparison.OrdinalIgnoreCase));
                }
            };
        }

        var protect = new Button
        {
            Content = T("ProtectSelectedSuggestions"),
            IsEnabled = false,
            MinWidth = 128,
            Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        void UpdateProtectButton() => protect.IsEnabled =
            familyChoices.Any(choice => choice.CheckBox.IsChecked == true) ||
            executableChoices.Any(choice => choice.CheckBox.IsChecked == true);
        foreach (var choice in familyChoices)
        {
            choice.CheckBox.Checked += (_, _) => UpdateProtectButton();
            choice.CheckBox.Unchecked += (_, _) => UpdateProtectButton();
        }
        foreach (var choice in executableChoices)
        {
            choice.CheckBox.Checked += (_, _) => UpdateProtectButton();
            choice.CheckBox.Unchecked += (_, _) => UpdateProtectButton();
        }

        var ignore = new Button
        {
            Content = T("IgnoreProtectionSuggestions"),
            MinWidth = 96,
            IsCancel = true,
            Style = (Style)FindResource("ButtonStyle")
        };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(ignore);
        buttons.Children.Add(protect);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = T("ProtectionSuggestionsDescription"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (MediaBrush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 14)
        });
        var scroll = new ScrollViewer
        {
            Content = selectionPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = this,
            Title = T("ProtectionSuggestionsTitle"),
            Width = 620,
            Height = 560,
            MinHeight = 420,
            MaxHeight = 720,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush"),
            Content = root
        };
        protect.Click += (_, _) =>
        {
            var selections = new List<RunningProtectionSelection>();
            foreach (var candidate in candidates)
            {
                var protectFamily = familyChoices.Any(choice =>
                    ReferenceEquals(choice.Candidate, candidate) &&
                    choice.CheckBox.IsChecked == true);
                var selectedPaths = executableChoices
                    .Where(choice => ReferenceEquals(choice.Candidate, candidate) &&
                                     choice.CheckBox.IsChecked == true)
                    .Select(choice => choice.Path)
                    .ToArray();
                if (!protectFamily && selectedPaths.Length == 0) continue;
                var protectedPaths = candidate.Executables
                    .Where(executable => executable.IsProtected)
                    .Select(executable => executable.ExecutablePath)
                    .Concat(selectedPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                selections.Add(new RunningProtectionSelection(
                    candidate.ApplicationExecutablePath,
                    protectFamily
                        ? ApplicationProtectionState.EntireFamily
                        : ApplicationProtectionState.Partial,
                    protectedPaths,
                    candidate.MatchedRuleApplicationPaths));
            }

            var mergedRules = RunningProtectionCandidateCatalog.MergeSelections(rules, selections);
            if (!TryUpdateSettings(settings => ApplicationProtectionSettings.Replace(settings, mergedRules)))
                return;
            RefreshProtectedList();
            UpdateProcessRows();
            UpdatePreviewRows();
            UpdateBenefitLearningStatus();
            _state.Status = TF("ProtectionSuggestionsAddedFormat", selections.Count);
            dialog.DialogResult = true;
        };
        ApplyDialogTheme(dialog);
        _ = dialog.ShowDialog();
    }

    private void OpenDataFolder_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppDataPaths.RootDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppDataPaths.RootDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _state.Status = TF("OpenDataFolderFailureFormat", exception.Message);
            _diagnosticLog.Warning("Unable to open the local data directory.", exception);
        }
    }

    private void Theme_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settings.FollowSystemTheme) return;
        var light = !_settings.LightTheme;
        if (!TryUpdateSettings(settings => settings.LightTheme = light)) return;
        ApplyTheme(_settings.LightTheme);
    }

    private void FollowSystemThemeCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingControls) return;
        var requested = FollowSystemThemeCheckBox.IsChecked == true;
        var currentSystemTheme = SystemThemeService.IsLightTheme();
        if (!TryUpdateSettings(settings =>
            {
                settings.FollowSystemTheme = requested;
                settings.LightTheme = currentSystemTheme;
            }))
        {
            _syncingControls = true;
            FollowSystemThemeCheckBox.IsChecked = _settings.FollowSystemTheme;
            _syncingControls = false;
            return;
        }
        ThemeButton.IsEnabled = !_settings.FollowSystemTheme;
        QuickThemeButton.IsEnabled = !_settings.FollowSystemTheme;
        CompactThemeButton.IsEnabled = !_settings.FollowSystemTheme;
        ApplyTheme(_settings.LightTheme);
    }

    private void SystemEvents_OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_settings.FollowSystemTheme) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_settings.FollowSystemTheme) return;
            var light = SystemThemeService.IsLightTheme();
            ApplyTheme(light);
        });
    }

    private void ApplyTheme(bool light)
    {
        SetBrush("WindowBrush", light ? "#FAFAFA" : "#0B0B0C");
        SetBrush("SurfaceBrush", light ? "#FFFFFF" : "#111113");
        SetBrush("SurfaceRaisedBrush", light ? "#F4F4F5" : "#18181B");
        SetBrush("BorderBrush", light ? "#E4E4E7" : "#27272A");
        SetBrush("TextBrush", light ? "#18181B" : "#FAFAFA");
        SetBrush("MutedBrush", light ? "#71717A" : "#A1A1AA");
        SetBrush("NavigationBrush", light ? "#F4F4F5" : "#0F0F10");
        SetBrush("NavigationHoverBrush", light ? "#E4E4E7" : "#202024");
        SetBrush("NavigationPressedBrush", light ? "#D4D4D8" : "#2A2A30");
        SetBrush("AlternateRowBrush", light ? "#F4F4F5" : "#151518");
        SetBrush("ScrollTrackBrush", light ? "#E4E4E7" : "#27272A");
        SetBrush("ScrollThumbBrush", light ? "#A1A1AA" : "#71717A");
        SetBrush("ScrollThumbHoverBrush", light ? "#6384D6" : "#A8C0FF");
        SetBrush("AccentSoftBrush", light ? "#E8EEFC" : "#1C2540");
        SetBrush("AccentBrush", light ? "#4169B1" : "#7C9CEB");
        SetBrush("AccentHoverBrush", light ? "#31589B" : "#A8C0FF");
        SetBrush("AccentPressedBrush", light ? "#27477E" : "#6384D6");
        SetBrush("SuccessBrush", light ? "#15803D" : "#4ADE80");
        SetBrush("WarningBrush", light ? "#B45309" : "#D6A13A");
        SetBrush("WarningHoverBrush", light ? "#92400E" : "#E0B85C");
        SetBrush("WarningPressedBrush", light ? "#78350F" : "#B98224");
        SetBrush("ActionTextBrush", "#FFFFFF");
        SetBrush("BrandLogoBrush", light ? "#FFFFFF" : "#09090B");
        SetBrush("BrandLogoTextBrush", light ? "#18181B" : "#FAFAFA");
        SetBrush("BrandLogoBorderBrush", light ? "#E4E4E7" : "#27272A");
        SetBrush("LiteBrush", light ? "#15803D" : "#4ADE80");
        SetBrush("TurboBrush", light ? "#B45309" : "#D6A13A");
        SetBrush("UltimateBrush", light ? "#B91C1C" : "#F87171");
        var themeIcon = light ? "\uE706" : "\uE708";
        var toggleThemeResourceKey = light ? "ToggleToDarkTheme" : "ToggleToLightTheme";
        var themeGeometry = (Geometry)FindResource(light ? "IconSun" : "IconMoon");
        ThemeButtonIcon.Data = themeGeometry;
        QuickThemeIcon.Data = themeGeometry;
        CompactThemeIcon.Data = themeGeometry;
        QuickThemeButton.SetResourceReference(ToolTipProperty, toggleThemeResourceKey);
        QuickThemeButton.SetResourceReference(AutomationProperties.NameProperty, toggleThemeResourceKey);
        CompactThemeButton.SetResourceReference(ToolTipProperty, toggleThemeResourceKey);
        CompactThemeButton.SetResourceReference(AutomationProperties.NameProperty, toggleThemeResourceKey);
        Background = (MediaBrush)FindResource("WindowBrush");
        ApplyNativeWindowTheme(light);
        if (_trayMenu is not null) CopyThemeResources(_trayMenu.Resources);
    }

    private bool IsLightThemeActive() =>
        _settings.FollowSystemTheme ? SystemThemeService.IsLightTheme() : _settings.LightTheme;

    private void SetBrush(string key, string color)
    {
        var value = (MediaColor)ColorConverter.ConvertFromString(color);
        if (Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = value;
            return;
        }

        Resources[key] = new SolidColorBrush(value);
    }

    private async void CheckUpdate_OnClick(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(manual: true);

    private async Task RunStartupUpdateMaintenanceAsync()
    {
        try
        {
            var directory = UpdateStorage.Resolve(_settings.UpdateDirectory);
            _ = await Task.Run(() =>
            {
                if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                    UpdateStorage.CleanupRollback(Environment.ProcessPath);
                return UpdateStorage.CleanupExpired(
                    directory, DateTimeOffset.UtcNow, UpdateConfiguration.CacheLifetime);
            });
        }
        catch (Exception exception)
        {
            _diagnosticLog.Warning("Unable to clean expired update files.", exception);
        }

        if (!UpdateCheckPolicy.IsDue(_settings.UpdateCheckFrequency,
                _settings.LastAutomaticUpdateCheckUtc, DateTimeOffset.UtcNow)) return;
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (!_exitRequested) await CheckForUpdatesAsync(manual: false);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckInProgress || (manual && _state.IsBusy)) return;
        if (!Uri.TryCreate(_settings.UpdateFeedUrl, UriKind.Absolute, out var feedUri))
        {
            if (manual) ShowUpdateStatusDialog(T("UpdateCheckFailed"), T("UpdateUnavailable"));
            return;
        }

        _updateCheckInProgress = true;
        if (manual) SetBusyState(true);
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var result = await new UpdateFeedClient(httpClient).CheckAsync(feedUri, AppVersion.Current);
            _ = TryUpdateSettings(settings => settings.LastAutomaticUpdateCheckUtc = DateTimeOffset.UtcNow);
            if (!result.IsAvailable || result.Asset is null)
            {
                _availableUpdate = null;
                RefreshOverviewAttention();
                if (manual) ShowUpdateStatusDialog(T("UpdateLatestTitle"),
                    TF("UpdateLatestMessageFormat", AppVersion.Current));
                return;
            }

            _availableUpdate = result.Asset;
            RefreshOverviewAttention();
            if (!manual && string.Equals(_settings.SuppressedUpdateVersion,
                    result.Asset.Version.ToString(), StringComparison.OrdinalIgnoreCase)) return;
            if (!manual && (_automaticUpdatePromptShown || _startHidden || !IsVisible)) return;
            if (!manual) _automaticUpdatePromptShown = true;
            await HandleAvailableUpdateAsync(result.Asset);
        }
        catch (Exception exception)
        {
            _diagnosticLog.Warning("Update check failed.", exception);
            if (manual) ShowUpdateStatusDialog(T("UpdateCheckFailed"), exception.Message);
        }
        finally
        {
            _updateCheckInProgress = false;
            if (manual && !_exitRequested) SetBusyState(false);
        }
    }

    private async Task HandleAvailableUpdateAsync(UpdateAsset asset)
    {
        var choice = ShowUpdateDialog(asset);
        if (choice.SuppressVersion)
        {
            _ = TryUpdateSettings(settings => settings.SuppressedUpdateVersion = asset.Version.ToString());
            _availableUpdate = null;
            RefreshOverviewAttention();
        }
        if (choice.Action == UpdateDialogAction.GitHub)
        {
            OpenGitHubRepository();
            return;
        }
        if (choice.Action != UpdateDialogAction.Install)
        {
            _updateShimmerDismissedVersion = asset.Version.ToString();
            RefreshOverviewAttention();
            return;
        }

        SetBusyState(true);
        using var downloadCancellation = new CancellationTokenSource();
        var progressDialog = ShowUpdateDownloadProgressDialog(downloadCancellation);
        try
        {
            UpdateLauncher.EnsureSupportedCurrentDistribution();
            var directory = UpdateStorage.Resolve(_settings.UpdateDirectory);
            using var downloadClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var package = await new UpdatePackageDownloader(downloadClient)
                .DownloadAsync(asset, directory, downloadCancellation.Token);
            UpdateLauncher.LaunchReplacement(package);
            ExitApplication();
        }
        catch (OperationCanceledException) when (downloadCancellation.IsCancellationRequested)
        {
            // User cancellation is an expected outcome.
        }
        catch (Exception exception)
        {
            _diagnosticLog.Error("Update failed.", exception);
            ShowUpdateStatusDialog(T("UpdateCheckFailed"), exception.Message);
        }
        finally
        {
            progressDialog.Close();
            if (!_exitRequested) SetBusyState(false);
        }
    }

    private Window ShowUpdateDownloadProgressDialog(CancellationTokenSource cancellation)
    {
        var dialog = CreateUpdateDialogWindow(T("UpdateDialogTitle"));
        var panel = (StackPanel)dialog.Content;
        panel.Children.Add(new TextBlock
        {
            Text = T("UpdateDownloading"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15
        });
        var buttons = CreateUpdateDialogButtons();
        AddUpdateDialogButton(buttons, T("Cancel"), false, () => cancellation.Cancel());
        panel.Children.Add(buttons);
        dialog.Closing += (_, _) =>
        {
            if (!cancellation.IsCancellationRequested && !_exitRequested) cancellation.Cancel();
        };
        ApplyDialogTheme(dialog);
        dialog.Show();
        return dialog;
    }

    private UpdateDialogChoice ShowUpdateDialog(UpdateAsset asset)
    {
        var action = UpdateDialogAction.Later;
        var dialog = CreateUpdateDialogWindow(T("UpdateDialogTitle"));
        var panel = (StackPanel)dialog.Content;
        panel.Children.Add(new TextBlock { Text = TF("UpdateDialogMessageFormat", asset.Version, AppVersion.Current), TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        var suppress = new System.Windows.Controls.CheckBox { Content = TF("UpdateSuppressVersionFormat", asset.Version), Margin = new Thickness(0, 18, 0, 0), Foreground = (MediaBrush)FindResource("TextBrush") };
        panel.Children.Add(suppress);
        var buttons = CreateUpdateDialogButtons();
        AddUpdateDialogButton(buttons, T("DialogDownloadInstall"), true, () => { action = UpdateDialogAction.Install; dialog.Close(); });
        AddUpdateDialogButton(buttons, T("UpdateLater"), false, () => dialog.Close());
        AddUpdateDialogButton(buttons, T("OpenGitHub"), false, () => { action = UpdateDialogAction.GitHub; dialog.Close(); });
        panel.Children.Add(buttons);
        ApplyDialogTheme(dialog);
        _ = dialog.ShowDialog();
        return new UpdateDialogChoice(action, suppress.IsChecked == true && action != UpdateDialogAction.Install);
    }

    private void ShowUpdateStatusDialog(string title, string message)
    {
        var dialog = CreateUpdateDialogWindow(title);
        var panel = (StackPanel)dialog.Content;
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        var buttons = CreateUpdateDialogButtons();
        AddUpdateDialogButton(buttons, T("DialogOk"), true, () => dialog.Close());
        AddUpdateDialogButton(buttons, T("OpenGitHub"), false, () => { OpenGitHubRepository(); dialog.Close(); });
        panel.Children.Add(buttons);
        ApplyDialogTheme(dialog);
        _ = dialog.ShowDialog();
    }

    private Window CreateUpdateDialogWindow(string title) => new()
    {
        Owner = this, Title = title, Width = 520, SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false, Background = (MediaBrush)FindResource("WindowBrush"),
        Foreground = (MediaBrush)FindResource("TextBrush"),
        Content = new StackPanel { Margin = new Thickness(24, 22, 24, 20) }
    };

    private static StackPanel CreateUpdateDialogButtons() => new()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        Margin = new Thickness(0, 22, 0, 0)
    };

    private void AddUpdateDialogButton(StackPanel panel, string text, bool primary, Action action)
    {
        var button = new Button { Content = text, MinWidth = 100,
            Margin = panel.Children.Count == 0 ? new Thickness(0) : new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource(primary ? "PrimaryButtonStyle" : "ButtonStyle") };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private static void OpenGitHubRepository() =>
        _ = Process.Start(new ProcessStartInfo(UpdateConfiguration.RepositoryUrl) { UseShellExecute = true });

    private enum UpdateDialogAction { Later, Install, GitHub }
    private sealed record UpdateDialogChoice(UpdateDialogAction Action, bool SuppressVersion);

    private ProtectionRules CurrentProtectionRules() => _settings.CreateProtectionRules();

    private (
        IReadOnlySet<string> BlockedComponents,
        IReadOnlySet<string> PendingComponents,
        IReadOnlySet<string> StableComponents) CurrentLearningFilters(DateTimeOffset now)
    {
        var optimizationSettings = _settings.ResolveOptimizationSettings(manual: false);
        var protection = CurrentProtectionRules();
        var stableSettings = _settings.IntelligentCandidateSelection
            ? _settings.ResolveStableStateSuppressionSettings()
            : null;
        var stableComponents = stableSettings is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : StableStateSuppressionPolicy.SuppressedComponentKeys(
                _families,
                _applicationBackoffTracker.FamilyStableLearningRecords,
                optimizationSettings,
                protection,
                stableSettings,
                now,
                _applicationBackoffTracker.StableCandidateStatuses,
                _settings.StableAnchorSettings);
        var pendingComponents = _applicationBackoffTracker.PendingObservationComponentKeys(now);
        if (stableSettings is not null)
        {
            pendingComponents = pendingComponents
                .Concat(_applicationBackoffTracker.NaturalStableObservationComponentKeys())
                .Concat(_applicationBackoffTracker.NaturalStableGrowthReviewComponentKeys())
                .Concat(_applicationBackoffTracker.NaturalStableProvisionalValidationComponentKeys())
                .Concat(_applicationBackoffTracker.NaturalStableRecoveryEligibleComponentKeys(now))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return (
            _applicationBackoffTracker.BlockedComponentKeys(now),
            pendingComponents,
            stableComponents);
    }

    private void SynchronizeApplicationRuleStates(
        IReadOnlyList<ApplicationOptimizationRule> rules)
    {
        var activeIds = rules.Select(rule => rule.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ruleId in _applicationRuleStates.Keys
                     .Where(ruleId => !activeIds.Contains(ruleId))
                     .ToArray())
        {
            _applicationRuleStates.Remove(ruleId);
            _applicationRuleRuntime.ResetExecutionForRule(ruleId);
        }
    }

    private void RestoreApplicationRuleDisplayStates(IReadOnlyList<ApplicationOptimizationRule> rules)
    {
        foreach (var rule in rules)
        {
            var targetStates = (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
                .Select(target => _applicationRuleRuntime.GetTargetState(rule, target))
                .ToArray();
            var lastStartedAt = targetStates
                .Where(state => state.LastExecutionStartedAt.HasValue)
                .Select(state => state.LastExecutionStartedAt)
                .Max();
            var lastExecutionAt = targetStates
                .SelectMany(state => state.ProcessStates.Values
                    .Where(process => process.LastSuccessfulTrimAt.HasValue)
                    .Select(process => process.LastSuccessfulTrimAt)
                    .Append(state.LastDelayExecutionAt))
                .Where(value => value.HasValue)
                .Max();
            if (!lastStartedAt.HasValue && !lastExecutionAt.HasValue) continue;

            var latestStates = targetStates
                .Where(state => state.LastExecutionStartedAt == lastStartedAt)
                .ToArray();
            _applicationRuleStates[rule.Id] = new ApplicationOptimizationRuleExecutionState
            {
                ExecutionsCompleted = targetStates.Sum(state => Math.Max(0, state.DelayExecutionsCompleted)),
                LastExecutionAt = lastExecutionAt,
                LastExecutionStartedAt = lastStartedAt,
                LastReleasedBytes = latestStates.Sum(state => Math.Max(0, state.LastReleasedBytes)),
                LastRetainedBytes = latestStates.Any(state => state.LastRetainedBytes.HasValue)
                    ? latestStates.Sum(state => Math.Max(0, state.LastRetainedBytes ?? 0))
                    : null,
                LastSkippedReason = latestStates.Select(state => state.LastSkippedReason)
                    .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
            };
        }
    }

    private ApplicationOptimizationRule? FindDueApplicationRule(
        IReadOnlyList<ApplicationOptimizationRule> rules,
        DateTimeOffset now)
    {
        foreach (var rule in rules.Where(rule => rule.Enabled))
        {
            if (_applicationRuleRuntime
                .GetTargetDecisions(rule, _families, ApplicationRuleMuseRamStartedAt(), now)
                .Any(decision => decision.IsDue)) return rule;
        }
        return null;
    }

    private OptimizationPlan CreateApplicationRulePlan(
        ApplicationOptimizationRule rule,
        OptimizationSettings settings,
        DateTimeOffset now)
    {
        var matches = ApplicationOptimizationRulePolicy.ResolveMatches(rule, _families);
        _pendingApplicationRuleDecisions = _applicationRuleRuntime
            .GetTargetDecisions(rule, _families, ApplicationRuleMuseRamStartedAt(), now);
        var delayDueTargets = _pendingApplicationRuleDecisions
            .Where(decision => decision.DelayDue)
            .Select(decision => decision.TargetIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workingSetDueTargets = _pendingApplicationRuleDecisions
            .Where(decision => decision.WorkingSetDueProcesses.Count > 0)
            .Select(decision => decision.TargetIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var delayDue = delayDueTargets.Count > 0;
        var learningFilters = CurrentLearningFilters(now);
        var coolingComponents = learningFilters.BlockedComponents
            .Concat(learningFilters.PendingComponents)
            .Concat(learningFilters.StableComponents)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = ApplicationOptimizationRulePolicy.CreateCandidates(
                rule,
                _families,
                settings,
                CurrentProtectionRules(),
                _applicationRuleRuntime,
                now,
                delayDue,
                coolingComponentKeys: coolingComponents,
                delayDueTargetIdentities: delayDueTargets,
                workingSetDueTargetIdentities: workingSetDueTargets)
            .OrderByDescending(candidate => candidate.PotentialReleaseBytes)
            .ThenBy(candidate => candidate.Family.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = candidates;
        var evaluations = matches
            .GroupBy(match => match.Family.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var candidate = selected.FirstOrDefault(item =>
                    string.Equals(item.Family.Key, group.Key, StringComparison.OrdinalIgnoreCase));
                var targetCount = candidate?.TargetProcesses.Count ?? 0;
                var exclusionExplanation = targetCount > 0
                    ? null
                    : ExplainApplicationRuleExclusionReasons(
                        rule,
                        group.ToArray(),
                        _pendingApplicationRuleDecisions,
                        settings,
                        CurrentProtectionRules(),
                        _applicationRuleRuntime,
                        now);
                return new CandidateEvaluation(
                    group.Key,
                    group.First().Family.DisplayName,
                    targetCount > 0,
                    group.First().Family.Processes.Count,
                    targetCount,
                    targetCount > 0
                        ? Array.Empty<CandidateExclusionReason>()
                        : exclusionExplanation!.Reasons)
                {
                    LegacyIdleScore = group.First().Family.IdleScore,
                    IdleConfidenceScore = group.First().Family.IdleConfidenceScore,
                    TargetWorkingSetBytes = candidate?.PotentialReleaseBytes ?? 0,
                    TotalWorkingSetBytes = group.First().Family.WorkingSetBytes,
                    TargetProcessIds = candidate?.TargetProcesses.Select(process => process.ProcessId).ToArray() ??
                                       Array.Empty<int>(),
                    ProcessExclusionReasons = exclusionExplanation?.ProcessReasons ??
                        new Dictionary<string, IReadOnlyList<CandidateExclusionReason>>(
                            StringComparer.OrdinalIgnoreCase)
                };
            })
            .ToArray();
        return new OptimizationPlan(
            selected.Length > 0,
            selected.Length > 0
                ? $"应用优化规则找到 {selected.Length} 个目标。"
                : "应用优化规则当前没有可处理目标。",
            selected,
            selected.Length > 0
                ? OptimizationPlanOutcome.CandidatesFound
                : OptimizationPlanOutcome.NoCandidates)
        {
            CandidateEvaluations = evaluations
        };
    }

    private void RecordApplicationRuleExecution(
        ApplicationOptimizationRule rule,
        OptimizationPlan plan,
        IReadOnlyCollection<ProcessSnapshot> successfulProcesses,
        long workingSetReduction,
        DateTimeOffset runStartedAt,
        IReadOnlyDictionary<string, long> releasedBytesByProcessIdentity)
    {
        if (successfulProcesses.Count == 0)
        {
            RecordApplicationRuleSkip(rule, T("ApplicationRuleSkipNoSuccessfulTrim"));
            return;
        }
        var completedAt = DateTimeOffset.UtcNow;
        _applicationRuleRuntime.RecordSuccessfulExecution(
            rule,
            _pendingApplicationRuleDecisions,
            successfulProcesses,
            runStartedAt,
            completedAt,
            workingSetReduction,
            releasedBytesByProcessIdentity: releasedBytesByProcessIdentity);
        var successfulIdentities = successfulProcesses
            .Select(ApplicationOptimizationRuleRuntime.ProcessIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var staleKey in _applicationRuleOutcomeAttributions.Keys
                     .Where(key => runStartedAt - key.StartedAt > TimeSpan.FromMinutes(10))
                     .ToArray())
            _applicationRuleOutcomeAttributions.Remove(staleKey);
        foreach (var decision in _pendingApplicationRuleDecisions)
        {
            foreach (var match in decision.Matches)
            {
                foreach (var process in match.Processes.Where(process =>
                             successfulIdentities.Contains(ApplicationOptimizationRuleRuntime.ProcessIdentity(process))))
                {
                    var key = new ApplicationRuleOutcomeAttributionKey(
                        runStartedAt,
                        ApplicationComponentIdentity.ForProcess(match.Family.Key, process));
                    if (!_applicationRuleOutcomeAttributions.TryGetValue(key, out var targets))
                    {
                        targets = new HashSet<ApplicationRuleTargetReference>();
                        _applicationRuleOutcomeAttributions[key] = targets;
                    }
                    targets.Add(new ApplicationRuleTargetReference(rule.Id, decision.TargetIdentity));
                }
            }
        }
        var state = _applicationRuleStates.GetValueOrDefault(rule.Id) ??
                    new ApplicationOptimizationRuleExecutionState();
        state.ExecutionsCompleted += _pendingApplicationRuleDecisions.Count(decision =>
            decision.IsDue && decision.Matches.SelectMany(match => match.Processes)
                .Any(process => successfulIdentities.Contains(
                    $"{process.ProcessId}|{process.StartTimeFileTimeUtc}")) &&
            decision.DelayDue);
        state.LastExecutionAt = completedAt;
        state.LastExecutionStartedAt = runStartedAt;
        state.LastReleasedBytes = Math.Max(0, workingSetReduction);
        state.LastRetainedBytes = null;
        state.LastSkippedReason = null;
        _applicationRuleStates[rule.Id] = state;
    }

    private void RecordApplicationRuleSkip(
        ApplicationOptimizationRule rule,
        string reason)
    {
        var state = _applicationRuleStates.GetValueOrDefault(rule.Id) ??
                    new ApplicationOptimizationRuleExecutionState();
        state.LastSkippedReason = string.IsNullOrWhiteSpace(reason)
            ? T("ApplicationRuleSkipNoCandidates")
            : reason;
        _applicationRuleStates[rule.Id] = state;
    }

    private string FormatApplicationRuleSkipReason(OptimizationPlan plan)
    {
        var reasons = plan.CandidateEvaluations
            .SelectMany(evaluation => evaluation.ExclusionReasons)
            .ToHashSet();
        if (reasons.Contains(CandidateExclusionReason.ApplicationRuleInvalidProcessIdentity))
            return T("ApplicationRuleSkipInvalidIdentity");
        if (reasons.Contains(CandidateExclusionReason.ApplicationRuleSystemProcess) ||
            reasons.Contains(CandidateExclusionReason.MuseRamProcess))
            return T("ApplicationRuleSkipSystem");
        if (reasons.Contains(CandidateExclusionReason.Protected))
            return T("ApplicationRuleSkipProtected");
        if (reasons.Contains(CandidateExclusionReason.UnreliableActivitySample) ||
            reasons.Contains(CandidateExclusionReason.IdleConfirmationPending))
            return T("ApplicationRuleSkipSampling");
        if (reasons.Contains(CandidateExclusionReason.CurrentCpuActivity) ||
            reasons.Contains(CandidateExclusionReason.CurrentIoActivity) ||
            reasons.Contains(CandidateExclusionReason.ActiveProcessRelationship))
            return T("ApplicationRuleSkipActivity");
        if (reasons.Contains(CandidateExclusionReason.ApplicationRuleForegroundBlocked))
            return T("ApplicationRuleSkipForeground");
        if (reasons.Contains(CandidateExclusionReason.ApplicationRuleWorkingSetCooldown))
            return T("ApplicationRuleSkipCooldown");
        if (reasons.Contains(CandidateExclusionReason.ApplicationRuleWorkingSetObservationPending))
            return T("ApplicationRuleSkipWorkingSet");
        if (reasons.Contains(CandidateExclusionReason.ApplicationRuleDelayNotDue))
            return T("ApplicationRuleSkipDelay");
        if (reasons.Contains(CandidateExclusionReason.ApplicationRuleZeroWorkingSet))
            return T("ApplicationRuleSkipZeroWorkingSet");
        if (reasons.Contains(CandidateExclusionReason.BelowProcessWorkingSet))
            return T("ApplicationRuleSkipProcessWorkingSet");
        if (reasons.Contains(CandidateExclusionReason.BelowFamilyWorkingSet))
            return T("ApplicationRuleSkipNoCandidates");
        return T("ApplicationRuleSkipSafety");
    }

    private sealed record ApplicationRuleExclusionExplanation(
        IReadOnlyList<CandidateExclusionReason> Reasons,
        IReadOnlyDictionary<string, IReadOnlyList<CandidateExclusionReason>> ProcessReasons);

    private static ApplicationRuleExclusionExplanation ExplainApplicationRuleExclusionReasons(
        ApplicationOptimizationRule rule,
        IReadOnlyList<ApplicationOptimizationRuleTargetMatch> matches,
        IReadOnlyList<ApplicationOptimizationRuleTargetDecision> decisions,
        OptimizationSettings settings,
        ProtectionRules protection,
        ApplicationOptimizationRuleRuntime runtime,
        DateTimeOffset now)
    {
        var reasons = new HashSet<CandidateExclusionReason>();
        var processes = matches.SelectMany(match => match.Processes).ToArray();
        if (processes.Length == 0)
        {
            return new ApplicationRuleExclusionExplanation(
                new[] { CandidateExclusionReason.ApplicationRuleInvalidProcessIdentity },
                new Dictionary<string, IReadOnlyList<CandidateExclusionReason>>(StringComparer.OrdinalIgnoreCase));
        }
        var allProcesses = matches
            .SelectMany(match => match.Family.Processes)
            .Concat(processes)
            .GroupBy(process => $"{process.ProcessId}|{process.StartTimeFileTimeUtc}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var context = protection.CreateContext(allProcesses);
        var activeProcessIds = allProcesses
            .Where(process => process.IsForeground ||
                              process.CpuPercent >= settings.ActiveCpuThresholdPercent ||
                              process.IoBytesPerSecond >= settings.ActiveIoThresholdBytesPerSecond)
            .Select(process => process.ProcessId)
            .ToHashSet();
        var processReasonsByIdentity = new Dictionary<string, IReadOnlyList<CandidateExclusionReason>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var match in matches)
        {
            var targetIdentity = ApplicationOptimizationRulePolicy.TargetIdentity(match.Target);
            var decision = decisions.FirstOrDefault(candidate =>
                string.Equals(candidate.TargetIdentity, targetIdentity, StringComparison.OrdinalIgnoreCase));
            var unprotected = match.Target.BypassProtectionConfirmed == true
                ? match.Processes
                : protection.FilterUnprotectedProcesses(
                    new ProcessFamilySnapshot(
                        match.Family.Key,
                        match.Family.DisplayName,
                        match.Family.ExecutableDirectory,
                        match.Processes),
                    context)?.Processes ?? Array.Empty<ProcessSnapshot>();
            var unprotectedIdentities = unprotected
                .Select(process => $"{process.ProcessId}|{process.StartTimeFileTimeUtc}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var processableBytes = 0L;
            foreach (var process in match.Processes)
            {
                var processIdentity = $"{process.ProcessId}|{process.StartTimeFileTimeUtc}";
                var processReasons = new HashSet<CandidateExclusionReason>();
                void AddReason(CandidateExclusionReason reason)
                {
                    reasons.Add(reason);
                    processReasons.Add(reason);
                }
                var blocked = false;
                if (process.StartTimeFileTimeUtc is not > 0)
                {
                    AddReason(CandidateExclusionReason.ApplicationRuleInvalidProcessIdentity);
                    blocked = true;
                }
                if (process.WorkingSetBytes <= 0)
                {
                    AddReason(CandidateExclusionReason.ApplicationRuleZeroWorkingSet);
                    blocked = true;
                }
                if (process.ProcessId == Environment.ProcessId)
                {
                    AddReason(CandidateExclusionReason.MuseRamProcess);
                    blocked = true;
                }
                if (SystemProcessPolicy.IsAlwaysExcluded(process.Name, process.ExecutablePath))
                {
                    AddReason(CandidateExclusionReason.ApplicationRuleSystemProcess);
                    blocked = true;
                }
                if (!process.HasReliableActivitySample)
                {
                    AddReason(CandidateExclusionReason.UnreliableActivitySample);
                    blocked = true;
                }
                if (process.CpuPercent >= settings.ActiveCpuThresholdPercent)
                {
                    AddReason(CandidateExclusionReason.CurrentCpuActivity);
                    blocked = true;
                }
                if (process.IoBytesPerSecond >= settings.ActiveIoThresholdBytesPerSecond)
                {
                    AddReason(CandidateExclusionReason.CurrentIoActivity);
                    blocked = true;
                }
                if (settings.EnhancedSafety && process.IsForeground)
                {
                    AddReason(CandidateExclusionReason.ApplicationRuleForegroundBlocked);
                    blocked = true;
                }
                var relatedActive = process.ParentProcessId is int parentId &&
                                    activeProcessIds.Contains(parentId) &&
                                    parentId != process.ProcessId;
                relatedActive |= allProcesses.Any(other =>
                    other.ParentProcessId == process.ProcessId &&
                    other.ProcessId != process.ProcessId &&
                    activeProcessIds.Contains(other.ProcessId));
                if (relatedActive)
                {
                    AddReason(CandidateExclusionReason.ActiveProcessRelationship);
                    blocked = true;
                }
                if (!unprotectedIdentities.Contains(processIdentity))
                {
                    AddReason(CandidateExclusionReason.Protected);
                    blocked = true;
                }

                var delayDue = decision?.DelayDue == true;
                if (!delayDue)
                {
                    if (rule.WorkingSetTriggerEnabled)
                    {
                        if (runtime.IsWorkingSetCooling(rule, match.Target, process, now, rule.CooldownMinutes))
                        {
                            AddReason(CandidateExclusionReason.ApplicationRuleWorkingSetCooldown);
                            blocked = true;
                        }
                        else if (!runtime.IsWorkingSetReady(rule, match.Target, process))
                        {
                            AddReason(CandidateExclusionReason.ApplicationRuleWorkingSetObservationPending);
                            blocked = true;
                        }
                    }
                    else
                    {
                        AddReason(CandidateExclusionReason.ApplicationRuleDelayNotDue);
                        blocked = true;
                    }
                }

                if (!blocked) processableBytes += Math.Max(0, process.WorkingSetBytes);
                if (processReasons.Count > 0)
                    processReasonsByIdentity[processIdentity] = processReasons.OrderBy(reason => reason).ToArray();
            }

        }
        var aggregateReasons = reasons.Count == 0
            ? new[] { CandidateExclusionReason.ApplicationRuleDelayNotDue }
            : reasons.OrderBy(reason => reason).ToArray();
        return new ApplicationRuleExclusionExplanation(aggregateReasons, processReasonsByIdentity);
    }

    private void ApplyCompletedApplicationRuleOutcome(ApplicationReboundOutcome outcome)
    {
        if (outcome.RunContext?.Trigger != OptimizationTriggerKind.ApplicationRule)
            return;
        foreach (var state in _applicationRuleStates.Values)
        {
            if (state.LastExecutionStartedAt != outcome.StartedAt) continue;
            state.LastRetainedBytes = (state.LastRetainedBytes ?? 0) +
                                      Math.Max(0, outcome.RetainedBytes);
        }
        if (!string.IsNullOrWhiteSpace(outcome.ComponentKey) &&
            _applicationRuleOutcomeAttributions.TryGetValue(
                new ApplicationRuleOutcomeAttributionKey(outcome.StartedAt, outcome.ComponentKey),
                out var targets))
        {
            foreach (var target in targets)
                _applicationRuleRuntime.RecordRetainedOutcome(
                    target.RuleId,
                    target.TargetIdentity,
                    outcome.StartedAt,
                    outcome.RetainedBytes);
        }
    }

    private void RefreshProtectedList()
    {
        if (_openApplicationRulePopups.Count > 0) return;
        var expandedKeys = _state.ProtectedApplications
            .Where(group => group.IsExpanded)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expandedExecutableKeys = _state.ProtectedApplications
            .SelectMany(group => group.Executables)
            .Where(executable => executable.IsExpanded)
            .Select(executable => executable.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rules = ApplicationProtectionSettings.Resolve(_settings);
        var minimumProcessWorkingSetBytes = _settings
            .ResolveOptimizationSettings(manual: false)
            .MinimumProcessWorkingSetBytes;
        var runningCandidates = RunningProtectionCandidateCatalog.Create(_families, rules)
            .Where(candidate => candidate.ProtectionState != ApplicationProtectionState.None)
            .ToArray();
        var consumedRulePaths = runningCandidates
            .SelectMany(candidate => candidate.MatchedRuleApplicationPaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ProtectedApplicationGroup>();
        foreach (var candidate in runningCandidates)
        {
            var applicationPath = NormalizeExecutablePath(candidate.ApplicationExecutablePath);
            var key = "protected:" + applicationPath;
            var executables = candidate.Executables
                .Where(executable =>
                    candidate.ProtectionState == ApplicationProtectionState.EntireFamily || executable.IsProtected)
                .Select(executable => new ProtectedExecutableEntry(
                    candidate.FamilyKey,
                    executable.Name,
                    executable.ExecutablePath,
                    executable.InstanceCount,
                    executable.WorkingSetBytes,
                    executable.Processes
                        .Select(process => new ProtectedProcessEntry(
                            process.ProcessId,
                            process.WorkingSetBytes,
                            process.WorkingSetBytes < minimumProcessWorkingSetBytes
                                ? T("BelowProcessThreshold")
                                : null))
                        .OrderByDescending(process => process.WorkingSetBytes)
                        .ThenBy(process => process.ProcessId)
                        .ToArray(),
                    expandedExecutableKeys.Contains(executable.ExecutablePath)))
                .OrderBy(executable => executable.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(executable => executable.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var applicationRuleDisplay = CreateApplicationRuleDisplay(
                applicationPath,
                candidate.MatchedRuleApplicationPaths,
                executables);
            groups.Add(new ProtectedApplicationGroup(
                key,
                candidate.FamilyKey,
                candidate.DisplayName,
                applicationPath,
                candidate.ProtectionState,
                candidate.MatchedRuleApplicationPaths,
                executables,
                candidate.ProcessCount,
                candidate.WorkingSetBytes,
                expandedKeys.Contains(key),
                applicationRuleDisplay.HasRule,
                applicationRuleDisplay.Status,
                applicationRuleDisplay.History,
                applicationRuleDisplay.Skip,
                applicationRuleDisplay.Detail));
        }

        foreach (var rule in rules.Where(rule =>
                     !consumedRulePaths.Contains(NormalizeExecutablePath(rule.ApplicationExecutablePath))))
        {
            var applicationPath = NormalizeExecutablePath(rule.ApplicationExecutablePath);
            var key = "protected:" + applicationPath;
            var executables = rule.ProtectEntireFamily
                ? Array.Empty<ProtectedExecutableEntry>()
                : (rule.ProtectedExecutablePaths ?? new List<string>())
                    .Select(NormalizeExecutablePath)
                    .Where(path => !string.Equals(path, applicationPath, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new ProtectedExecutableEntry(
                        string.Empty,
                        IOPath.GetFileNameWithoutExtension(path),
                        path,
                        0,
                        0,
                        Array.Empty<ProtectedProcessEntry>(),
                        false))
                    .ToArray();
            var applicationRuleDisplay = CreateApplicationRuleDisplay(
                applicationPath,
                new[] { applicationPath },
                executables);
            groups.Add(new ProtectedApplicationGroup(
                key,
                string.Empty,
                IOPath.GetFileNameWithoutExtension(applicationPath),
                applicationPath,
                rule.ProtectEntireFamily
                    ? ApplicationProtectionState.EntireFamily
                    : ApplicationProtectionState.Partial,
                new[] { applicationPath },
                executables,
                0,
                0,
                expandedKeys.Contains(key),
                applicationRuleDisplay.HasRule,
                applicationRuleDisplay.Status,
                applicationRuleDisplay.History,
                applicationRuleDisplay.Skip,
                applicationRuleDisplay.Detail));
        }

        var orderedGroups = groups
            .OrderByDescending(group => group.ProtectionState)
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ProtectedGroupsEqual(_state.ProtectedApplications, orderedGroups)) return;

        var verticalOffset = ProtectedGroupsScrollViewer.VerticalOffset;
        ReplaceCollection(_state.ProtectedApplications, orderedGroups);
        _ = Dispatcher.BeginInvoke(() =>
            ProtectedGroupsScrollViewer.ScrollToVerticalOffset(
                Math.Min(verticalOffset, ProtectedGroupsScrollViewer.ScrollableHeight)),
            DispatcherPriority.Loaded);
    }

    private ApplicationRuleDisplay CreateApplicationRuleDisplay(
        string applicationPath,
        IReadOnlyList<string> applicationRulePaths,
        IReadOnlyList<ProtectedExecutableEntry> executables)
    {
        var targetPaths = applicationRulePaths
            .Concat(new[] { applicationPath })
            .Concat(executables.Select(executable => executable.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeExecutablePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rules = ApplicationOptimizationRuleSettings.Resolve(_settings)
            .Where(rule => (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
                .Any(target => ApplicationOptimizationRulePolicy.TargetPaths(target)
                    .Any(targetPaths.Contains)))
            .ToArray();
        if (rules.Length == 0)
        {
            return new ApplicationRuleDisplay(
                false,
                T("ApplicationRuleNotConfigured"),
                string.Empty,
                string.Empty,
                T("ApplicationRuleNotConfigured"));
        }

        var rule = rules[0];
        var state = _applicationRuleStates.GetValueOrDefault(rule.Id) ??
                    new ApplicationOptimizationRuleExecutionState();
        var matches = ApplicationOptimizationRulePolicy.ResolveMatches(rule, _families);
        var now = DateTimeOffset.UtcNow;
        var enabled = rule.Enabled ? T("ApplicationRuleEnabled") : T("ApplicationRuleDisabled");
        var targetSummary = string.Join(
            " + ",
            (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
                .Select(ApplicationRuleTargetTypeText)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        var nextCheck = FormatApplicationRuleNextCheck(rule, state, matches, now);
        var lastExecution = state.LastExecutionAt is { } lastExecutionAt
            ? lastExecutionAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : T("ApplicationRuleNever");
        var release = state.LastExecutionAt.HasValue
            ? DisplayFormat.Bytes(state.LastReleasedBytes)
            : "--";
        var retained = state.LastRetainedBytes is { } retainedBytes
            ? DisplayFormat.Bytes(retainedBytes)
            : state.LastExecutionAt.HasValue
                ? T("ApplicationRuleObservationPending")
                : "--";
        var skip = state.LastSkippedReason;
        if (string.IsNullOrWhiteSpace(skip) && rule.Enabled && matches.Count == 0)
            skip = T("ApplicationRuleSkipTargetNotRunning");
        skip ??= T("ApplicationRuleNoRecentSkip");
        var status = TF("ApplicationRuleStatusFormat", enabled, targetSummary, nextCheck);
        var history = TF("ApplicationRuleHistoryFormat", lastExecution, release, retained);
        var skipText = TF("ApplicationRuleSkipFormat", skip);
        var targetDetail = string.Join(
            Environment.NewLine,
            (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
                .Select(target => $"{ApplicationRuleTargetTypeText(target)}: {ApplicationRuleTargetDetail(target)}"));
        return new ApplicationRuleDisplay(
            true,
            status,
            history,
            skipText,
            string.Join(
                Environment.NewLine,
                status,
                history,
                skipText,
                targetDetail));
    }

    private string FormatApplicationRuleNextCheck(
        ApplicationOptimizationRule rule,
        ApplicationOptimizationRuleExecutionState state,
        IReadOnlyList<ApplicationOptimizationRuleTargetMatch> matches,
        DateTimeOffset now)
    {
        if (!rule.Enabled) return T("ApplicationRuleDisabled");
        if (matches.Count == 0) return T("ApplicationRuleNextTargetRunning");
        if (rule.TriggerMode == ApplicationOptimizationRuleTriggerMode.FollowAutomatic)
            return _settings.AutoOptimization
                ? T("ApplicationRuleNextAutomaticOptimization")
                : T("ApplicationRuleAutomaticOptimizationDisabled");

        var decisions = _applicationRuleRuntime.GetTargetDecisions(
            rule,
            _families,
            ApplicationRuleMuseRamStartedAt(),
            now);
        var delayExecutionLimit = rule.RepeatIndefinitely
            ? int.MaxValue
            : Math.Clamp(rule.ExecutionCount, 1, 10);
        var delayTargetsRemain = (rule.Targets ?? new List<ApplicationOptimizationRuleTarget>())
            .Any(target => _applicationRuleRuntime.GetTargetState(rule, target).DelayExecutionsCompleted < delayExecutionLimit);
        if (rule.DelayTriggerEnabled && !delayTargetsRemain)
            return T("ApplicationRuleCompleted");

        DateTimeOffset? nextDelayAt = null;
        foreach (var decision in decisions)
        {
            var targetState = _applicationRuleRuntime.GetTargetState(rule, decision.Target);
            if (!rule.DelayTriggerEnabled || targetState.DelayExecutionsCompleted >= delayExecutionLimit)
                continue;

            var anchor = rule.DelayAnchor == ApplicationOptimizationDelayAnchor.MuseRamStartup
                ? ApplicationRuleMuseRamStartedAt()
                : ApplicationRuleTargetStartedAt(
                    ApplicationOptimizationRulePolicy.ResolveLaunchProcesses(decision.Target, decision.Matches));
            if (anchor.HasValue)
            {
                var targetDueAt = anchor.Value.AddMinutes(Math.Clamp(rule.DelayMinutes, 1, 1440));
                if (targetState.LastDelayExecutionAt is { } lastDelayExecutionAt)
                    targetDueAt = new[]
                    {
                        targetDueAt,
                        lastDelayExecutionAt.AddMinutes(Math.Clamp(rule.ExecutionIntervalMinutes, 1, 1440))
                    }.Max();
                if (targetDueAt > now && (nextDelayAt is null || targetDueAt < nextDelayAt.Value))
                    nextDelayAt = targetDueAt;
            }
        }

        if (nextDelayAt is { } nextDueAt)
            return nextDueAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        if (rule.WorkingSetTriggerEnabled && decisions.Any(decision =>
                decision.DelayDue && !decision.WorkingSetThresholdSatisfied))
            return T("ApplicationRuleNextWorkingSet");
        return T("ApplicationRuleNextRefresh");
    }

    private static DateTimeOffset? ApplicationRuleTargetStartedAt(
        IEnumerable<ProcessSnapshot> processes)
    {
        var starts = processes
            .Where(process => process.StartTimeFileTimeUtc is > 0)
            .Select(process =>
            {
                try { return DateTimeOffset.FromFileTime(process.StartTimeFileTimeUtc!.Value); }
                catch { return (DateTimeOffset?)null; }
            })
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        return starts.Length == 0 ? null : starts[0];
    }

    private static bool ProtectedGroupsEqual(
        IReadOnlyList<ProtectedApplicationGroup> current,
        IReadOnlyList<ProtectedApplicationGroup> updated)
    {
        if (current.Count != updated.Count) return false;
        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = updated[index];
            if (!string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(left.FamilyKey, right.FamilyKey, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                !string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) ||
                left.ProtectionState != right.ProtectionState ||
                left.InstanceCount != right.InstanceCount ||
                left.WorkingSetBytes != right.WorkingSetBytes ||
                left.HasApplicationRule != right.HasApplicationRule ||
                !string.Equals(left.ApplicationRuleStatus, right.ApplicationRuleStatus, StringComparison.Ordinal) ||
                !string.Equals(left.ApplicationRuleHistory, right.ApplicationRuleHistory, StringComparison.Ordinal) ||
                !string.Equals(left.ApplicationRuleSkip, right.ApplicationRuleSkip, StringComparison.Ordinal) ||
                !string.Equals(left.ApplicationRuleDetail, right.ApplicationRuleDetail, StringComparison.Ordinal) ||
                !left.RuleApplicationPaths.SequenceEqual(
                    right.RuleApplicationPaths,
                    StringComparer.OrdinalIgnoreCase) ||
                left.Executables.Count != right.Executables.Count)
            {
                return false;
            }

            for (var executableIndex = 0; executableIndex < left.Executables.Count; executableIndex++)
            {
                var leftExecutable = left.Executables[executableIndex];
                var rightExecutable = right.Executables[executableIndex];
                if (!string.Equals(leftExecutable.Name, rightExecutable.Name, StringComparison.Ordinal) ||
                    !string.Equals(leftExecutable.FamilyKey, rightExecutable.FamilyKey, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(leftExecutable.Path, rightExecutable.Path, StringComparison.OrdinalIgnoreCase) ||
                    leftExecutable.InstanceCount != rightExecutable.InstanceCount ||
                    leftExecutable.WorkingSetBytes != rightExecutable.WorkingSetBytes ||
                    leftExecutable.Processes.Count != rightExecutable.Processes.Count)
                {
                    return false;
                }

                for (var processIndex = 0; processIndex < leftExecutable.Processes.Count; processIndex++)
                {
                    var leftProcess = leftExecutable.Processes[processIndex];
                    var rightProcess = rightExecutable.Processes[processIndex];
                    if (leftProcess.ProcessId != rightProcess.ProcessId ||
                        leftProcess.WorkingSetBytes != rightProcess.WorkingSetBytes)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static string NormalizeExecutablePath(string path)
    {
        try { return IOPath.GetFullPath(path.Trim()).TrimEnd(IOPath.DirectorySeparatorChar); }
        catch { return path.Trim(); }
    }

    private ProcessFamilySnapshot[] ResolveProtectedTargetFamilies(ProtectedOptimizationTarget target)
    {
        var executablePaths = target.ExecutablePaths
            .Select(NormalizeExecutablePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _families.Where(family =>
            string.Equals(family.Key, target.FamilyKey, StringComparison.OrdinalIgnoreCase) ||
            family.Processes.Any(process =>
                !string.IsNullOrWhiteSpace(process.ExecutablePath) &&
                executablePaths.Contains(NormalizeExecutablePath(process.ExecutablePath))))
            .ToArray();
    }

    private OptimizationRunContext CreateOptimizationRunContext(
        LocalSettings settings,
        bool manual,
        bool scheduled,
        bool longIdle = false,
        bool applicationRule = false,
        string? runId = null)
    {
        var customProfile = settings.ActiveCustomProfile;
        var baseProfile = customProfile?.BaseProfile ?? settings.Profile;
        var profileKey = customProfile is null
            ? $"builtin:{baseProfile}"
            : $"custom:{customProfile.Id}";
        var trigger = applicationRule
            ? OptimizationTriggerKind.ApplicationRule
            : longIdle
            ? OptimizationTriggerKind.LongIdle
            : scheduled
            ? OptimizationTriggerKind.Scheduled
            : manual
                ? OptimizationTriggerKind.Manual
                : OptimizationTriggerKind.Automatic;
        return new OptimizationRunContext(profileKey, baseProfile, trigger, CurrentAppVersion)
        {
            RunId = runId
        };
    }

    private void SetBusyState(bool isBusy)
    {
        _state.IsBusy = isBusy;
        OptimizeNowButton.IsEnabled = !isBusy;
        CompactOptimizeNowButton.IsEnabled = !isBusy;
        ProtectionSuggestionButton.IsEnabled = !isBusy &&
            (_availableUpdate is not null || _currentProtectionSuggestions.Count > 0);
        ReviewProtectionSuggestionsButton.IsEnabled =
            !isBusy && (_displayedProtectionSuggestions.Count > 0 || _currentProtectionSuggestions.Count > 0);
        SynchronizeProfileCopyButton(CustomProfileCatalogList.SelectedItem as ProfileCatalogItem);
        SynchronizeStableSuppressionCopyButton(
            StableSuppressionCatalogList.SelectedItem as StableSuppressionCatalogItem);
    }

    private void StartResponsivenessMonitoring()
    {
        if (_backgroundResponsivenessTask is { IsCompleted: false }) return;
        _responsivenessCancellation?.Dispose();
        _responsivenessCancellation = new CancellationTokenSource();
        _lastUiHeartbeatTimestamp = Stopwatch.GetTimestamp();
        _uiResponsivenessTimer.Start();
        _backgroundResponsivenessTask = MonitorBackgroundResponsivenessAsync(_responsivenessCancellation.Token);
    }

    private void StopResponsivenessMonitoring()
    {
        _uiResponsivenessTimer.Stop();
        _responsivenessCancellation?.Cancel();
        _backgroundResponsivenessTask = null;
    }

    private void UiResponsivenessTimer_OnTick(object? sender, EventArgs e)
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastUiHeartbeatTimestamp, nowTimestamp);
        _lastUiHeartbeatTimestamp = nowTimestamp;
        RecordResponsivenessStallIfNeeded("ui", elapsed, ref _lastUiStallRecordedAt);
    }

    private async Task MonitorBackgroundResponsivenessAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var previousTimestamp = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var nowTimestamp = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, nowTimestamp);
                previousTimestamp = nowTimestamp;
                RecordResponsivenessStallIfNeeded(
                    "background",
                    elapsed,
                    ref _lastBackgroundStallRecordedAt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RecordResponsivenessStallIfNeeded(
        string source,
        TimeSpan elapsed,
        ref DateTimeOffset lastRecordedAt)
    {
        if (!_settings.DiagnosticDataCollectionEnabled) return;
        if (elapsed < TimeSpan.FromMilliseconds(250)) return;
        var now = DateTimeOffset.UtcNow;
        if (now - lastRecordedAt < TimeSpan.FromSeconds(1)) return;
        lastRecordedAt = now;
        var metric = new ResponsivenessStallCalibrationMetric(
            now,
            CurrentBuildId,
            source,
            elapsed.TotalMilliseconds,
            _state.IsBusy,
            _activeOptimizationRunId);
        QueueCalibrationWrite(() => _calibrationMetricsStore.AppendResponsivenessStall(metric));
    }

    private void QueueCalibrationWrite(Action write)
    {
        if (!_settings.DiagnosticDataCollectionEnabled) return;
        if (Volatile.Read(ref _diagnosticClearInProgress) != 0) return;
        var generation = Volatile.Read(ref _calibrationWriteGeneration);
        lock (_calibrationWriteQueueGate)
        {
            _calibrationWriteQueue = _calibrationWriteQueue.ContinueWith(
                _ =>
                {
                    if (generation != Volatile.Read(ref _calibrationWriteGeneration)) return;
                    try { write(); }
                    catch (Exception exception)
                    {
                        _diagnosticLog.Warning("Unable to append queued calibration metrics.", exception);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private void FlushCalibrationWrites()
    {
        Task pending;
        lock (_calibrationWriteQueueGate) pending = _calibrationWriteQueue;
        try { _ = pending.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException)
        {
            // Individual writes already report their own failures.
        }
    }

    private void RecordCandidateCalibration(
        OptimizationPlan plan,
        OptimizationSettings settings,
        OptimizationRunContext runContext,
        MemorySnapshot memory)
    {
        if (!_settings.DiagnosticDataCollectionEnabled) return;
        var recordedAt = DateTimeOffset.UtcNow;
        var protection = CurrentProtectionRules();
        var learningFilters = CurrentLearningFilters(recordedAt);
        OptimizationPlan CreateReadOnlyPlan(OptimizationSettings shadowSettings)
        {
            var planningSettings = runContext.Trigger == OptimizationTriggerKind.LongIdle
                ? shadowSettings with { MaxApplications = 0 }
                : shadowSettings;
            var readOnlyPlan = _planner.CreatePlan(
                memory,
                _families,
                planningSettings,
                protection,
                _lastTrimTimes,
                recordedAt,
                manual: runContext.Trigger == OptimizationTriggerKind.Manual,
                _activity,
                automaticBackoffFamilies: null,
                outcomeMultipliers: _applicationBackoffTracker.OutcomeMultipliers,
                intelligentPreview: true,
                learningConfidences: _applicationBackoffTracker.LearningConfidences,
                candidateIdleReadiness: _candidateIdleReadiness,
                enforceUnattendedSafety: runContext.Trigger is
                    OptimizationTriggerKind.Scheduled or OptimizationTriggerKind.LongIdle,
                pendingReboundObservationFamilies: null,
                lastTrimProcessStartTimes: _lastTrimProcessStartTimes,
                automaticBackoffComponents: learningFilters.BlockedComponents,
                pendingReboundObservationComponents: learningFilters.PendingComponents,
                stableSuppressedComponents: learningFilters.StableComponents);
            if (runContext.Trigger != OptimizationTriggerKind.LongIdle) return readOnlyPlan;
            var minimumIdle = TimeSpan.FromMinutes(
                LongIdleOptimizationPolicy.NormalizeMinutes(_settings.LongIdleOptimizationMinutes));
            return CandidatePlanCalibrationPolicy.ApplyLongIdleFilter(
                readOnlyPlan,
                _activity,
                minimumIdle,
                shadowSettings.MaxApplications);
        }
        var profileParameterOptions = runContext.Trigger == OptimizationTriggerKind.ApplicationRule
            ? ProfileParameterShadowPlanningOptions.Disabled
            : new ProfileParameterShadowPlanningOptions(CreateReadOnlyPlan);
        var metric = CandidatePlanCalibrationPolicy.Create(
            runContext,
            recordedAt,
            plan,
            settings,
            memory,
            _families,
            _families.Count,
            _activity,
            profileParameterShadows: profileParameterOptions);
        var signature = string.Join(
            '|',
            runContext.ProfileKey,
            plan.Outcome,
            metric.EffectiveTriggerAvailableBytes,
            metric.MaxApplications,
            metric.MinimumFamilyWorkingSetBytes,
            metric.MinimumProcessWorkingSetBytes,
            metric.LegacyIdleThreshold,
            metric.ActiveCpuThresholdPercent,
            metric.ActiveIoThresholdBytesPerSecond,
            metric.EvaluatedFamilyCount,
            metric.EligibleFamilyCount,
            string.Join(',', metric.ExclusionReasonCounts.Select(pair => $"{pair.Key}:{pair.Value}")));
        recordedAt = metric.RecordedAt;
        var minimumInterval = plan.Outcome == OptimizationPlanOutcome.LowMemoryPressure
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromSeconds(30);
        var throttleRepeatedPlan = runContext.Trigger is
            OptimizationTriggerKind.Automatic or OptimizationTriggerKind.LongIdle;
        if (throttleRepeatedPlan &&
            _lastCandidateCalibrations.TryGetValue(runContext.Trigger, out var previous) &&
            string.Equals(signature, previous.Signature, StringComparison.Ordinal) &&
            recordedAt - previous.RecordedAt < minimumInterval)
        {
            return;
        }

        _lastCandidateCalibrations[runContext.Trigger] = (recordedAt, signature);
        metric = CandidatePlanCalibrationPolicy.AttachActivityThresholdShadows(
            metric,
            runContext.Trigger == OptimizationTriggerKind.ApplicationRule
                ? Array.Empty<ActivityThresholdShadowMetric>()
                : CreateActivityThresholdShadowMetrics(plan, settings, CreateReadOnlyPlan));
        QueueCalibrationWrite(() => _calibrationMetricsStore.AppendCandidatePlan(metric));
    }

    private IReadOnlyList<ActivityThresholdShadowMetric> CreateActivityThresholdShadowMetrics(
        OptimizationPlan formalPlan,
        OptimizationSettings settings,
        Func<OptimizationSettings, OptimizationPlan> createReadOnlyPlan)
    {
        if (!_settings.DiagnosticDataCollectionEnabled || _activityThresholdShadowStates.Count == 0)
            return Array.Empty<ActivityThresholdShadowMetric>();

        var baselineState = _activityThresholdShadowStates.Single(state => state.Experiment.IsBaseline);
        var recomputedBaselinePlan = createReadOnlyPlan(settings with
        {
            ActiveCpuThresholdPercent = baselineState.Experiment.CpuThresholdPercent,
            ActiveIoThresholdBytesPerSecond = baselineState.Experiment.IoThresholdBytesPerSecond
        });
        var familyByKey = _families.ToDictionary(family => family.Key, StringComparer.OrdinalIgnoreCase);

        return _activityThresholdShadowStates.Select(state =>
        {
            var shadowSettings = settings with
            {
                ActiveCpuThresholdPercent = state.Experiment.CpuThresholdPercent,
                ActiveIoThresholdBytesPerSecond = state.Experiment.IoThresholdBytesPerSecond
            };
            var comparisonPlan = state.Experiment.IsBaseline ? formalPlan : recomputedBaselinePlan;
            var comparisonKeys = comparisonPlan.Candidates
                .Select(candidate => candidate.Family.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var comparisonEvaluations = comparisonPlan.CandidateEvaluations
                .ToDictionary(evaluation => evaluation.FamilyKey, StringComparer.OrdinalIgnoreCase);
            var shadowPlan = state.Experiment.IsBaseline
                ? recomputedBaselinePlan
                : createReadOnlyPlan(shadowSettings);
            var shadowKeys = shadowPlan.Candidates
                .Select(candidate => candidate.Family.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var addedKeys = shadowKeys.Except(comparisonKeys, StringComparer.OrdinalIgnoreCase).ToArray();
            var removedKeys = comparisonKeys.Except(shadowKeys, StringComparer.OrdinalIgnoreCase).ToArray();
            var shadowEvaluations = shadowPlan.CandidateEvaluations
                .ToDictionary(evaluation => evaluation.FamilyKey, StringComparer.OrdinalIgnoreCase);
            var differences = addedKeys
                .Concat(removedKeys)
                .Select(key => CreateActivityThresholdDifference(
                    key,
                    familyByKey,
                    comparisonEvaluations,
                    shadowEvaluations))
                .Where(difference => difference is not null)
                .Select(difference => difference!)
                .ToArray();
            return new ActivityThresholdShadowMetric(
                state.Experiment.Key,
                state.Experiment.CpuThresholdPercent,
                state.Experiment.IoThresholdBytesPerSecond,
                shadowPlan.CandidateEvaluations.Count(evaluation => evaluation.IsEligible),
                shadowPlan.Candidates.Count,
                shadowPlan.Candidates.Sum(candidate => candidate.TargetProcesses.Count),
                shadowPlan.Candidates.Sum(candidate => candidate.PotentialReleaseBytes),
                addedKeys.Length,
                removedKeys.Length,
                shadowPlan.CandidateEvaluations.Count(evaluation =>
                    evaluation.ExclusionReasons.Contains(CandidateExclusionReason.CurrentCpuActivity)),
                shadowPlan.CandidateEvaluations.Count(evaluation =>
                    evaluation.ExclusionReasons.Contains(CandidateExclusionReason.CurrentIoActivity)))
            {
                ComparisonKind = state.Experiment.IsBaseline ? "formal-plan-drift" : "recomputed-baseline",
                ParameterName = state.Experiment.ParameterName,
                BaselineValue = state.Experiment.BaselineValue,
                ShadowValue = state.Experiment.ShadowValue,
                IsBaseline = state.Experiment.IsBaseline,
                AddedFamilyIds = addedKeys
                    .Select(CalibrationFamilyIdentity.Create)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                RemovedFamilyIds = removedKeys
                    .Select(CalibrationFamilyIdentity.Create)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                Differences = differences
            };
        }).ToArray();
    }

    private static ActivityThresholdCandidateDifferenceMetric? CreateActivityThresholdDifference(
        string familyKey,
        IReadOnlyDictionary<string, ProcessFamilySnapshot> familyByKey,
        IReadOnlyDictionary<string, CandidateEvaluation> baselineEvaluations,
        IReadOnlyDictionary<string, CandidateEvaluation> shadowEvaluations)
    {
        if (!familyByKey.TryGetValue(familyKey, out var family)) return null;
        var baseline = baselineEvaluations.GetValueOrDefault(familyKey);
        var shadow = shadowEvaluations.GetValueOrDefault(familyKey);
        var targetProcessIds = shadow?.TargetProcessIds.ToHashSet() ?? new HashSet<int>();
        var reliableTargetCount = family.Processes.Count(process =>
            targetProcessIds.Contains(process.ProcessId) && process.HasReliableActivitySample);
        return new ActivityThresholdCandidateDifferenceMetric(
            CalibrationFamilyIdentity.Create(familyKey),
            shadow?.IsEligible == true,
            baseline?.LegacyIdleScore ?? family.IdleScore,
            shadow?.LegacyIdleScore ?? family.IdleScore,
            shadow?.TargetWorkingSetBytes ?? 0,
            shadow?.TotalWorkingSetBytes ?? family.WorkingSetBytes,
            reliableTargetCount,
            (shadow?.ExclusionReasons ?? Array.Empty<CandidateExclusionReason>())
                .Distinct()
                .OrderBy(reason => reason)
                .Select(reason => reason.ToString())
                .ToArray());
    }

    private void RecordCandidateTransitions(
        OptimizationPlan plan,
        OptimizationSettings settings,
        DateTimeOffset now)
    {
        if (!_settings.DiagnosticDataCollectionEnabled) return;
        var currentKeys = plan.Candidates
            .Select(candidate => candidate.Family.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_lastPreviewCandidateFamilyKeys is null)
        {
            _lastPreviewCandidateFamilyKeys = currentKeys;
            return;
        }

        var changedKeys = currentKeys
            .Except(_lastPreviewCandidateFamilyKeys, StringComparer.OrdinalIgnoreCase)
            .Concat(_lastPreviewCandidateFamilyKeys.Except(
                currentKeys,
                StringComparer.OrdinalIgnoreCase))
            .ToArray();
        _lastPreviewCandidateFamilyKeys = currentKeys;
        if (changedKeys.Length == 0) return;

        var evaluations = plan.CandidateEvaluations.ToDictionary(
            evaluation => evaluation.FamilyKey,
            StringComparer.OrdinalIgnoreCase);
        var families = _families.ToDictionary(family => family.Key, StringComparer.OrdinalIgnoreCase);
        var runContext = CreateOptimizationRunContext(_settings, manual: false, scheduled: false, longIdle: false);
        foreach (var familyKey in changedKeys)
        {
            if (!families.TryGetValue(familyKey, out var family)) continue;
            var reliable = family.Processes.Where(process => process.HasReliableActivitySample).ToArray();
            var reasons = evaluations.TryGetValue(familyKey, out var evaluation)
                ? evaluation.ExclusionReasons.Select(reason => reason.ToString()).Distinct().ToArray()
                : Array.Empty<string>();
            var maximumIoProcess = reliable
                .OrderByDescending(process => Math.Max(0, process.IoBytesPerSecond))
                .FirstOrDefault();
            var metric = new CandidateTransitionCalibrationMetric(
                runContext,
                now,
                string.Empty,
                currentKeys.Contains(familyKey),
                family.Processes.Count,
                reliable.Length,
                reliable.Sum(process => Math.Max(0, process.CpuPercent)),
                reliable.Select(process => Math.Max(0, process.CpuPercent)).DefaultIfEmpty().Max(),
                reliable.Sum(process => Math.Max(0, process.IoBytesPerSecond)),
                reliable.Select(process => Math.Max(0, process.IoBytesPerSecond)).DefaultIfEmpty().Max(),
                maximumIoProcess?.ProcessId,
                Math.Max(0, maximumIoProcess?.IoReadBytesPerSecond ?? 0),
                Math.Max(0, maximumIoProcess?.IoWriteBytesPerSecond ?? 0),
                Math.Max(0, maximumIoProcess?.IoSampleIntervalSeconds ?? 0),
                family.HasForegroundProcess,
                family.HasVisibleWindow,
                settings.ActiveCpuThresholdPercent,
                settings.ActiveIoThresholdBytesPerSecond,
                reasons);
            QueueCalibrationWrite(() =>
                _calibrationMetricsStore.AppendCandidateTransition(familyKey, metric));
        }
    }

    private void RecordProcessActivitySamples(DateTimeOffset now)
    {
        if (!_settings.DiagnosticDataCollectionEnabled) return;
        var settings = _settings.ResolveOptimizationSettings(manual: false) with
        {
            EnhancedSafety = _settings.EnhancedSafety,
            IgnoreMemoryPressureThreshold = true,
            IntelligentCandidateSelection = _settings.IntelligentCandidateSelection,
            MaxApplications = int.MaxValue
        };
        var protection = CurrentProtectionRules();
        var learningFilters = CurrentLearningFilters(now);
        var plan = _planner.CreatePlan(
            _currentMemory,
            _families,
            settings,
            protection,
            _lastTrimTimes,
            now,
            manual: false,
            _activity,
            automaticBackoffFamilies: null,
            outcomeMultipliers: _applicationBackoffTracker.OutcomeMultipliers,
            intelligentPreview: true,
            learningConfidences: _applicationBackoffTracker.LearningConfidences,
            candidateIdleReadiness: _candidateIdleReadiness,
            pendingReboundObservationFamilies: null,
            lastTrimProcessStartTimes: _lastTrimProcessStartTimes,
            automaticBackoffComponents: learningFilters.BlockedComponents,
            pendingReboundObservationComponents: learningFilters.PendingComponents,
            stableSuppressedComponents: learningFilters.StableComponents);
        var runContext = CreateOptimizationRunContext(
            _settings,
            manual: false,
            scheduled: false,
            longIdle: false);
        var observations = _processIoCalibrationTracker.Observe(
            runContext,
            now,
            plan,
            settings,
            _families);
        foreach (var observation in observations)
        {
            QueueCalibrationWrite(() => _calibrationMetricsStore.AppendProcessIoSample(
                observation.FamilyKey,
                observation.Metric));
        }
        var cpuObservations = _processCpuCalibrationTracker.Observe(
            runContext,
            now,
            plan,
            settings,
            _families);
        foreach (var observation in cpuObservations)
        {
            QueueCalibrationWrite(() => _calibrationMetricsStore.AppendProcessCpuSample(
                observation.FamilyKey,
                observation.Metric));
        }
    }

    private void RecordLargeMemoryOpportunityIfDue(
        OptimizationPlan plan,
        OptimizationSettings settings,
        OptimizationRunContext runContext,
        MemorySnapshot memory,
        DateTimeOffset now)
    {
        if (!_settings.DiagnosticDataCollectionEnabled) return;
        if (!LargeMemoryOpportunityPolicy.ShouldObserve(
                memory,
                plan.Outcome,
                _lastLargeMemoryOpportunityAt,
                now))
        {
            return;
        }

        var shadowSettings = settings with
        {
            IgnoreMemoryPressureThreshold = true,
            MaxApplications = 0
        };
        var learningFilters = CurrentLearningFilters(now);
        var shadowPlan = _planner.CreatePlan(
            memory,
            _families,
            shadowSettings,
            CurrentProtectionRules(),
            _lastTrimTimes,
            now,
            manual: false,
            _activity,
            automaticBackoffFamilies: null,
            outcomeMultipliers: _applicationBackoffTracker.OutcomeMultipliers,
            learningConfidences: _applicationBackoffTracker.LearningConfidences,
            candidateIdleReadiness: _candidateIdleReadiness,
            pendingReboundObservationFamilies: null,
            lastTrimProcessStartTimes: _lastTrimProcessStartTimes,
            automaticBackoffComponents: learningFilters.BlockedComponents,
            pendingReboundObservationComponents: learningFilters.PendingComponents,
            stableSuppressedComponents: learningFilters.StableComponents);
        var metric = LargeMemoryOpportunityPolicy.CreateMetric(
            runContext,
            DateTimeOffset.UtcNow,
            memory,
            shadowPlan);
        _lastLargeMemoryOpportunityAt = now;
        QueueCalibrationWrite(() => _calibrationMetricsStore.AppendLargeMemoryOpportunity(metric));
    }

    private bool TryUpdateSettings(Action<LocalSettings> mutate)
    {
        var result = LocalSettingsTransaction.TryCommit(_settings, mutate, _settingsStore.Save);
        if (result.Succeeded)
        {
            _settings = result.Settings;
            _settingsWriteAvailable = true;
            return true;
        }

        var exception = result.Error!;
        _settingsWriteAvailable = false;
        _state.Status = TF("SettingsSaveFailureFormat", exception.Message);
        _diagnosticLog.Error("Unable to save settings.", exception);
        SynchronizeProfileCopyButton(CustomProfileCatalogList.SelectedItem as ProfileCatalogItem);
        SynchronizeStableSuppressionCopyButton(
            StableSuppressionCatalogList.SelectedItem as StableSuppressionCatalogItem);
        return false;
    }

    private void LoadRuntimeProgress(RuntimeProgressDocument progress, DateTimeOffset now)
    {
        _restoredSessionUptime = RuntimeProgressPolicy.RestoreDuration(progress.SessionUptimeSeconds);
        _scheduledOptimizationAnchor = RuntimeProgressPolicy.RestoreAnchor(
            progress.ScheduledOptimizationElapsedSeconds,
            now);
        _lastSuccessfulOptimizationAt = RuntimeProgressPolicy.RestoreAnchor(
            progress.LastSuccessfulOptimizationElapsedSeconds,
            now);
        _automaticOptimizationSafetyAnchor = progress.AutomaticSafetyElapsedSeconds is { } safety
            ? RuntimeProgressPolicy.RestoreAnchor(safety, now)
            : null;
        _cumulativeTrimBytes = Math.Max(0, progress.CumulativeTrimBytes);
        _cumulativeNetGainBytes = progress.CumulativeNetGainBytes;
        _state.CumulativeTrim = FormatMetricBytes(_cumulativeTrimBytes);
        _state.CumulativeNetGain = FormatMetricBytes(_cumulativeNetGainBytes);
        _pendingRuntimeActivities = progress.Activities.ToList();
        _pendingRuntimeTrimHistory = progress.TrimHistory.ToList();
        _applicationBackoffTracker.RestoreProgress(progress.Backoffs, now);
        _applicationBackoffTracker.RestoreNaturalStableObservationProgress(
            progress.NaturalStableObservations,
            progress.SavedAtUtc,
            now);
        _pendingApplicationRuleProgress = progress.ApplicationRuleTargets ??
                                          Array.Empty<ApplicationOptimizationRuleTargetProgress>();
    }

    private void LoadReboundHistory(ReboundHistoryDocument document)
    {
        _reboundRunHistory.Clear();
        _reboundRunHistory.AddRange(document.Runs.Select(run =>
            run.State == ReboundObservationState.Observing
                ? run with
                {
                    FinishedAt = run.FinishedAt ?? document.SavedAtUtc,
                    State = ReboundObservationState.Replaced
                }
                : run));
        _nextReboundRunSequence = _reboundRunHistory.Count == 0
            ? 0
            : _reboundRunHistory.Max(run => run.Sequence);
        _activeReboundRunSequence = null;
        _lastReboundHistorySaveAt = document.SavedAtUtc;
    }

    private void SaveReboundHistoryIfDue(DateTimeOffset now, bool force = false)
    {
        if (_reboundRunHistory.Count == 0 ||
            !force && _lastReboundHistorySaveAt.HasValue &&
            now - _lastReboundHistorySaveAt.Value < RuntimeProgressPolicy.SaveInterval)
        {
            return;
        }

        try
        {
            _reboundHistoryStore.Save(_reboundRunHistory, DateTimeOffset.UtcNow);
            _lastReboundHistorySaveAt = now;
        }
        catch (Exception exception)
        {
            _diagnosticLog.Warning("Unable to save rebound history.", exception);
        }
    }

    private void RestorePendingRuntimeProgress(
        OptimizationSettings settings,
        DateTimeOffset now)
    {
        foreach (var item in _pendingRuntimeTrimHistory.ToArray())
        {
            var process = _families.SelectMany(family => family.Processes).FirstOrDefault(candidate =>
                candidate.ProcessId == item.ProcessId &&
                candidate.StartTimeFileTimeUtc == item.ProcessStartTimeFileTimeUtc);
            _pendingRuntimeTrimHistory.Remove(item);
            if (process is null) continue;
            _lastTrimTimes[item.ProcessId] = RuntimeProgressPolicy.RestoreAnchor(item.ElapsedSeconds, now);
            _lastTrimProcessStartTimes[item.ProcessId] = item.ProcessStartTimeFileTimeUtc;
        }

        foreach (var item in _pendingRuntimeActivities.ToArray())
        {
            var family = _families.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, item.FamilyKey, StringComparison.OrdinalIgnoreCase));
            if (family is null)
            {
                _pendingRuntimeActivities.Remove(item);
                continue;
            }

            var anchor = family.Processes.FirstOrDefault(process =>
                process.ProcessId == item.AnchorProcessId &&
                process.StartTimeFileTimeUtc == item.AnchorProcessStartTimeFileTimeUtc);
            if (anchor is null || family.HasForegroundProcess)
            {
                _pendingRuntimeActivities.Remove(item);
                continue;
            }

            if (family.Processes.Any(process =>
                    !process.HasReliableActivitySample ||
                    !_candidateIdleReadiness.TryGetValue(process.ProcessId, out var readiness) ||
                    readiness.ConsecutiveReliableLowActivitySamples <
                        CandidateIdleTracker.MinimumReliableLowActivitySamples))
            {
                continue;
            }

            if (family.Processes.Any(process =>
                    process.CpuPercent >= settings.ActiveCpuThresholdPercent ||
                    process.IoBytesPerSecond >= settings.ActiveIoThresholdBytesPerSecond))
            {
                _pendingRuntimeActivities.Remove(item);
                continue;
            }

            _activityTracker.RestoreProgress(
                item.FamilyKey,
                TimeSpan.FromSeconds(item.ObservedSeconds),
                TimeSpan.FromSeconds(item.IdleSeconds),
                item.SampleCount,
                now);
            _pendingRuntimeActivities.Remove(item);
        }
    }

    private void SaveRuntimeProgressIfDue(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_settings.RuntimeProgressPersistenceEnabled ||
            !force && _lastRuntimeProgressSaveAt.HasValue &&
            now - _lastRuntimeProgressSaveAt.Value < RuntimeProgressPolicy.SaveInterval)
        {
            return;
        }

        try
        {
            _runtimeProgressStore.Save(CaptureRuntimeProgress(now));
            _lastRuntimeProgressSaveAt = now;
        }
        catch (Exception exception)
        {
            _diagnosticLog.Warning("Unable to save runtime progress.", exception);
        }
    }

    private RuntimeProgressDocument CaptureRuntimeProgress(DateTimeOffset now)
    {
        var activities = _pendingRuntimeActivities.ToList();
        var activityKeys = activities
            .Select(item => item.FamilyKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _activity)
        {
            if (activityKeys.Contains(pair.Key)) continue;
            var family = _families.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, pair.Key, StringComparison.OrdinalIgnoreCase));
            var anchor = family?.Processes
                .Where(process => process.StartTimeFileTimeUtc > 0)
                .OrderBy(process => process.StartTimeFileTimeUtc)
                .ThenBy(process => process.ProcessId)
                .FirstOrDefault();
            if (anchor?.StartTimeFileTimeUtc is not { } anchorStartTime) continue;
            activities.Add(new RuntimeActivityProgress(
                pair.Key,
                anchor.ProcessId,
                anchorStartTime,
                Math.Max(0, pair.Value.ObservedFor.TotalSeconds),
                Math.Max(0, pair.Value.IdleFor.TotalSeconds),
                Math.Max(0, pair.Value.SampleCount)));
            activityKeys.Add(pair.Key);
        }

        var trimHistory = _pendingRuntimeTrimHistory.ToList();
        var trimProcessIds = trimHistory.Select(item => item.ProcessId).ToHashSet();
        foreach (var pair in _lastTrimTimes)
        {
            if (trimProcessIds.Contains(pair.Key)) continue;
            if (!_lastTrimProcessStartTimes.TryGetValue(pair.Key, out var startTime) || startTime <= 0) continue;
            trimHistory.Add(new RuntimeTrimProgress(
                pair.Key,
                startTime,
                RuntimeProgressPolicy.ElapsedSeconds(pair.Value, now)));
            trimProcessIds.Add(pair.Key);
        }

        return new RuntimeProgressDocument(
            RuntimeProgressStore.CurrentSchemaVersion,
            now,
            RuntimeProgressPolicy.ElapsedSeconds(_scheduledOptimizationAnchor, now),
            RuntimeProgressPolicy.ElapsedSeconds(_lastSuccessfulOptimizationAt, now),
            _automaticOptimizationSafetyAnchor is { } safety
                ? RuntimeProgressPolicy.ElapsedSeconds(safety, now)
                : null,
            _cumulativeTrimBytes,
            _cumulativeNetGainBytes,
            activities,
            trimHistory,
            _applicationBackoffTracker.CaptureProgress(now),
            CurrentSessionUptime().TotalSeconds,
            _applicationRuleRuntime.CaptureProgress(
                ApplicationOptimizationRuleSettings.Resolve(_settings),
                now),
            _applicationBackoffTracker.CaptureNaturalStableObservationProgress());
    }

    private void SaveBenefitLearning()
    {
        try
        {
            _benefitLearningStore.Save(
                _applicationBackoffTracker.LearningRecords,
                _dismissedSuggestionIds,
                _applicationBackoffTracker.FamilyStableLearningRecords);
        }
        catch (Exception exception)
        {
            _diagnosticLog.Warning("Unable to save benefit-learning data.", exception);
        }
    }

    private void AddHistory(string resourceKey, params object?[] arguments) =>
        AddHistory(ActivityHistoryEntry.Create(resourceKey, arguments));

    private void AddHistoryNested(
        string resourceKey,
        string nestedResourceKey,
        IReadOnlyList<object?> nestedArguments) =>
        AddHistory(ActivityHistoryEntry.Create(
            resourceKey,
            new object?[] { string.Empty },
            nestedResourceKey,
            nestedArguments));

    private void AddHistory(ActivityHistoryEntry entry)
    {
        _activityHistory.Insert(0, entry);
        while (_activityHistory.Count > 30) _activityHistory.RemoveAt(_activityHistory.Count - 1);
        RefreshActivityHistory();
        try { _historyStore.Save(_activityHistory); }
        catch (Exception exception) { _diagnosticLog.Warning("Unable to save activity history.", exception); }
        _diagnosticLog.Info(entry.Format(_uiLanguage));
    }

    private void RefreshActivityHistory()
    {
        _state.History.Clear();
        foreach (var entry in _activityHistory) _state.History.Add(entry.Format(_uiLanguage));
    }

    private void UpdateSelfOverhead()
    {
        var overhead = _overheadSampler.Capture();
        var cpu = overhead.HasReliableCpuSample ? $"{overhead.CpuPercent:0.0}% CPU" : T("CpuObserving");
        _state.SelfOverhead = TF(
            "SelfOverheadFormat",
            DisplayFormat.Bytes(overhead.WorkingSetBytes),
            cpu,
            DisplayFormat.Bytes(overhead.PrivateMemoryBytes),
            overhead.HandleCount,
            overhead.ThreadCount);
        RecordMonitoringCalibration(overhead);
    }

    private void RecordMonitoringCalibration(AppOverheadSnapshot overhead)
    {
        if (!_settings.DiagnosticDataCollectionEnabled) return;
        if (_families.Count == 0 || _lastProcessCaptureDuration <= TimeSpan.Zero) return;
        var now = DateTimeOffset.UtcNow;
        if (_lastMonitoringCalibrationAt.HasValue &&
            now - _lastMonitoringCalibrationAt.Value < TimeSpan.FromMinutes(5))
        {
            return;
        }

        var metric = new MonitoringCalibrationMetric(
            now,
            _lastProcessCaptureDuration.TotalMilliseconds,
            _monitorTimer.Interval.TotalSeconds,
            _families.Sum(family => family.Processes.Count),
            _families.Count,
            _settings.AutoOptimization,
            overhead.WorkingSetBytes,
            overhead.PrivateMemoryBytes,
            overhead.CpuPercent,
            overhead.IoBytesPerSecond,
            overhead.ThreadCount,
            overhead.HandleCount,
            overhead.HasReliableCpuSample,
            overhead.HasReliableIoSample)
        {
            RelationshipSnapshotMilliseconds = _lastProcessCaptureDiagnostics.RelationshipSnapshotMilliseconds,
            WindowEnumerationMilliseconds = _lastProcessCaptureDiagnostics.WindowEnumerationMilliseconds,
            PathReadMilliseconds = _lastProcessCaptureDiagnostics.PathReadMilliseconds,
            SlowestPathReadMilliseconds = _lastProcessCaptureDiagnostics.SlowestPathReadMilliseconds,
            SlowestPathProcessId = _lastProcessCaptureDiagnostics.SlowestPathProcessId,
            MainModuleFallbackCount = _lastProcessCaptureDiagnostics.MainModuleFallbackCount,
            PathFailureCount = _lastProcessCaptureDiagnostics.PathFailureCount,
            CpuReadMilliseconds = _lastProcessCaptureDiagnostics.CpuReadMilliseconds,
            IoReadMilliseconds = _lastProcessCaptureDiagnostics.IoReadMilliseconds,
            ProcessLoopMilliseconds = _lastProcessCaptureDiagnostics.ProcessLoopMilliseconds,
            OtherMilliseconds = _lastProcessCaptureDiagnostics.OtherMilliseconds
        };
        _lastMonitoringCalibrationAt = now;
        QueueCalibrationWrite(() => _calibrationMetricsStore.AppendMonitoring(metric));
    }

    private void PruneLastTrimHistory(
        IReadOnlyList<ProcessSnapshot> currentProcesses,
        DateTimeOffset now)
    {
        var currentStartTimes = currentProcesses
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.First().StartTimeFileTimeUtc);
        foreach (var processId in _lastTrimTimes.Keys.ToArray())
        {
            var currentProcessObserved = currentStartTimes.TryGetValue(
                processId,
                out var currentStartTimeFileTimeUtc);
            if (!_lastTrimProcessStartTimes.TryGetValue(processId, out var recordedStartTimeFileTimeUtc) ||
                ProcessTrimHistoryPolicy.ShouldDiscard(
                    _lastTrimTimes[processId],
                    recordedStartTimeFileTimeUtc,
                    currentProcessObserved,
                    currentStartTimeFileTimeUtc,
                    now))
            {
                _lastTrimTimes.Remove(processId);
                _lastTrimProcessStartTimes.Remove(processId);
            }
        }

        foreach (var processId in _lastTrimProcessStartTimes.Keys
                     .Where(processId => !_lastTrimTimes.ContainsKey(processId))
                     .ToArray())
        {
            _lastTrimProcessStartTimes.Remove(processId);
        }
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e)
    {
        LogWindowAnimationState("minimize-click-before");
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestore_OnClick(object sender, RoutedEventArgs e)
    {
        if (_compactMode) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnStateChanged(object? sender, EventArgs e)
    {
        LogWindowAnimationState("state-changed");
        if (MaximizeRestoreButton is null) return;
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = isMaximized ? "\uE923" : "\uE922";
        MaximizeRestoreIcon.Data = (Geometry)FindResource(isMaximized ? "IconRestore" : "IconMaximize");
        MaximizeRestoreButton.Content = MaximizeRestoreIcon;
        var resourceKey = isMaximized ? "RestoreWindow" : "MaximizeWindow";
        MaximizeRestoreButton.SetResourceReference(ToolTipProperty, resourceKey);
        MaximizeRestoreButton.SetResourceReference(AutomationProperties.NameProperty, resourceKey);
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        _trayOpenMenuItem = CreateTrayMenuItem(T("TrayOpen"), "IconFolderOpen");
        _trayOptimizeMenuItem = CreateTrayMenuItem(T("TrayOptimize"), "IconZap");
        _trayExitMenuItem = CreateTrayMenuItem(T("TrayExit"), "IconClose");
        _trayOpenMenuItem.Click += (_, _) => RestoreFromTray();
        _trayOptimizeMenuItem.Click += async (_, _) => await RunOptimizationAsync(manual: true);
        _trayExitMenuItem.Click += (_, _) => ExitApplication();
        _trayMenu = new System.Windows.Controls.ContextMenu
        {
            Style = (Style)FindResource("ThemedContextMenuStyle"),
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            MinWidth = 176
        };
        _trayMenu.Opened += TrayMenu_OnOpened;
        CopyThemeResources(_trayMenu.Resources);
        _trayMenu.Items.Add(_trayOpenMenuItem);
        _trayMenu.Items.Add(_trayOptimizeMenuItem);
        _trayExitMenuItem.Margin = new Thickness(0, 4, 0, 0);
        _trayMenu.Items.Add(_trayExitMenuItem);
        var icon = new Forms.NotifyIcon { Text = "MuseRAM", Icon = _applicationIcon, Visible = true };
        icon.MouseClick += (_, args) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (args.Button == Forms.MouseButtons.Left)
                {
                    RestoreFromTray();
                    return;
                }
                if (args.Button != Forms.MouseButtons.Right) return;
                CopyThemeResources(_trayMenu.Resources);
                _trayMenu.IsOpen = true;
            });
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        return icon;
    }

    private void TrayMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_trayMenu.IsOpen || PresentationSource.FromVisual(_trayMenu) is not HwndSource source) return;
            _ = SetForegroundWindow(source.Handle);
            _trayMenu.Focus();
        }, DispatcherPriority.Input);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private MenuItem CreateTrayMenuItem(string text, string iconResourceKey)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(iconResourceKey),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextBrush");
        header.Children.Add(new Viewbox
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon
        });
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        return new MenuItem
        {
            Header = header,
            Tag = label,
            Style = (Style)FindResource("ThemedMenuItemStyle")
        };
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(executablePath) && System.Drawing.Icon.ExtractAssociatedIcon(executablePath) is { } icon
            ? icon
            : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private void ApplyTrayLanguage()
    {
        if (_trayOpenMenuItem.Tag is TextBlock open) open.Text = T("TrayOpen");
        if (_trayOptimizeMenuItem.Tag is TextBlock optimize) optimize.Text = T("TrayOptimize");
        if (_trayExitMenuItem.Tag is TextBlock exit) exit.Text = T("TrayExit");
        UpdateTrayMemoryIcon();
    }

    private void UpdateTrayMemoryIcon(MemorySnapshot? memory = null)
    {
        var percent = memory.HasValue
            ? (int?)memory.Value.LoadPercent
            : _currentMemory.TotalPhysicalBytes > 0
                ? (int?)_currentMemory.LoadPercent
                : null;
        var tooltip = _settings.ShowMemoryUsageInTrayIcon && percent.HasValue
            ? TF("TrayMemoryUsageFormat", percent.Value)
            : "MuseRAM";
        try
        {
            _trayMemoryIconController.Apply(
                _trayIcon,
                _applicationIcon,
                _settings.ShowMemoryUsageInTrayIcon,
                percent,
                tooltip);
            _trayMemoryIconFailureLogged = false;
        }
        catch (Exception exception)
        {
            _trayIcon.Icon = _applicationIcon;
            if (_trayMemoryIconFailureLogged) return;
            _trayMemoryIconFailureLogged = true;
            _diagnosticLog.Warning("Unable to update the tray memory icon.", exception);
        }
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;

        var behavior = _settings.CloseButtonBehavior;
        if (behavior == CloseButtonBehavior.Ask)
        {
            var result = ShowCloseBehaviorDialog();
            if (!result.HasValue)
            {
                e.Cancel = true;
                return;
            }

            behavior = result.Value.Behavior;
            if (result.Value.Remember)
            {
                if (TryUpdateSettings(settings => settings.CloseButtonBehavior = behavior))
                {
                    _syncingControls = true;
                    try
                    {
                        CloseBehaviorBox.SelectedItem = CloseBehaviorBox.Items
                            .OfType<ComboBoxItem>()
                            .First(item => string.Equals(
                                item.Tag as string,
                                behavior.ToString(),
                                StringComparison.Ordinal));
                    }
                    finally { _syncingControls = false; }
                }
            }
        }

        if (behavior == CloseButtonBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        PrepareForExit();
    }

    private ClosePromptResult? ShowCloseBehaviorDialog()
    {
        CloseButtonBehavior? selected = null;
        var remember = new System.Windows.Controls.CheckBox
        {
            Content = T("CloseDialogRemember"),
            Style = (Style)FindResource("ThemedCheckBoxStyle"),
            Margin = new Thickness(0, 18, 0, 0)
        };
        var message = new TextBlock
        {
            Text = T("CloseDialogMessage"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (MediaBrush)FindResource("MutedBrush")
        };
        var exit = new Button
        {
            Content = T("CloseDialogExit"),
            MinWidth = 130,
            Style = (Style)FindResource("DangerButtonStyle")
        };
        var tray = new Button
        {
            Content = T("CloseDialogTray"),
            MinWidth = 170,
            Margin = new Thickness(10, 0, 0, 0),
            IsDefault = true,
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(exit);
        buttons.Children.Add(tray);
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(message);
        root.Children.Add(remember);
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = this,
            Title = T("CloseDialogTitle"),
            Width = 480,
            Height = 230,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (MediaBrush)FindResource("WindowBrush"),
            Foreground = (MediaBrush)FindResource("TextBrush"),
            Content = root
        };
        exit.Click += (_, _) => { selected = CloseButtonBehavior.Exit; dialog.Close(); };
        tray.Click += (_, _) => { selected = CloseButtonBehavior.MinimizeToTray; dialog.Close(); };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) dialog.Close();
        };
        ApplyDialogTheme(dialog);
        dialog.ShowDialog();
        return selected.HasValue ? new ClosePromptResult(selected.Value, remember.IsChecked == true) : null;
    }

    private void HideToTray()
    {
        LogWindowAnimationState("hide-to-tray-before");
        Hide();
        LogWindowAnimationState("hide-to-tray-after");
    }

    private void RestoreFromTray()
    {
        LogWindowAnimationState("restore-before");
        Opacity = 1;
        ShowActivated = true;
        ShowInTaskbar = true;
        if (!IsVisible && WindowThemeService.TrySetCloaked(this, true))
        {
            _revealAfterRendering = true;
            CompositionTarget.Rendering -= CompositionTarget_OnRendering;
            CompositionTarget.Rendering += CompositionTarget_OnRendering;
        }
        Show();
        LogWindowAnimationState("restore-after-show");
        WindowState = WindowState.Normal;
        LogWindowAnimationState("restore-after-normal");
        Activate();
        LogWindowAnimationState("restore-after-activate");
        _ = Dispatcher.BeginInvoke(
            () => LogWindowAnimationState("restore-settled"),
            DispatcherPriority.Background);
    }

    private void CompositionTarget_OnRendering(object? sender, EventArgs e)
    {
        if (!_revealAfterRendering) return;
        _revealAfterRendering = false;
        CompositionTarget.Rendering -= CompositionTarget_OnRendering;
        _ = Dispatcher.BeginInvoke(() =>
        {
            WindowThemeService.FlushComposition();
            if (!WindowThemeService.TrySetCloaked(this, false))
                _diagnosticLog.Warning("Unable to reveal the main window after rendering.");
        }, DispatcherPriority.Background);
    }

    internal void RestoreFromExternalActivation()
    {
        LogWindowAnimationState("external-activation");
        if (!IsVisible)
        {
            RestoreFromTray();
            return;
        }

        ShowActivated = true;
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private IntPtr WindowAnimationMessageHook(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == 0x0014)
        {
            handled = true;
            return new IntPtr(1);
        }

        var detail = message switch
        {
            0x0005 => $"WM_SIZE({wParam.ToInt64()})",
            0x0006 => $"WM_ACTIVATE({wParam.ToInt64() & 0xFFFF})",
            0x0007 => "WM_SETFOCUS",
            0x0018 => $"WM_SHOWWINDOW({wParam.ToInt64()})",
            0x0047 => "WM_WINDOWPOSCHANGED",
            0x0086 => $"WM_NCACTIVATE({wParam.ToInt64()})",
            0x0112 => $"WM_SYSCOMMAND(0x{wParam.ToInt64() & 0xFFF0:X})",
            _ => null
        };
        if (detail is not null && _settings.DiagnosticDataCollectionEnabled)
        {
            _diagnosticLog.Info(
                $"[DEBUG-MYDOCK] message={detail}; ManagedState={WindowState}; " +
                $"IsVisible={IsVisible}; Native={WindowThemeService.DescribeNativeWindowState(this)}");
        }
        return IntPtr.Zero;
    }

    private void LogWindowAnimationState(string stage)
    {
        if (!_settings.DiagnosticDataCollectionEnabled) return;
        _diagnosticLog.Info(
            $"[DEBUG-MYDOCK] {stage}; ManagedState={WindowState}; IsVisible={IsVisible}; " +
            $"ShowInTaskbar={ShowInTaskbar}; IsActive={IsActive}; " +
            WindowThemeService.DescribeNativeWindowState(this));
    }

    private void ExitApplication()
    {
        _diagnosticLog.Info("MuseRAM exited.");
        PrepareForExit();
        Close();
    }

    private void PrepareForExit()
    {
        SynchronizeReboundRunHistory(DateTimeOffset.Now);
        SaveReboundHistoryIfDue(DateTimeOffset.Now, force: true);
        SaveRuntimeProgressIfDue(force: true);
        _exitRequested = true;
        _monitorTimer.Stop();
        _memoryTimer.Stop();
        StopResponsivenessMonitoring();
        FlushCalibrationWrites();
        SystemEvents.UserPreferenceChanged -= SystemEvents_OnUserPreferenceChanged;
        _trayMenu.IsOpen = false;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMemoryIconController.Dispose();
        _applicationIcon.Dispose();
    }

    private readonly record struct ClosePromptResult(CloseButtonBehavior Behavior, bool Remember);

    private sealed record ProfileChoice(
        string Name,
        OptimizationProfile? BuiltInProfile,
        string? CustomProfileId,
        string BrushKey);

    private sealed record ProfileCatalogItem(
        string Name,
        string Kind,
        OptimizationProfile? BuiltInProfile,
        string? CustomProfileId);

    private sealed record StableSuppressionChoice(
        string Name,
        StableStateSuppressionMode Mode,
        string? CustomProfileId);

    private sealed record StableSuppressionCatalogItem(
        string Name,
        string Kind,
        OptimizationProfile? BuiltInProfile,
        string? CustomProfileId);

    private sealed record SliderBounds(
        double MinApplications,
        double MaxApplications,
        double MinFamilyMiB,
        double MaxFamilyMiB,
        double MinIdleScore,
        double MaxIdleScore,
        double MinTriggerPercent,
        double MaxTriggerPercent,
        double MinProcessCooldown,
        double MaxProcessCooldown,
        double MinAutoCooldown,
        double MaxAutoCooldown,
        double MinVisibleWindowIdleMinutes,
        double MaxVisibleWindowIdleMinutes,
        double MinActiveCpuPercent,
        double MaxActiveCpuPercent,
        double MinActiveIoMiBPerSecond,
        double MaxActiveIoMiBPerSecond);

    private sealed record DeepReleaseTarget(
        ProcessSnapshot Process,
        IReadOnlySet<int> RelatedProcessIds);

    private sealed class ComponentTrimAccumulator
    {
        public ComponentTrimAccumulator(string componentKey, string? executablePath)
        {
            ComponentKey = componentKey;
            ExecutablePath = executablePath;
        }

        public string ComponentKey { get; }
        public string? ExecutablePath { get; }
        public long WorkingSetBeforeBytes { get; set; }
        public long WorkingSetAfterBytes { get; set; }
        public List<int> ProcessIds { get; } = new();
        public List<long> ProcessStartTimes { get; } = new();
    }

    private sealed class ApplicationOptimizationRuleExecutionState
    {
        public int ExecutionsCompleted { get; set; }
        public DateTimeOffset? LastExecutionAt { get; set; }
        public DateTimeOffset? LastExecutionStartedAt { get; set; }
        public long LastReleasedBytes { get; set; }
        public long? LastRetainedBytes { get; set; }
        public string? LastSkippedReason { get; set; }
        public string? TargetLaunchSignature { get; set; }
    }

    private readonly record struct ApplicationRuleOutcomeAttributionKey(
        DateTimeOffset StartedAt,
        string ComponentKey);

    private readonly record struct ApplicationRuleTargetReference(
        string RuleId,
        string TargetIdentity);

    private sealed record ApplicationRuleDisplay(
        bool HasRule,
        string Status,
        string History,
        string Skip,
        string Detail);

    private static string SignedBytes(long bytes) => bytes >= 0 ? $"+{DisplayFormat.Bytes(bytes)}" : $"-{DisplayFormat.Bytes(Math.Abs(bytes))}";

    private static string FormatMetricBytes(long bytes)
    {
        var sign = bytes >= 0 ? "+" : "-";
        var value = Math.Abs((double)bytes);
        if (value < 1024) return $"{sign}{value:0} B";
        if (value < 1024 * 1024) return $"{sign}{value / 1024:0.0} KB";
        if (value < 1024d * 1024 * 1024) return $"{sign}{value / (1024d * 1024):0.0} MB";
        return $"{sign}{value / (1024d * 1024 * 1024):0.00} GB";
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private static void SynchronizeCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var updated = values.ToArray();
        var sharedCount = Math.Min(target.Count, updated.Length);
        for (var index = 0; index < sharedCount; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(target[index], updated[index]))
            {
                target[index] = updated[index];
            }
        }

        while (target.Count > updated.Length) target.RemoveAt(target.Count - 1);
        for (var index = target.Count; index < updated.Length; index++) target.Add(updated[index]);
    }

    private sealed record OptimizationResultDisplay(long WorkingSetReductionBytes, long NetAvailableBytes);
}
