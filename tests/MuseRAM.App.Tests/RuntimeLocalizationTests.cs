using MuseRAM.App;

namespace MuseRAM.App.Tests;

public sealed class RuntimeLocalizationTests
{
    [Theory]
    [InlineData(UiLanguage.ChineseSimplified)]
    [InlineData(UiLanguage.English)]
    public void CurrentVersionTextMatchesApplicationVersion(UiLanguage language)
    {
        Assert.Contains("0.1.7.1", UiTextCatalog.For(language)["CurrentVersion"]);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "学习中")]
    [InlineData(UiLanguage.English, "Learning")]
    public void CandidateLearningLabelCannotBeConfusedWithStableSamples(
        UiLanguage language,
        string expectedLabel)
    {
        var text = UiTextCatalog.For(language);

        Assert.Contains(expectedLabel, text["LearningInProgressShortFormat"]);
        Assert.Contains(expectedLabel, text["RankingLearningFormat"]);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "已完成一次自然稳态确认并计入样本")]
    [InlineData(UiLanguage.English, "completed one natural steady-state confirmation")]
    public void SessionStableHelpDescribesACommittedSample(
        UiLanguage language,
        string expectedText)
    {
        Assert.Contains(expectedText, UiTextCatalog.For(language)["ProcessStatusSessionStableHelp"]);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "当前锚点复核")]
    [InlineData(UiLanguage.English, "current-anchor reviews")]
    public void StableReviewScheduleSeparatesAnchorReviewsFromHighEvidence(
        UiLanguage language,
        string expectedText)
    {
        var text = UiTextCatalog.For(language);

        Assert.Contains(expectedText, text["ProcessStatusStableReviewScheduleDetailFormat"],
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedText, text["ProcessStatusStableReviewDueDetailFormat"],
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedText, text["ProcessStatusStableReviewAwaitingRecoveryCycleDetailFormat"],
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "优化后 120 秒占用", "各轮")]
    [InlineData(UiLanguage.English, "Historical usage 120 s after optimization", "each run")]
    public void BenefitWorkingSetLabelDescribesAggregatedHistoricalObservations(
        UiLanguage language,
        string expectedLabel,
        string expectedScopeText)
    {
        var text = UiTextCatalog.For(language);

        Assert.Equal(expectedLabel, text["PostOptimizationWorkingSet"]);
        Assert.Contains(expectedScopeText, text["BenefitLearningDataScopeNotice"]);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "无限制")]
    [InlineData(UiLanguage.English, "Unlimited")]
    public void StableWorkingSetUnlimitedValueIsExplicitlyLabeled(
        UiLanguage language,
        string expected)
    {
        Assert.Equal(expected, UiTextCatalog.For(language)["StableMaximumWorkingSetUnlimitedValue"]);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "组件有效样本")]
    [InlineData(UiLanguage.English, "component samples")]
    public void BenefitLearningProgressLabelsComponentValidSamples(UiLanguage language, string expectedLabel)
    {
        var text = UiTextCatalog.For(language);

        Assert.Contains(expectedLabel, text["BenefitLearningProgressFormat"]);
        Assert.Contains(expectedLabel, text["BenefitLearningProgressWithLegacyFormat"]);
        Assert.DoesNotContain("launch samples", text["BenefitLearningProgressWithLegacyFormat"], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "滚动", "2 分钟")]
    [InlineData(UiLanguage.English, "rolling", "2 continuous stable minutes")]
    public void StableStateHelpExplainsRollingObservationAndImmediateValidation(
        UiLanguage language,
        string rollingText,
        string validationText)
    {
        var text = UiTextCatalog.For(language);

        Assert.Contains(rollingText, text["StableStateSuppressionHelp"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(validationText, text["StableStateSuppressionHelp"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(rollingText, text["StableReferenceHelpEnabledFormat"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(validationText, text["StableReferenceHelpEnabledFormat"], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified, "稳态确认样本")]
    [InlineData(UiLanguage.English, "confirmed steady-state samples")]
    public void StableLimitHelpUsesNaturalStableSamples(UiLanguage language, string expectedSource)
    {
        var help = UiTextCatalog.For(language)["StableSuppressionLimitHelpFormat"];

        Assert.Contains(expectedSource, help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("120-second Working Set", help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("120 秒 Working Set", help, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(UiLanguage.ChineseSimplified)]
    [InlineData(UiLanguage.English)]
    public void CriticalRuntimeAndDialogTextIsAvailable(UiLanguage language)
    {
        var text = UiTextCatalog.For(language);
        var keys = new[]
        {
            "RuntimeSampling", "PlanLowPressure", "PlanNoCandidates", "PlanCandidatesFormat",
            "ActivityForeground", "ActivityVisible", "ActivityMinimized", "ActivityIdle", "ActivityWorking",
            "CandidateMemory", "CandidateMemoryHelp", "CandidateMemoryFormat", "CandidateDisplayLimit",
            "CandidateDisplayLimitHelp", "CandidateDisplayLimitCurrentFormat", "CandidateDisplayUnlimited",
            "ProtectionNoCandidates", "ProtectionEntireFamily", "ProtectionPartial", "ProtectionNone",
            "ProtectionUpdatedFormat", "RunningProtectionExecutableFormat", "RunningProtectionProcessFormat",
            "RunningProtectionProcessHelp", "ExpandExecutableProcesses", "CandidateMode",
            "StandardCandidateMode", "QuickCandidateMode", "StandardCandidateModeHelp", "QuickCandidateModeHelp",
            "AddRunningHelp", "AddExeHelp", "RunningProtectionApplicationFormat",
            "RunningProtectionFamilyCheckHelp", "RunningProtectionExecutableCheckHelp",
            "RunningProtectionStatusHelp", "ProtectedStateHelp", "ProtectedProcessCountSuffix", "ServiceDialogWarning", "ForceTerminatePromptFormat",
            "ApplicationRuleNotConfigured", "ApplicationRuleEnabled", "ApplicationRuleDisabled",
            "ApplicationRuleCompleted", "ApplicationRuleTargetFamily", "ApplicationRuleTargetExecutable",
            "ApplicationRuleTargetProtectedGroupFormat",
            "ApplicationRuleTargetConflictFormat", "ApplicationRuleTargetConflictInRule",
            "ApplicationRuleStatusFormat", "ApplicationRuleHistoryFormat", "ApplicationRuleSkipFormat",
            "ApplicationRuleNoRecentSkip", "ApplicationRuleNever", "ApplicationRuleObservationPending",
            "ApplicationRuleNextTargetRunning", "ApplicationRuleNextWorkingSet", "ApplicationRuleNextRefresh",
            "ApplicationRuleFollowAutomatic", "ApplicationRuleFollowAutomaticHelp",
            "ApplicationRuleDelayTriggerHelp", "ApplicationRuleDelayAnchorHelp", "ApplicationRuleDelayMinutesHelp",
            "ApplicationRuleExecutionCountHelp", "ApplicationRuleExecutionIntervalHelp",
            "ApplicationRuleRepeatIndefinitely", "ApplicationRuleRepeatIndefinitelyHelp",
            "ApplicationRuleWorkingSetTriggerHelp", "ApplicationRuleFollowProfileThresholdHelp",
            "ApplicationRuleWorkingSetThresholdHelp", "ApplicationRuleCooldownHelp",
            "ApplicationRuleRestartWithApplicationHelp", "ApplicationRuleNextAutomaticOptimization",
            "ApplicationRuleAutomaticOptimizationDisabled",
            "ApplicationRuleSkipTargetNotRunning", "ApplicationRuleSkipNoCandidates", "ApplicationRuleSkipProtected",
            "ApplicationRuleSkipSampling", "ApplicationRuleSkipActivity", "ApplicationRuleSkipWorkingSet",
            "ApplicationRuleSkipSafety", "ApplicationRuleSkipNoSuccessfulTrim",
            "TrayOpen", "TrayExit", "AlreadyRunningMessage", "MemoryMetrics", "MemoryChange",
            "ComparedWithPreviousSample", "RecentTrim", "CumulativeTrim", "BoostNetGain", "CumulativeNetGain",
            "LastUpdated", "SessionUptime", "SessionUptimeMinutesFormat", "SessionUptimeHoursFormat",
            "SessionUptimeDaysFormat", "SelfOverheadFormat", "Lite", "Turbo", "Ultimate",
            "ProfileHelpTitle", "ProfileHelpIntro", "LiteDescription", "TurboDescription",
            "UltimateDescription", "EnhancedSafety", "EnhancedSafetyDescription",
            "IntelligentCandidateSelection", "IntelligentCandidateSelectionDescription", "IgnoreMemoryPressureShort",
            "StableStateSuppression", "StableStateSuppressionHelp", "StableStateSuppressionDescription",
            "StableSuppressionFollowProfile", "StableSuppressionFollowCurrentFormat", "StableSuppressionReduceRepeated", "StableSuppressionBalanced",
            "StableSuppressionFasterReevaluation", "StableSuppressionCustom", "StableSuppressionDisabled", "StableSuppressionPausedWithoutLearning",
            "CustomStableStateSuppression", "CustomStableStateSuppressionHelp", "CustomProfilesTab", "CustomStableSuppressionTab",
            "CustomStableSuppressionDescription", "StableSuppressionTemplate", "ApplyTemplate", "SaveStableSuppression",
            "CustomStableSuppressionSaved", "StableMinimumSamples",
            "StableRecordAgeDays", "StableRelativeMarginPercent", "StableAbsoluteMarginMiB",
            "StableMinimumSamplesHelp", "StableRecordAgeDaysHelp", "StableRelativeMarginPercentHelp", "StableAbsoluteMarginMiBHelp",
            "StableObservationMinutes", "StableObservationMinutesHelp", "StableSampleIntervalMinutes", "StableSampleIntervalMinutesHelp",
            "StableMaximumSamplesPerLaunch", "StableMaximumSamplesPerLaunchHelp", "StableSamplePool", "StableSamplePoolHelp",
            "IdleScoreColumn", "IdleScoreDetailFormat", "IdleScoreUnavailable", "ProcessMemoryDetailFormat", "ProcessMemoryInsufficientDetailFormat",
            "BelowFamilyWorkingSet", "BelowProcessThreshold", "RunningProtectionProcessBelowThresholdFormat",
            "SelectedOptimizationProcessableWorkingSetInsufficientFormat",
            "ProtectionSuggestions", "ProtectionSuggestionsNone", "ProtectionSuggestionsAvailableFormat",
            "ProtectionSuggestionsTitle", "ProtectionSuggestionsDescription", "ProtectEntireSuggestedFamily",
            "ProtectionSuggestionExecutableFormat", "ProtectionSuggestionReasonFormat",
            "IgnoreProtectionSuggestions", "ProtectSelectedSuggestions",
            "DiagnosticDataCollection", "DiagnosticDataCollectionHelp", "DiagnosticDataCollectionDescription",
            "ClearDiagnosticData", "ClearDiagnosticDataConfirm", "DiagnosticDataCleared", "ClearDiagnosticDataFailureFormat",
            "RuntimeProgressPersistence", "RuntimeProgressPersistenceHelp", "RuntimeProgressPersistenceDescription",
            "RuntimeProgressData",
            "DiagnosticCalibrationData", "DiagnosticLogData", "DiagnosticFilesHelp",
            "CustomProfileIgnoreMemoryPressureHelp", "AllowForegroundOptimizationHelp",
            "ProcessDescription", "ScheduledOptimizationHelp", "AutoCooldownHelp",
            "SelectedOptimizationProtectedFormat", "SelectedOptimizationSamplingFormat",
            "SelectedOptimizationActiveFormat", "SelectedOptimizationSafetyFormat",
            "SelectedOptimizationPromptTitle", "SelectedOptimizationPromptFormat",
            "SelectedOptimizationDoNotRemind",
            "CandidateSortingComprehensive", "CandidateSortingBenefitAware", "RankingReference", "NoBackoff",
            "ReboundObservationPending", "AutoBackoffLongTerm", "AutoBackoffLongTermAwaitingIdle",
            "ProcessStatusSessionStable", "ProcessStatusSessionStableHelp", "ProcessStatusLongTermStable",
            "ProcessStatusStableObservation", "ProcessStatusStableObservationHelp",
            "ProcessStatusStableReview", "ProcessStatusStableReviewHelp",
            "ProcessStatusStableReviewRollingDetailFormat",
            "ProcessStatusStableReviewRollingActiveDetailFormat",
            "StableStateRetainedDuringReviewFormat",
            "ProcessStatusBenefitObservation", "ProcessStatusBenefitObservationHelp",
            "ProcessStatusBenefitObservationWithHistoricalStable",
            "ProcessStatusBenefitObservationWithHistoricalStableHelp",
            "ProcessStatusForeground", "ProcessStatusForegroundHelp", "ProcessStatusIoActivity",
            "ProcessStatusCpuActivity", "ProcessStatusActivityHelp", "ProcessStatusSampling",
            "ProcessStatusSamplingHelp", "ProcessStatusCooldown", "ProcessStatusCooldownHelp",
            "ProcessStatusVisibleWait", "ProcessStatusVisibleWaitHelp", "ProcessStatusBelowThreshold",
            "ProcessStatusBelowThresholdHelp", "ProcessStatusRelationship", "ProcessStatusRelationshipHelp",
            "ProcessStatusIdleScore", "ProcessStatusIdleScoreHelp", "ProcessStatusCandidateReady",
            "ProcessStatusCandidateReadyHelp",
            "CompositeRankFormat", "RankingIdleHigh", "RankingIdleMedium", "RankingIdleLow",
            "RankingMemoryLarge", "RankingMemoryMedium", "RankingMemorySmall", "CompositeFallbackRankFormat",
            "RankingLearningFormat", "ExpectedSustainedReleaseFormat", "BenefitLearningWaiting",
            "CandidateBenefitLearning", "LearningOffShort", "LearningPendingShort",
            "LearningInProgressShortFormat", "LearningEstimateShortFormat",
            "BenefitLearningProgressFormat", "BenefitLearningOffEmpty", "BenefitLearningOffSavedFormat",
            "ReboundDetails", "ReboundApplicationsUnavailable", "PrimaryReboundApplicationFormat",
            "ReboundApplicationsObservingFormat", "NoApplicationReboundObserved", "ReboundDetailsTitle",
            "ReboundDetailsObservingFormat", "ReboundDetailsComplete",
            "ReboundDetailsObservingWithTimeFormat", "ReboundDetailsCompleteWithTimeFormat",
            "AutoOptimizationStatusHelp", "InitialTrim", "RegainedWorkingSet",
            "ApplicationReboundRate", "ObservationStatus", "ObservationInProgress", "ObservationComplete",
            "ReboundDetailsDisclaimer", "ReboundDetailsClose", "ReboundHistory", "ReboundHistoryAvailable",
            "ReboundHistoryLimitFormat", "ReboundHistoryAll", "ReboundHistoryRunFormat",
            "ReboundHistoryRunCompactFormat", "ReboundHistoryRunStateFormat", "ReboundHistoryAllShort",
            "ReboundHistoryEmpty", "BenefitLearningAnalysisTitle", "ReviewProtectionSuggestions",
            "BenefitLearningDataScopeNotice", "PostOptimizationWorkingSet", "SustainedRelease",
            "AverageRebound", "LearningSamples", "LastObserved", "ProtectionSuggestionStatus",
            "LearningSampleSummaryFormat", "ProtectionSuggested", "NoProtectionSuggestion", "UnknownApplication",
            "ReboundRunManual", "ReboundRunAutomatic", "ReboundRunScheduled", "ReboundRunLongIdle",
            "ReboundRunObservingSummaryFormat", "ReboundRunCompleteSummaryFormat",
            "ReboundRunReplacedSummaryFormat", "ObservationReplaced",
            "ClearBenefitLearning", "ClearBenefitLearningConfirm", "BenefitLearningCleared",
            "ClearLegacyBenefitLearning", "ClearLegacyBenefitLearningConfirmFormat", "LegacyBenefitLearningClearedFormat",
            "LearningActions", "ClearApplicationBenefitLearning",
            "ClearApplicationBenefitLearningConfirmFormat", "ApplicationBenefitLearningClearedFormat",
            "ClearBenefitLearningWithAnchorsConfirm", "ClearApplicationBenefitLearningWithAnchorsConfirmFormat",
            "ClearLearningAndAnchors", "KeepAnchorsAndClearLearning",
            "StableSuppressionLimitFormat", "StableSuppressionLimitHelpFormat",
            "StableSuppressionLimitUnavailableHelp",
            "StableReferenceAndLimitFormat", "StableReferenceHelpEnabledFormat",
            "StableAnchorAndLimitFormat", "StableAnchorSettings", "StableAnchorMode",
            "StableAnchorFormat", "StableUpperLimitFormat", "StableAnchorSummaryFormat",
            "StableAnchorSamplesRequiredFormat", "StableAnchorSettingsDisabled", "StableAnchorScopeUnavailable",
            "StableAnchorRecordExpired", "StableAnchorSettingsOffline", "StableAnchorSettingsHistoricalScope",
            "StableAnchorScopeFormat", "StableHistoricalUpperLimitFormat", "StableHistoricalAnchorSummaryFormat",
            "StableAnchorAdaptive", "StableAnchorFixed", "StableAnchorValue", "StableAnchorValueFormat",
            "StableAnchorFixedTooltip", "StableAnchorTrendUpFormat", "StableAnchorTrendDownFormat",
            "StableAnchorFixedSaved", "StableAnchorAdaptiveSaved",
            "StableHistoricalReferenceFormat", "StableReferenceInactiveScopeHelpFormat",
            "LearningStableSampleProgressFormat", "LearningStableSampleInactiveScopeFormat",
            "LearningStableSamplesHelpFormat", "LearningStableSamplesRolling",
            "LearningStableSamplesCollecting", "LearningStableSamplesHelpDisabledFormat",
            "LearningStableSamplePoolRolling", "LearningStableSamplePoolCollecting",
            "StableReferenceHelpDisabledFormat", "StableSessionAwaiting",
            "StableSessionTargetNotRunning", "StableSessionProvisionalFormat",
            "StableSessionConvergedFormat", "StableSessionExcluded", "StableSessionMultipleFormat",
            "LocalData", "SettingsAndCustomProfilesData", "BenefitLearningData",
            "OpenDataFolder", "ProgramDirectoryPathHelp", "OpenDataFolderFailureFormat",
            "LocalDataLoadWarningTitle", "SettingsLoadFailureFormat", "BenefitLearningLoadFailureFormat",
            "UltimateRiskTitle", "UltimateRiskMessage", "UltimateRiskDoNotRemind", "UltimateRiskConfirm",
            "RemoveProtection", "RemoveProtectionConfirmTitle", "RemoveProtectedGroupConfirmFormat", "RemoveProtectedPathConfirmFormat",
            "CloseButtonSetting", "CloseBehaviorAsk", "CloseBehaviorExit", "CloseBehaviorTray",
            "CloseDialogTitle", "CloseDialogMessage", "CloseDialogExit", "CloseDialogTray",
            "CloseDialogRemember", "FollowSystemTheme",
            "TrayMemoryUsageIcon", "TrayMemoryUsageIconDescription", "TrayMemoryUsageFormat",
            "BackToOverview"
        };

        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(text[key])));
        Assert.DoesNotContain("{0}", string.Format(text["PlanCandidatesFormat"], 3));
        Assert.DoesNotContain("{0}", string.Format(text["ForceTerminatePromptFormat"], 2));
        Assert.DoesNotContain("{0}", string.Format(text["RemoveProtectedGroupConfirmFormat"], "Editor", 2));
        Assert.DoesNotContain("{1}", string.Format(text["RemoveProtectedGroupConfirmFormat"], "Editor", 2));
        Assert.DoesNotContain("{0}", string.Format(text["RemoveProtectedPathConfirmFormat"], "Editor.exe"));
        Assert.DoesNotContain("{0}", string.Format(text["SessionUptimeMinutesFormat"], 12));
        Assert.DoesNotContain("{1}", string.Format(text["SessionUptimeHoursFormat"], 2, 5));
        Assert.DoesNotContain("{2}", string.Format(text["SessionUptimeDaysFormat"], 3, 4, 5));
        var stableProgress = string.Format(text["LearningStableSampleProgressFormat"], 2, 3, 4, 9);
        Assert.Contains(language == UiLanguage.ChineseSimplified ? "本次 2/3" : "This launch 2/3", stableProgress);
        Assert.Contains(language == UiLanguage.ChineseSimplified ? "总 4/9" : "total 4/9", stableProgress);
        var stableSamplesHelp = string.Format(
            text["LearningStableSamplesHelpFormat"],
            3, 3, text["LearningStableSamplesRolling"], 8, 9,
            text["LearningStableSamplePoolCollecting"], 15, "08-07 14:30", "08-07 14:25", 1, 2);
        Assert.Contains("3/3", stableSamplesHelp);
        Assert.Contains("8/9", stableSamplesHelp);
        Assert.Contains("08-07 14:30", stableSamplesHelp);
        Assert.Contains("08-07 14:25", stableSamplesHelp);
        var stableHelp = string.Format(
            text["StableReferenceHelpEnabledFormat"],
            1, 1, 4, "96 MB", 35, 3, 30, 1, "converged", 2, 3, 9, "08-06 15:45", 3, 15);
        Assert.Contains("4/9", stableHelp);
        Assert.Contains("08-06 15:45", stableHelp);
        Assert.Contains("15", text["RuntimeProgressPersistenceHelp"]);
        Assert.Contains(language == UiLanguage.ChineseSimplified ? "样本槽位" : "Sample slots",
            text["StableMaximumSamplesPerLaunch"]);
        Assert.Contains(language == UiLanguage.ChineseSimplified ? "复核" : "review",
            text["StableSampleIntervalMinutesHelp"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(language == UiLanguage.ChineseSimplified ? "实际运行时长" : "active runtime",
            text["RuntimeProgressPersistenceHelp"]);
        Assert.Contains("预计持续释放", UiTextCatalog.For(UiLanguage.ChineseSimplified)["ExpectedSustainedReleaseFormat"]);
        Assert.Contains("Estimated sustained release", UiTextCatalog.For(UiLanguage.English)["ExpectedSustainedReleaseFormat"]);
        Assert.Contains("分数越高越闲置", UiTextCatalog.For(UiLanguage.ChineseSimplified)["IdleRequirementHelp"]);
        Assert.Contains("内存占用", UiTextCatalog.For(UiLanguage.ChineseSimplified)["TriggerAvailablePercent"]);
        Assert.Contains("仅观察", UiTextCatalog.For(UiLanguage.ChineseSimplified)["ProcessDescription"]);
        Assert.Contains("observation-only", UiTextCatalog.For(UiLanguage.English)["ProcessDescription"]);
    }

    [Fact]
    public void TurboDescriptionExplainsBoostedSafetyChecks()
    {
        var description = UiTextCatalog.For(UiLanguage.ChineseSimplified)["TurboDescription"];

        Assert.StartsWith("默认加强策略", description);
        Assert.Contains("CPU/I/O", description);
        Assert.Contains("前台状态", description);
        Assert.Contains("用户保护", description);
        Assert.Contains("快速回弹", description);
    }

    [Fact]
    public void StableSuppressionUsesTheApprovedChineseOptionNames()
    {
        var text = UiTextCatalog.For(UiLanguage.ChineseSimplified);

        Assert.Equal("跟随当前优化档位", text["StableSuppressionFollowProfile"]);
        Assert.Equal("跟随当前优化档位（{0}）", text["StableSuppressionFollowCurrentFormat"]);
        Assert.Equal("减少重复优化（Lite）", text["StableSuppressionReduceRepeated"]);
        Assert.Equal("平衡（Turbo）", text["StableSuppressionBalanced"]);
        Assert.Equal("更快重新评估（Ultimate）", text["StableSuppressionFasterReevaluation"]);
        Assert.Equal("自定义", text["StableSuppressionCustom"]);
        Assert.Equal("关闭稳态抑制", text["StableSuppressionDisabled"]);
    }

    [Fact]
    public void SecondBackoffHelpDisclosesThirdReboundLongTermObservation()
    {
        var help = UiTextCatalog.For(UiLanguage.ChineseSimplified)["SecondBackoffHelp"];

        Assert.Contains("第三次快速回弹", help);
        Assert.Contains("长期观察", help);
        Assert.Contains("严重内存压力", help);
        Assert.Contains("1 小时", help);
    }

    [Fact]
    public void AutomaticBackoffStatusUsesCompactTableCopy()
    {
        var text = UiTextCatalog.For(UiLanguage.ChineseSimplified);

        Assert.Equal("已回避 · {0} 分钟", text["AutoBackoffMinutesFormat"]);
        Assert.Equal("已回避 · 长期观察中", text["AutoBackoffLongTerm"]);
        Assert.Equal("已检测前台 · 等待后台低活动", text["AutoBackoffLongTermAwaitingIdle"]);
        Assert.DoesNotContain("回避", text["CandidateAutomaticBackoff"]);
    }

    [Fact]
    public void SimplifiedChineseUltimateWarningUsesApprovedCopy()
    {
        var text = UiTextCatalog.For(UiLanguage.ChineseSimplified);

        Assert.Equal("该档位目前还在测试中，风险较高，不建议小白使用", text["UltimateRiskMessage"]);
        Assert.Contains("取消可见窗口等待、采用较短进程冷却", text["UltimateDescription"]);
        Assert.Contains("回弹退避", text["UltimateDescription"]);
    }
}
