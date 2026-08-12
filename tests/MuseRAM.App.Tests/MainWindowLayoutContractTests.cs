using System.Xml.Linq;

namespace MuseRAM.App.Tests;

[Trait("Category", "SourceContract")]
public sealed class MainWindowLayoutContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void BenefitLearningMigrationLogUsesTheCurrentSchemaVersion()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("BenefitLearningStore.CurrentSchemaVersion", code);
        Assert.DoesNotContain("Migrated benefit-learning.json to schema version 5.", code);
    }

    [Fact]
    public void NavigationUsesLucideOutlineGeometryResources()
    {
        var document = LoadDocument();
        var iconStyle = FindKeyedStyle(document, "NavIconStyle");

        Assert.Equal("Path", (string?)iconStyle.Attribute("TargetType"));
        Assert.Equal("{StaticResource NavIconPathStyle}", (string?)iconStyle.Attribute("BasedOn"));
        Assert.Equal(7, document.Descendants(Presentation + "Path").Count(element =>
            (string?)element.Attribute("Style") == "{StaticResource NavIconStyle}"));

        var resources = File.ReadAllText(IconResourcesFixturePath());
        Assert.Contains("x:Key=\"IconDashboard\"", resources);
        Assert.Contains("x:Key=\"IconShield\"", resources);
        Assert.Contains("x:Key=\"IconSettings\"", resources);
    }

    [Fact]
    public void HistoryAndRefreshUseDistinctOpenRingIcons()
    {
        var layout = File.ReadAllText(FixturePath());
        var resources = File.ReadAllText(IconResourcesFixturePath());

        Assert.Contains("x:Key=\"IconHistory\"", resources);
        Assert.Contains("M12,7 L12,12 L16,14", resources);
        Assert.Contains("x:Key=\"IconRefresh\"", resources);
        Assert.Contains("M21,3 L21,9 L15,9", resources);
        Assert.Contains("x:Name=\"HistoryNav\"", layout);
        Assert.Contains("Style=\"{StaticResource NavIconStyle}\" Data=\"{StaticResource IconHistory}\"", layout);
        Assert.DoesNotContain("Width=\"21\" Height=\"21\" Data=\"{StaticResource IconHistory}\"", layout);
        Assert.Equal(1, CountOccurrences(layout, "Data=\"{StaticResource IconHistory}\""));
        Assert.Equal(2, CountOccurrences(layout, "Data=\"{StaticResource IconRefresh}\""));
    }

    [Fact]
    public void PopupTriggersConsumeTheClickFollowingPressToClose()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("_suppressedPopupTriggerClicks.Add(button);", code);
        Assert.Contains("e.Handled = true;", code);
        Assert.Contains("ManagedPopupTrigger_OnPreviewMouseLeftButtonDown", code);
        Assert.Contains("var open = !popup.IsOpen;", code);
        Assert.Contains("CloseManagedPopups(popup);", code);
        Assert.Contains("popup.IsOpen = open;", code);
        Assert.Contains("e.Handled = true;", code);
        Assert.Equal(5, CountOccurrences(layout, "PreviewMouseLeftButtonDown=\"ManagedPopupTrigger_OnPreviewMouseLeftButtonDown\""));
        Assert.Equal(5, CountOccurrences(layout, "StaysOpen=\"True\""));
        Assert.True(CountOccurrences(code, "ConsumeSuppressedPopupTriggerClick(sender)") >= 5);
        Assert.Contains("var suppressHistoryButtonClick = false;", code);
        Assert.Contains("suppressHistoryButtonClick = true;", code);
        Assert.Contains("if (suppressHistoryButtonClick)", code);
        Assert.Contains("? PopupAnimation.Slide", code);
        Assert.Contains("x:Key=\"FadePopupStyle\"", layout);
        Assert.Contains("x:Name=\"SchedulePopup\" Style=\"{StaticResource FadePopupStyle}\"", layout);
        Assert.Contains("x:Name=\"CandidateModePopup\" Style=\"{StaticResource FadePopupStyle}\"", layout);
        Assert.Equal(5, CountOccurrences(layout, "Style=\"{StaticResource FadePopupStyle}\""));
    }

    [Fact]
    public void NavigationHasNamedSelectableEntries()
    {
        var document = LoadDocument();
        var names = document.Descendants(Presentation + "ToggleButton")
            .Select(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")))
            .Where(name => name is not null)
            .ToArray();

        Assert.Contains("OverviewNav", names);
        Assert.Contains("ProcessesNav", names);
        Assert.Contains("ProtectionNav", names);
        Assert.Contains("HistoryNav", names);
        Assert.Contains("CustomNav", names);
        Assert.Contains("SettingsNav", names);
    }

    [Fact]
    public void ApplicationRulesUseAChecklistIconDistinctFromSettings()
    {
        var document = LoadDocument();
        var applicationRules = document.Descendants(Presentation + "ToggleButton").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ApplicationRulesNav");
        var settings = document.Descendants(Presentation + "ToggleButton").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SettingsNav");

        Assert.Equal(
            "{StaticResource IconListChecks}",
            (string?)applicationRules.Element(Presentation + "Path")?.Attribute("Data"));
        Assert.Equal(
            "{StaticResource IconSettings}",
            (string?)settings.Element(Presentation + "Path")?.Attribute("Data"));
    }

    [Fact]
    public void ProgrammaticSelectionVisualsKeepDynamicThemeReferences()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var method = MethodBody(code, "private void ApplyPopupSelectionVisual", "private void MainWindow_OnDeactivated");

        Assert.Contains("SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, \"AccentSoftBrush\")", method);
        Assert.Contains("SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, \"AccentBrush\")", method);
        Assert.DoesNotContain("button.Background = (MediaBrush)FindResource", method);
        Assert.DoesNotContain("button.Foreground = (MediaBrush)FindResource", method);
    }

    [Fact]
    public void ProtectionIsAlwaysGroupedAndScopeIsManagedPerApplication()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("<ScrollViewer x:Name=\"ProtectedGroupsScrollViewer\"", text);
        Assert.Contains("<ItemsControl x:Name=\"ProtectedGroupsList\"", text);
        Assert.DoesNotContain("<ListBox x:Name=\"ProtectedGroupsList\"", text);
        Assert.Contains("IsChecked=\"{Binding IsExpanded, Mode=TwoWay}\"", text);
        Assert.Contains("ItemsSource=\"{Binding Executables}\"", text);
        Assert.Contains("{x:Static local:ApplicationProtectionState.EntireFamily}", text);
        Assert.Contains("x:Name=\"IndeterminateMark\"", text);
        Assert.Contains("<Trigger Property=\"IsChecked\" Value=\"{x:Null}\">", text);
        Assert.DoesNotContain("x:Name=\"ProtectRelatedProcessesCheckBox\"", text);
        Assert.DoesNotContain("x:Name=\"ProtectedFlatList\"", text);
        Assert.DoesNotContain("ProtectRelatedProcessesCheckBox_OnChanged", text);
    }

    [Fact]
    public void ProtectedGroupRefreshPreservesPixelScrollAndSkipsIdenticalContent()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("ProtectedGroupsEqual(", code);
        Assert.Contains("_state.ProtectedApplications, orderedGroups", code);
        Assert.Contains("ProtectedGroupsScrollViewer.VerticalOffset", code);
        Assert.Contains("ProtectedGroupsScrollViewer.ScrollToVerticalOffset", code);
    }

    [Fact]
    public void ProtectedGroupRefreshDoesNotReplaceRowsWhileRuleMenuIsOpen()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var refresh = MethodBody(
            code,
            "private void RefreshProtectedList()",
            "private ApplicationRuleDisplay CreateApplicationRuleDisplay");

        Assert.Equal(2, CountOccurrences(layout, "Opened=\"ApplicationRulePopup_OnOpened\""));
        Assert.Contains("_openApplicationRulePopups", code);
        Assert.Contains("_openApplicationRulePopups.Count > 0", refresh);
        Assert.Contains("ExpansionMotion.IsAnyAnimationActive", refresh);
    }

    [Fact]
    public void ProtectedGroupsShowApplicationRuleStateAndDiagnostics()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var pageStart = layout.IndexOf("<Grid x:Name=\"ProtectionPage\"", StringComparison.Ordinal);
        var pageEnd = layout.IndexOf("<Grid x:Name=\"HistoryPage\"", pageStart, StringComparison.Ordinal);
        Assert.True(pageStart >= 0 && pageEnd > pageStart);

        var page = layout[pageStart..pageEnd];
        Assert.Contains("Text=\"{Binding ApplicationRuleStatus}\"", page);
        Assert.Contains("Text=\"{Binding ApplicationRuleHistory}\"", page);
        Assert.Contains("Text=\"{Binding ApplicationRuleSkip}\"", page);
        Assert.Contains("ToolTip=\"{Binding ApplicationRuleDetail}\"", page);
        Assert.Contains("CreateApplicationRuleDisplay(", code);
        Assert.Contains("ApplyCompletedApplicationRuleOutcome(", code);
        Assert.Contains("LastRetainedBytes", code);
    }

    [Fact]
    public void ProtectionRemovalIsRowScopedAndConfirmed()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Key=\"RowDeleteButtonStyle\"", layout);
        Assert.Contains("Click=\"RemoveProtectedGroup_OnClick\"", layout);
        Assert.DoesNotContain("Click=\"RemoveProtectedPath_OnClick\"", layout);
        Assert.DoesNotContain("Click=\"RemoveProtected_OnClick\"", layout);
        Assert.Contains("group.RuleApplicationPaths", code);
        Assert.Contains("RemoveProtectedGroupConfirmFormat", code);
        Assert.Contains("RemoveProtectionRules(group.RuleApplicationPaths)", code);
        Assert.Contains("Style = (Style)FindResource(\"DangerButtonStyle\")", code);
        Assert.Contains("T(\"RemoveProtectionConfirmTitle\")", code);
        Assert.Contains("ApplyDialogTheme(dialog)", code);
        Assert.Contains("dialog.ShowDialog()", code);
    }

    [Fact]
    public void ProtectedExecutablesExpandToReadOnlyPidRows()
    {
        var text = File.ReadAllText(FixturePath());

        var start = text.IndexOf("<Grid x:Name=\"ProtectionPage\"", StringComparison.Ordinal);
        var end = text.IndexOf("<Grid x:Name=\"HistoryPage\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var page = text[start..end];
        Assert.Contains("ItemsSource=\"{Binding Executables}\"", page);
        Assert.Contains("Text=\"{Binding InstanceCount, Mode=OneWay}\"", page);
        Assert.Contains("Text=\"{Binding Memory}\"", page);
        Assert.Contains("ItemsSource=\"{Binding Processes}\"", page);
        Assert.Contains("IsChecked=\"{Binding IsExpanded, Mode=TwoWay}\"", page);
        Assert.Contains("IsEnabled=\"{Binding HasProcesses}\"", page);
        Assert.Contains("Text=\"{Binding Label}\"", page);
    }

    [Fact]
    public void ProtectedGroupExpanderUsesThemedNormalHoverAndDisabledStates()
    {
        var document = LoadDocument();
        var style = FindKeyedStyle(document, "ExpandButtonStyle");
        var commandStyle = FindKeyedStyle(document, "ExpandCommandButtonStyle");
        var text = style.ToString();

        Assert.Equal("ToggleButton", (string?)style.Attribute("TargetType"));
        Assert.Equal("Button", (string?)commandStyle.Attribute("TargetType"));
        Assert.Contains("{DynamicResource NavigationHoverBrush}", text);
        Assert.Contains("Property=\"IsEnabled\" Value=\"False\"", text);
        Assert.Contains("Property=\"Background\" Value=\"Transparent\"", text);
        Assert.Empty(document.Descendants(Presentation + "Button").Where(element =>
            (string?)element.Attribute("Style") == "{StaticResource ExpandButtonStyle}"));
        Assert.Equal(5, document.Descendants(Presentation + "Button").Count(element =>
            (string?)element.Attribute("Style") == "{StaticResource ExpandCommandButtonStyle}"));
        Assert.Equal(2, document.Descendants(Presentation + "ToggleButton").Count(element =>
            (string?)element.Attribute("Style") == "{StaticResource ExpandButtonStyle}"));
    }

    [Fact]
    public void SafetyTogglesAnimateBetweenStates()
    {
        var style = FindKeyedStyle(LoadDocument(), "SafetyToggleStyle").ToString();

        Assert.Contains("ThicknessAnimation", style);
        Assert.Contains("Duration=\"0:0:0.16\"", style);
        Assert.Contains("QuadraticEase", style);
    }

    [Fact]
    public void CustomPageOnlyEditsCopiedProfilesAndExposesSimpleAndAdvancedControls()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("x:Name=\"CustomPage\"", text);
        Assert.Contains("x:Name=\"CopyProfileButton\"", text);
        Assert.Contains("x:Name=\"CustomProfileNameTextBox\"", text);
        Assert.Contains("x:Name=\"AdvancedProfilePanel\" Visibility=\"Collapsed\"", text);
        Assert.Contains("x:Name=\"ShowBuiltInProfilesCheckBox\"", text);
        foreach (var key in new[]
        {
            "TriggerAvailablePercentHelp", "MaximumApplicationsHelp", "MinimumApplicationMemoryHelp",
            "VisibleWindowIdleDelayHelp", "ProcessCooldownHelp", "AutoCooldownHelp", "EarlyReboundThresholdHelp",
            "LateReboundThresholdHelp", "FirstBackoffHelp", "SecondBackoffHelp"
        })
        {
            Assert.Contains($"ToolTip=\"{{DynamicResource {key}}}\"", text);
        }
        Assert.DoesNotContain("x:Name=\"IdleScoreSlider\"", text);
        foreach (var key in new[]
        {
            "MinimumProcessMemoryHelp", "TriggerAvailableGiBHelp", "EarlyWindowSecondsHelp",
            "LateWindowSecondsHelp", "IgnoreMemoryPressureHelp", "AllowForegroundOptimizationHelp",
            "ActiveCpuThresholdHelp", "ActiveIoThresholdHelp",
            "AllowIndependentBackgroundProcessTrimHelp"
        })
        {
            Assert.Contains($"ToolTip=\"{{DynamicResource {key}}}\"", text);
        }
        Assert.DoesNotContain("ProtectGamingCheckBox", text);
    }

    [Fact]
    public void CustomProfilesExposeProfileSpecificVisibleWindowWait()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"VisibleWindowIdleDelaySlider\"", layout);
        Assert.Contains("<StackPanel Grid.Row=\"1\" Grid.Column=\"2\" Margin=\"0,12,0,0\">", layout);
        Assert.Contains("{DynamicResource VisibleWindowIdleDelay}", layout);
        Assert.Contains("VisibleWindowIdleDelay = TimeSpan.FromMinutes(VisibleWindowIdleDelaySlider.Value)", code);
        Assert.Contains("new SliderBounds(1, 40, 96, 1024, 45, 90, 5, 48, 18, 600, 90, 900, 3, 15, 2, 15, 1, 8)", code);
        Assert.Contains("new SliderBounds(2, 40, 2, 280, 20, 65, 10, 70, 5, 120, 30, 300, 1, 10, 4, 25, 2, 16)", code);
        Assert.Contains("new SliderBounds(7, 40, 2, 96, 5, 45, 1, 95, 1, 18, 15, 120, 0, 5, 8, 50, 4, 32)", code);
    }

    [Fact]
    public void CustomProfilesExposeActivityThresholdsAndIndependentBackgroundPolicy()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"ActiveCpuThresholdSlider\"", layout);
        Assert.Contains("x:Name=\"ActiveIoThresholdSlider\"", layout);
        Assert.Contains("x:Name=\"ActiveCpuThresholdSlider\" Style=\"{StaticResource EditorSliderStyle}\" Minimum=\"1\" Maximum=\"50\"", layout);
        Assert.Contains("x:Name=\"ActiveIoThresholdSlider\" Style=\"{StaticResource EditorSliderStyle}\" Minimum=\"1\" Maximum=\"32\"", layout);
        Assert.Contains("x:Name=\"AllowIndependentBackgroundProcessTrimCheckBox\"", layout);
        Assert.Contains("ActivityThresholdSlider_OnValueChanged", layout);
        Assert.DoesNotContain("(ActiveCpuThresholdSlider.Minimum, ActiveCpuThresholdSlider.Maximum)", code);
        Assert.DoesNotContain("(ActiveIoThresholdSlider.Minimum, ActiveIoThresholdSlider.Maximum)", code);
        Assert.Contains("_currentCustomProfileBounds?.MinActiveCpuPercent", code);
        Assert.Contains("_currentCustomProfileBounds?.MinActiveIoMiBPerSecond", code);
        Assert.Contains("AllowIndependentBackgroundProcessTrim = AllowIndependentBackgroundProcessTrimCheckBox.IsChecked == true", code);
    }

    [Fact]
    public void WindowUsesBuiltInUiFontsAndDisplayTextRendering()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("FontFamily=\"Segoe UI, Microsoft YaHei UI\"", text);
        Assert.Contains("TextOptions.TextFormattingMode=\"Display\"", text);
        Assert.Contains("TextOptions.TextRenderingMode=\"ClearType\"", text);
        Assert.DoesNotContain("Cascadia Mono", text);
        Assert.DoesNotContain("Segoe UI Variable Text", text);
    }

    [Fact]
    public void TooltipsSupportBothStringsAndExistingTextBlockContent()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("DataTemplate DataType=\"{x:Type sys:String}\"", text);
        Assert.Contains("<ToolTip><TextBlock Text=\"{DynamicResource EnhancedSafetyDescription}\"", text);
        Assert.DoesNotContain("<Setter Property=\"ContentTemplate\">", text);
    }

    [Fact]
    public void DataGridAndNavigationColorsUseThemeResources()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("Property=\"AlternatingRowBackground\" Value=\"{DynamicResource SurfaceBrush}\"", text);
        Assert.Contains("Property=\"AlternationCount\" Value=\"1\"", text);
        Assert.Contains("Background=\"{DynamicResource NavigationBrush}\"", text);
        Assert.DoesNotContain("AlternatingRowBackground=\"#0D1014\"", text);
    }

    [Fact]
    public void ThemeUsesNeutralSurfaceTokensAndARestrictedBrandAccent()
    {
        var text = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Key=\"SurfaceBrush\" Color=\"#111113\"", text);
        Assert.Contains("x:Key=\"BorderBrush\" Color=\"#27272A\"", text);
        Assert.Contains("x:Key=\"AccentBrush\" Color=\"#7C9CEB\"", text);
        Assert.Contains("SetBrush(\"AccentBrush\", light ? \"#4169B1\" : \"#7C9CEB\")", code);
        Assert.Contains("SetBrush(\"SuccessBrush\"", code);
        Assert.Contains("SetBrush(\"WarningBrush\"", code);
        Assert.Contains("SetBrush(\"UltimateBrush\"", code);
    }

    [Fact]
    public void InteractionMotionHonorsTheWindowsClientAnimationPreference()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Equal(System.Windows.SystemParameters.ClientAreaAnimation, MotionPolicy.Current.IsEnabled);
        Assert.Contains("MotionPolicy.Current", text);
        Assert.Contains("Duration=\"0:0:0.14\"", text);
        Assert.Contains("Duration=\"0:0:0.12\"", text);
    }

    [Fact]
    public void CandidatePanelExplainsThatOptimizationOnlyTrimsWorkingSet()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("{DynamicResource CandidateDescription}", text);
        Assert.Contains("{Binding CandidateSorting}", text);
        Assert.Contains("Binding=\"{Binding IdleStatus}\"", text);
        Assert.Contains("Text=\"{DynamicResource IdleStatusColumn}\"", text);
        Assert.Contains("CreateCandidateRow(candidate, protection, protectionContext, now)", File.ReadAllText(CodeFixturePath()));
        Assert.DoesNotContain("Click=\"Preview_OnClick\"", text);
    }

    [Fact]
    public void RunningApplicationsShowOneIdleStatusColumnAfterWorkingSet()
    {
        var layout = File.ReadAllText(FixturePath());
        var pageStart = layout.IndexOf("<Grid x:Name=\"ProcessesPage\"", StringComparison.Ordinal);
        Assert.True(pageStart >= 0);

        var pageEnd = layout.IndexOf("<Grid x:Name=\"ProtectionPage\"", pageStart, StringComparison.Ordinal);
        Assert.True(pageEnd > pageStart);

        var processesPage = layout[pageStart..pageEnd];
        var workingSetColumn = processesPage.IndexOf(
            "Binding=\"{Binding Memory}\"",
            StringComparison.Ordinal);
        var idleStatusColumn = processesPage.IndexOf(
            "Binding=\"{Binding IdleStatus}\"",
            StringComparison.Ordinal);

        Assert.True(workingSetColumn >= 0);
        Assert.True(idleStatusColumn > workingSetColumn);
        Assert.Contains("x:Name=\"ProcessesGrid\"", processesPage);
        Assert.Contains("IsReadOnly=\"True\"", processesPage);
        foreach (var key in new[] { "Application", "Pid", "CandidateMemory", "IdleStatusColumn", "ProtectionAndRetention", "Path" })
            Assert.Contains($"Text=\"{{DynamicResource {key}}}\"", processesPage);
        Assert.DoesNotContain("Binding=\"{Binding IdleScore}\"", processesPage);
        Assert.DoesNotContain("Text=\"{DynamicResource IdleScoreColumn}\"", processesPage);
        Assert.DoesNotContain("Binding=\"{Binding Activity}\"", processesPage);
        Assert.Equal(1, processesPage.Split(
            "HeaderStyle=\"{StaticResource CandidateCenterHeaderStyle}\" ElementStyle=\"{StaticResource CandidateCenterCellTextStyle}\"",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("DataGridTemplateColumn Width=\"110\"", processesPage);
        Assert.Contains("ContentTemplate=\"{StaticResource RetentionStatusIconTemplate}\"", processesPage);
        Assert.Contains("Text=\"{Binding Protection}\" Style=\"{StaticResource ProcessRetentionCellTextStyle}\"", processesPage);
        Assert.Equal(2, processesPage.Split(
            "HeaderStyle=\"{StaticResource CandidateLeftHeaderStyle}\" ElementStyle=\"{StaticResource CandidateLeftCellTextStyle}\"",
            StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void DeepReleaseIsSeparatedFromBenefitLearningControl()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("x:Name=\"DeepReleaseButton\" Grid.Row=\"3\" Style=\"{StaticResource DangerButtonStyle}\"", text);
        Assert.Contains("Height=\"30\" Padding=\"14,0\" Margin=\"0,12,0,0\"", text);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", text);
        Assert.Contains("x:Name=\"SettingsBenefitLearningStatus\"", text);
        Assert.DoesNotContain("<StackPanel VerticalAlignment=\"Center\"><TextBlock Text=\"{DynamicResource IntelligentCandidateSelection}\" FontWeight=\"SemiBold\"/><TextBlock Text=\"{Binding BenefitLearningStatus}\"", text);
    }

    [Fact]
    public void StableStateSuppressionUsesBuiltInAndNamedCustomProfiles()
    {
        var document = LoadDocument();
        var overview = FindNamedElement(document, "OverviewPage");
        var custom = FindNamedElement(document, "CustomPage");
        var modeBox = FindNamedElement(overview, "OverviewStableStateSuppressionModeBox");

        Assert.Equal("OverviewStableStateSuppressionModeBox_OnSelectionChanged", (string?)modeBox.Attribute("SelectionChanged"));
        Assert.Equal("Name", (string?)modeBox.Attribute("DisplayMemberPath"));
        Assert.DoesNotContain(custom.Descendants(), element =>
            ((string?)element.Attribute(Xaml + "Name"))?.Contains("StableStateSuppressionModeBox", StringComparison.Ordinal) == true);

        string[] expectedCustomElements =
        [
            "CustomOptimizationProfilesTabButton",
            "CustomStableSuppressionTabButton",
            "CustomStableSuppressionPanel",
            "StableSuppressionCatalogList",
            "CopyStableSuppressionProfileButton",
            "DeleteStableSuppressionProfileButton",
            "ShowBuiltInStableSuppressionProfilesCheckBox",
            "StableSuppressionProfileNameTextBox",
            "StableSuppressionEditorStateText",
            "StableSuppressionTemplatePanel",
            "SaveCustomStableSuppressionButton",
            "StableMinimumSamplesSlider",
            "StableRecordAgeDaysSlider",
            "StableRelativeMarginSlider",
            "StableAbsoluteMarginSlider",
            "StableObservationMinutesSlider",
            "StableSampleIntervalMinutesSlider",
            "StableMaximumSamplesPerLaunchSlider",
            "StableSamplePoolSlider",
            "StableMaximumWorkingSetSlider",
            "StableMaximumWorkingSetValueText",
            "StableMaximumWorkingSetUnlimitedCheckBox",
        ];
        Assert.All(expectedCustomElements, name => FindNamedElement(custom, name));

        var templateBox = FindNamedElement(custom, "StableSuppressionTemplateBox");
        Assert.Equal("StableSuppressionTemplateBox_OnSelectionChanged", (string?)templateBox.Attribute("SelectionChanged"));
        Assert.DoesNotContain(custom.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "ApplyStableSuppressionTemplateButton");
        Assert.DoesNotContain(custom.Descendants().Attributes("Click"), attribute =>
            attribute.Value == "StableSuppressionTemplateApply_OnClick");

        var customElements = custom.Descendants().ToList();
        var advancedPanel = FindNamedElement(custom, "AdvancedProfilePanel");
        var stablePanel = FindNamedElement(custom, "CustomStableSuppressionPanel");
        Assert.True(customElements.IndexOf(stablePanel) > customElements.IndexOf(advancedPanel));
        Assert.DoesNotContain(advancedPanel.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "StableMinimumSamplesSlider");
        var stableSliders = stablePanel.Descendants(Presentation + "Slider").ToArray();
        Assert.Equal(9, stableSliders.Length);
        Assert.All(stableSliders, slider =>
        {
            Assert.Equal("{StaticResource EditorSliderStyle}", (string?)slider.Attribute("Style"));
            Assert.Equal("StableSuppressionDraftSlider_OnValueChanged", (string?)slider.Attribute("ValueChanged"));
        });
        Assert.Contains(overview.Descendants(Presentation + "RowDefinition"), element =>
            (string?)element.Attribute("Height") == "1.15*");
    }

    [Fact]
    public void StableWorkingSetUnlimitedStateUsesExplicitTextAndPreservesUnlimitedValue()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var load = MethodBody(
            code,
            "private void LoadCustomStableSuppressionEditor",
            "private void UpdateStableMaximumWorkingSetValueText");
        var display = MethodBody(
            code,
            "private void UpdateStableMaximumWorkingSetValueText",
            "private void SaveCustomStableSuppression_OnClick");
        var read = MethodBody(
            code,
            "private bool TryReadStableSuppressionDraft",
            "private void LoadCustomProfileEditor");

        Assert.Contains("x:Name=\"StableMaximumWorkingSetValueText\"", layout);
        Assert.DoesNotContain("Binding ElementName=StableMaximumWorkingSetSlider", layout);
        Assert.Contains("StableMaximumWorkingSetUnlimitedValue", display);
        Assert.Contains("UpdateStableMaximumWorkingSetValueText();", load);
        Assert.Contains("? long.MaxValue", read);
    }

    [Fact]
    public void StableObservationUsesIndependentSeverePressureDetectionWithoutUltimateBypass()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var refresh = MethodBody(
            code,
            "private async Task<bool> RefreshSnapshotAsync",
            "private bool CanRunUnattendedOptimization");

        Assert.Contains("IsSevereMemoryPressureRegardlessOfOptimizationOverride", refresh);
        Assert.Contains("ObserveNaturalStableStates", refresh);
        Assert.Contains("naturalStableSettings,", refresh);
        Assert.Contains("severeMemoryPressure,", refresh);
        Assert.DoesNotContain("OptimizationProfile.Ultimate", refresh);
    }

    [Fact]
    public void DeepReleaseRevalidatesInsideTheSharedBusyWindow()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var deepRelease = code.IndexOf("private async void DeepRelease_OnClick", StringComparison.Ordinal);
        var setBusy = code.IndexOf("SetBusyState(true);", deepRelease, StringComparison.Ordinal);
        var waitForFreshSnapshot = code.IndexOf(
            "if (!await RefreshSnapshotAsync(waitForCurrentRefresh: true)) return;",
            setBusy,
            StringComparison.Ordinal);
        var executionRecheck = code.IndexOf(
            "DeepReleaseExecutionSafetyPolicy.FilterSafeCandidates",
            waitForFreshSnapshot,
            StringComparison.Ordinal);
        var closeApplications = code.IndexOf("await CloseApplicationsAsync(", executionRecheck, StringComparison.Ordinal);
        var finallyBlock = code.IndexOf("finally", closeApplications, StringComparison.Ordinal);
        var clearBusy = code.IndexOf("SetBusyState(false);", finallyBlock, StringComparison.Ordinal);

        Assert.True(deepRelease >= 0);
        Assert.True(setBusy > deepRelease);
        Assert.True(waitForFreshSnapshot > setBusy);
        Assert.True(executionRecheck > waitForFreshSnapshot);
        Assert.True(closeApplications > executionRecheck);
        Assert.True(finallyBlock > closeApplications);
        Assert.True(clearBusy > finallyBlock);
        Assert.Contains("if (!_state.IsBusy) await RefreshSnapshotAsync();", code);
        var visibleCollectionsUpdate = code.IndexOf("UpdateVisibleProcessCollections();", StringComparison.Ordinal);
        var unattendedBusyGuard = code.IndexOf("if (!_state.IsBusy)", visibleCollectionsUpdate, StringComparison.Ordinal);
        var scheduledDue = code.IndexOf("var scheduledOptimizationDue", unattendedBusyGuard, StringComparison.Ordinal);
        Assert.True(unattendedBusyGuard > visibleCollectionsUpdate);
        Assert.True(scheduledDue > unattendedBusyGuard);
        Assert.True(CountOccurrences(code, "TryOpenSafeDeepReleaseProcess(target)") >= 3);
        Assert.Contains("await Task.Run(() =>", code[closeApplications..]);
    }

    [Fact]
    public void DeepReleaseUsesOneEnhancedSafetySnapshot()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var handler = MethodBody(code, "private async void DeepRelease_OnClick", "private IReadOnlyList<DeepReleaseCandidate>? ShowDeepReleaseDialog");
        var execution = MethodBody(code, "private async Task CloseApplicationsAsync", "private static Process? TryOpenSafeDeepReleaseProcess");

        Assert.Contains("var enhancedSafety = _settings.EnhancedSafety;", handler);
        Assert.Contains("selectedServices,", handler);
        Assert.Contains("enhancedSafety);", handler);
        Assert.Contains("bool enhancedSafety", execution);
        Assert.Contains("DeepReleaseGracePeriod(enhancedSafety)", execution);
        Assert.Contains("RequiresForceTerminationConfirmation(enhancedSafety)", execution);
        Assert.DoesNotContain("_settings.EnhancedSafety", execution);
    }

    [Fact]
    public void OverviewTopRowHasEnoughHeightForOptimizationControls()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("<RowDefinition Height=\"1.15*\"/><RowDefinition Height=\"16\"/><RowDefinition Height=\"0.85*\"/>", text);
        Assert.Contains("x:Name=\"ScheduleMenuButton\" Grid.Column=\"2\" Tag=\"Right\" Style=\"{StaticResource PrimarySplitButtonStyle}\" Padding=\"0\"", text);
        Assert.Contains("x:Name=\"OptimizeNowButton\" Tag=\"Left\" Style=\"{StaticResource PrimarySplitButtonStyle}\"", text);
        Assert.Contains("x:Key=\"PopupTriggerButtonStyle\"", text);
        Assert.Contains("x:Key=\"PrimaryPopupTriggerButtonStyle\"", text);
        Assert.Contains("x:Key=\"PrimarySplitButtonStyle\"", text);
        Assert.Contains("Text=\"{DynamicResource SafeCandidates}\" FontSize=\"17\" FontWeight=\"SemiBold\" VerticalAlignment=\"Center\" ToolTip=\"{DynamicResource CandidateDescription}\"", text);
        Assert.DoesNotContain("Grid.Row=\"2\" Grid.ColumnSpan=\"3\" Text=\"{DynamicResource CandidateDescription}\"", text);
        Assert.Contains("ItemsSource=\"{Binding Candidates}\" MaxHeight=\"280\"", text);
        Assert.Contains("DataGridTemplateColumn Width=\"1.55*\" MinWidth=\"120\" HeaderStyle=\"{StaticResource CandidateLeftHeaderStyle}\"", text);
        Assert.Contains("Binding=\"{Binding AutoOptimizationStatus}\" Width=\"1.5*\" MinWidth=\"120\"", text);
        Assert.Contains("x:Name=\"OverviewStableSuppressionRow\" Margin=\"0,8,0,0\" Background=\"Transparent\" BorderThickness=\"0\" Padding=\"0\" Height=\"38\"", text);
    }

    [Fact]
    public void AutomaticOptimizationOnlyEntersBusyStateAfterFindingWork()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var method = MethodBody(
            code,
            "private async Task RunOptimizationAsync(",
            "private string FormatSelectedApplicationExclusion(");
        var unattendedMode = method.IndexOf("var unattended = !manual || scheduled;", StringComparison.Ordinal);
        Assert.True(unattendedMode >= 0);
        var manualBusy = method.IndexOf("if (!unattended)", unattendedMode, StringComparison.Ordinal);
        Assert.True(manualBusy > unattendedMode);
        var noWork = method.IndexOf("if (!plan.ShouldRun)", manualBusy, StringComparison.Ordinal);
        Assert.True(noWork > manualBusy);
        var deferredBusy = method.IndexOf("if (!busyStateEntered)", noWork, StringComparison.Ordinal);
        Assert.True(deferredBusy > noWork);
        var execution = method.IndexOf("executionStarted = Stopwatch.GetTimestamp();", deferredBusy, StringComparison.Ordinal);

        Assert.True(execution > deferredBusy);
        Assert.Contains("if (busyStateEntered) SetBusyState(false);", method);
    }

    [Fact]
    public void ProtectionSuggestionsUseAHeaderLevelTextOnlyShimmer()
    {
        var layout = File.ReadAllText(FixturePath());
        var overviewStart = layout.IndexOf("<Grid x:Name=\"OverviewPage\"", StringComparison.Ordinal);
        var settingsStart = layout.IndexOf("<Grid x:Name=\"SettingsPage\"", StringComparison.Ordinal);
        Assert.True(overviewStart >= 0 && settingsStart > overviewStart);

        var overview = layout[overviewStart..settingsStart];
        var settings = layout[settingsStart..];
        Assert.Contains("x:Name=\"ProtectionSuggestionButton\" Grid.Column=\"1\" Style=\"{StaticResource ProtectionSuggestionButtonStyle}\"", overview);
        Assert.Contains("x:Name=\"ProtectionSuggestionShimmerTransform\" X=\"-1.2\"", overview);
        Assert.Contains("FontWeight=\"SemiBold\" TextTrimming=\"CharacterEllipsis\"", overview);
        Assert.DoesNotContain("x:Name=\"ProtectionSuggestionButton\"", settings);

        var style = FindKeyedStyle(LoadDocument(), "ProtectionSuggestionButtonStyle").ToString();
        Assert.Contains("Property=\"Background\" Value=\"Transparent\"", style);
        Assert.Contains("Property=\"BorderThickness\" Value=\"0\"", style);
        Assert.Contains("Property=\"FontWeight\" Value=\"SemiBold\"", style);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource TextBrush}\"", style);
    }

    [Fact]
    public void OverviewExposesApplicationReboundDetailsWithoutAddingAnotherVerticalRow()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"ReboundDetailsButton\"", layout);
        Assert.Contains("Text=\"{Binding ReboundSummary}\"", layout);
        Assert.Contains("IsEnabled=\"{Binding HasReboundDetails}\"", layout);
        Assert.Contains("private void ReboundDetails_OnClick", code);
        Assert.Contains("NavigateToHistoryAnalysis(\"Rebound\")", code);
        Assert.Contains("ApplicationReboundDetailRow", code);
        Assert.Contains("ReboundDetailsDisclaimer", code);
        Assert.Contains("ReboundRunObservingSummaryFormat", code);
        Assert.Contains("ReboundRunCompleteSummaryFormat", code);
        Assert.Contains("_applicationReboundDetailTracker.StartedAt", code);
        Assert.Contains("ToolTip=\"{DynamicResource AutoOptimizationStatusHelp}\"", layout);
    }

    [Fact]
    public void HistoryAnalysisHostsPersistentReboundNavigationAndReadOnlyDetails()
    {
        var document = LoadDocument();
        var code = File.ReadAllText(CodeFixturePath());
        var history = FindNamedElement(document, "HistoryPage");
        var navigation = MethodBody(code, "private void Nav_OnClick", "private void SelectNavigation");
        var click = MethodBody(code, "private void ReboundDetails_OnClick", "private string ReboundStateText");

        FindNamedElement(history, "ActivityHistoryPanel");
        FindNamedElement(history, "BenefitLearningPanel");
        var reboundPanel = FindNamedElement(history, "ReboundHistoryPanel");
        var reboundGrid = FindNamedElement(reboundPanel, "ReboundAnalysisGrid");
        var reboundRuns = FindNamedElement(reboundPanel, "ReboundRunsList");
        Assert.Equal("{Binding ReboundRuns}", (string?)reboundRuns.Attribute("ItemsSource"));

        var limitTags = reboundPanel.Descendants(Presentation + "ToggleButton")
            .Where(element => (string?)element.Attribute("Style") == "{StaticResource AnalysisFilterButtonStyle}")
            .Select(element => (string?)element.Attribute("Tag"))
            .ToArray();
        Assert.Equal(["5", "10", "20", "0"], limitTags);
        Assert.Equal("{StaticResource NonSelectableDataGridRowStyle}", (string?)reboundGrid.Attribute("RowStyle"));

        var columns = reboundGrid.Descendants(Presentation + "DataGridTextColumn").ToArray();
        Assert.Equal(5, columns.Length);
        var expectedHeaderResources = new[]
        {
            "{DynamicResource Application}",
            "{DynamicResource InitialTrim}",
            "{DynamicResource RegainedWorkingSet}",
            "{DynamicResource ApplicationReboundRate}",
            "{DynamicResource ObservationStatus}"
        };
        Assert.Equal(expectedHeaderResources, columns.Select(column =>
            (string?)column
                .Element(Presentation + "DataGridTextColumn.Header")?
                .Element(Presentation + "TextBlock")?
                .Attribute("Text")));
        Assert.Equal("{Binding Application}", (string?)columns[0].Attribute("Binding"));
        Assert.Equal("*", (string?)columns[0].Attribute("Width"));
        Assert.Equal("{StaticResource CandidateLeftHeaderStyle}", (string?)columns[0].Attribute("HeaderStyle"));
        Assert.Equal("{StaticResource CandidateLeftCellTextStyle}", (string?)columns[0].Attribute("ElementStyle"));
        Assert.All(columns[1..], column =>
        {
            Assert.Equal("{StaticResource CandidateCenterHeaderStyle}", (string?)column.Attribute("HeaderStyle"));
            Assert.Equal("{StaticResource CandidateCenterCellTextStyle}", (string?)column.Attribute("ElementStyle"));
        });
        Assert.Contains("if (page == HistoryPage) _historyAnalysisTab = \"Activity\";", navigation);
        Assert.True(
            navigation.IndexOf("_historyAnalysisTab = \"Activity\";", StringComparison.Ordinal) <
            navigation.IndexOf("SelectNavigation(page, title);", StringComparison.Ordinal));
        Assert.Contains("_selectedReboundRunSequence = _reboundRunHistory[0].Sequence;", click);
        Assert.True(
            click.IndexOf("_selectedReboundRunSequence = _reboundRunHistory[0].Sequence;", StringComparison.Ordinal) <
            click.IndexOf("NavigateToHistoryAnalysis(\"Rebound\")", StringComparison.Ordinal));
        Assert.Contains("NavigateToHistoryAnalysis(\"Rebound\")", click);
        Assert.Contains("SynchronizeCollection(_state.ReboundDetails", code);
        var back = FindNamedElement(reboundPanel, "ReboundHistoryBackButton");
        Assert.Equal("2", (string?)back.Attribute("Grid.Column"));
        Assert.Equal("{DynamicResource BackToOverview}", (string?)back.Attribute("ToolTip"));
        Assert.Equal("ReboundHistoryBack_OnClick", (string?)back.Attribute("Click"));
        var backIcon = back.Element(Presentation + "Path");
        Assert.NotNull(backIcon);
        Assert.Equal("1.5", (string?)backIcon.Attribute("StrokeThickness"));
        Assert.Equal("Round", (string?)backIcon.Attribute("StrokeLineJoin"));
        Assert.Equal(
            "M 7,3 L 3,7 L 7,11 M 3,7 H 14 C 18,7 21,10 21,14 C 21,18 18,21 14,21 H 7",
            (string?)backIcon.Attribute("Data"));
        Assert.Null(back.Element(Presentation + "TextBlock"));
        Assert.Contains("SelectNavigation(OverviewPage, T(\"Overview\"))", code);
    }

    [Fact]
    public void TrayMemoryUsageIconIsOptionalAndReusesTheExistingMemoryRefresh()
    {
        var document = LoadDocument();
        var code = File.ReadAllText(CodeFixturePath());
        var toggle = FindNamedElement(document, "TrayMemoryUsageIconCheckBox");
        var refresh = MethodBody(code, "private void RefreshMemoryMetrics", "private void UpdateMemoryMetricsIfDue");
        var handler = MethodBody(
            code,
            "private void TrayMemoryUsageIconCheckBox_OnChanged",
            "private void DiagnosticDataCollectionCheckBox_OnChanged");

        Assert.Equal("TrayMemoryUsageIconCheckBox_OnChanged", (string?)toggle.Attribute("Checked"));
        Assert.Equal("TrayMemoryUsageIconCheckBox_OnChanged", (string?)toggle.Attribute("Unchecked"));
        Assert.Contains("TrayMemoryUsageIconCheckBox.IsChecked = _settings.ShowMemoryUsageInTrayIcon", code);
        Assert.Contains("UpdateTrayMemoryIcon(memory)", refresh);
        Assert.Contains("settings.ShowMemoryUsageInTrayIcon = requested", handler);
        Assert.Contains("UpdateTrayMemoryIcon()", handler);
        Assert.Contains("_trayMemoryIconController.Dispose()", code);
    }

    [Fact]
    public void BenefitLearningUsesComparableHorizontalBarsWithoutClaimingATimeSeries()
    {
        var document = LoadDocument();
        var code = File.ReadAllText(CodeFixturePath());
        var panel = FindNamedElement(document, "BenefitLearningPanel");
        var rows = panel.Descendants(Presentation + "ItemsControl").Single(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding BenefitLearningRows}");

        var barValues = rows.Descendants(Presentation + "ProgressBar")
            .Select(element => (string?)element.Attribute("Value"))
            .ToArray();
        Assert.Equal(["{Binding WorkingSetPercent}", "{Binding SustainedReleasePercent}"], barValues);
        Assert.Contains(panel.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{DynamicResource BenefitLearningDataScopeNotice}");
        var clearButton = rows.Descendants(Presentation + "Button").Single(element =>
            (string?)element.Attribute("Click") == "ClearApplicationBenefitLearning_OnClick");
        Assert.Equal("{Binding}", (string?)clearButton.Attribute("Tag"));
        Assert.Equal("{DynamicResource ClearApplicationBenefitLearning}",
            (string?)clearButton.Attribute("ToolTip"));
        Assert.Equal("5", (string?)clearButton.Attribute("Grid.Column"));
        var stableAnchor = rows.Descendants(Presentation + "TextBlock").Single(element =>
            (string?)element.Attribute("Text") == "{Binding StableAnchor}");
        var stableLimit = rows.Descendants(Presentation + "TextBlock").Single(element =>
            (string?)element.Attribute("Text") == "{Binding StableUpperLimit}");
        var workingSetSummary = FindNamedElement(rows, "PostOptimizationWorkingSetSummary");
        var summaryScroller = FindNamedElement(rows, "StableAnchorSummaryScroller");
        var anchorSummary = FindNamedElement(summaryScroller, "StableAnchorSummary");
        Assert.Equal("{Binding StableAnchorSummaryHelp}", (string?)stableAnchor.Attribute("ToolTip"));
        Assert.Equal("{Binding StableAnchorSummaryHelp}", (string?)stableLimit.Attribute("ToolTip"));
        Assert.Same(anchorSummary, stableAnchor.Parent);
        Assert.Equal(
            ["{Binding StableAnchor}", "\uE72E", "{Binding StableTrendGlyph}", " · ", "{Binding StableUpperLimit}"],
            anchorSummary.Elements(Presentation + "TextBlock")
                .Select(element => (string?)element.Attribute("Text")));
        Assert.Equal("Hidden", (string?)summaryScroller.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)summaryScroller.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("0", (string?)workingSetSummary.Attribute("Grid.Column"));
        Assert.Equal("2", (string?)summaryScroller.Attribute("Grid.Column"));
        Assert.Same(workingSetSummary.Parent, summaryScroller.Parent);
        Assert.DoesNotContain(workingSetSummary, summaryScroller.Descendants());
        Assert.Equal("Right", (string?)anchorSummary.Attribute("HorizontalAlignment"));
        Assert.Equal("StableAnchorSummaryScroller_OnMouseEnter", (string?)summaryScroller.Attribute("MouseEnter"));
        Assert.Equal("StableAnchorSummaryScroller_OnMouseLeave", (string?)summaryScroller.Attribute("MouseLeave"));
        Assert.Equal("StableAnchorSummaryScroller_OnUnloaded", (string?)summaryScroller.Attribute("Unloaded"));
        var releaseSummary = FindNamedElement(rows, "SustainedReleaseSummary");
        Assert.Equal(Presentation + "StackPanel", releaseSummary.Name);
        Assert.Equal("Horizontal", (string?)releaseSummary.Attribute("Orientation"));
        Assert.Equal("0,0,10,0", (string?)releaseSummary.Parent!.Attribute("Margin"));
        Assert.Equal(
            ["{DynamicResource SustainedRelease}", " · ", "{Binding SustainedRelease}"],
            releaseSummary.Elements(Presentation + "TextBlock")
                .Select(element => (string?)element.Attribute("Text")));
        var benefitSamples = rows.Descendants(Presentation + "TextBlock").Single(element =>
            (string?)element.Attribute("Text") == "{Binding BenefitSamples}");
        var stableSamples = rows.Descendants(Presentation + "TextBlock").Single(element =>
            (string?)element.Attribute("Text") == "{Binding StableSamples}");
        Assert.Equal("Wrap", (string?)benefitSamples.Attribute("TextWrapping"));
        Assert.Equal("Wrap", (string?)stableSamples.Attribute("TextWrapping"));
        Assert.Equal("3", (string?)benefitSamples.Parent!.Attribute("Grid.Column"));
        Assert.Equal("{Binding StableSamplesHelp}", (string?)benefitSamples.Parent.Attribute("ToolTip"));
        Assert.Equal(6, rows.Descendants(Presentation + "DataTemplate")
            .Single()
            .Descendants(Presentation + "Grid")
            .First()
            .Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements(Presentation + "ColumnDefinition")
            .Count());
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(Xaml + "Name") == "BenefitLearningGrid");

        var barStyle = FindKeyedStyle(document, "AnalysisBarStyle");
        Assert.Contains(barStyle.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "PART_Track");
        Assert.Contains(barStyle.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "PART_Indicator");
        Assert.Contains("var scaleBytes = Math.Max(", code);
        Assert.Contains("metric.WorkingSetBytes / (double)scaleBytes", code);
        Assert.Contains("metric.SustainedReleaseBytes / (double)scaleBytes", code);
        Assert.Contains("StableStateSuppressionPolicy.StableReferenceBytes", code);
        Assert.Contains("StableAnchorSummaryFormat", code);
        Assert.Contains("StableCandidateSummary(group.Key, stableValidationMinutes)", code);
        var refresh = MethodBody(
            code,
            "private void RefreshBenefitLearningAnalysis",
            "private string ResolveLearningApplicationName");
        Assert.Contains(".Where(record => record.ValidSampleCount > 0)", refresh);
        Assert.Contains("StableStateSuppressionPolicy.SuppressionLimitBytes", refresh);
        Assert.Contains("calculatedStableLimit =", refresh);
        Assert.Contains("effectiveAnchor.Value", refresh);
        Assert.DoesNotContain("StableReferenceAndProvisionalLimitFormat", refresh);
        Assert.Contains("StableReferenceHelpEnabledFormat", refresh);
        Assert.Contains("StableReferenceHelpDisabledFormat", refresh);
        Assert.Contains("displayedStableRecord = familyStableRecord ?? familyStableRecords.FirstOrDefault()", refresh);
        Assert.Contains("LearningStableSampleInactiveScopeFormat", refresh);
        Assert.Contains("LearningStableHistoricalScopesFormat", refresh);
        Assert.Contains("historicalStableRecords", refresh);
        Assert.Contains("stableSnapshotsByScope", refresh);
        Assert.Contains("_applicationBackoffTracker.FamilyStableLearningRecords", refresh);
        Assert.Contains("NaturalStableScopeRequests(stableSnapshotObservedAt)", refresh);
        Assert.Contains("LastStableLaunchSignature", refresh);
        Assert.Contains("StableLastObservedAt", refresh);
        Assert.Contains("LearningStableSamplesHelpFormat", refresh);
        Assert.Contains("StableAnchorLearningPolicy.AcceptedSampleCountForLaunch", refresh);
    }

    [Fact]
    public void StableAnchorEditorKeepsTheRangeOnSliderEndsAndMovesOneValueLabelWithTheThumb()
    {
        var document = LoadDocument();
        var code = File.ReadAllText(CodeFixturePath());
        var popup = FindNamedElement(document, "StableAnchorPopup");
        var slider = FindNamedElement(popup, "StableAnchorSlider");
        var valueLabel = FindNamedElement(popup, "StableAnchorValueLabel");
        var minimumLabel = FindNamedElement(popup, "StableAnchorMinimumLabel");
        var maximumLabel = FindNamedElement(popup, "StableAnchorMaximumLabel");
        var modeStyle = FindKeyedStyle(document, "StableAnchorModeButtonStyle");

        Assert.Equal("{StaticResource EditorSliderStyle}", (string?)slider.Attribute("Style"));
        Assert.Equal("StableAnchorSlider_OnValueChanged", (string?)slider.Attribute("ValueChanged"));
        Assert.Equal("Center", (string?)valueLabel.Attribute("TextAlignment"));
        Assert.Equal("Left", (string?)minimumLabel.Attribute("HorizontalAlignment"));
        Assert.Equal("Right", (string?)maximumLabel.Attribute("HorizontalAlignment"));
        Assert.Empty(popup.Descendants(Presentation + "TextBox"));
        Assert.DoesNotContain(popup.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") is "参考区间" or "Reference range");
        AssertStyleSetter(modeStyle, "MinWidth", "68");
        AssertStyleSetter(modeStyle, "Width", "Auto");
        Assert.Contains("Canvas.SetLeft", code);
        Assert.Contains("thumbCenter - labelWidth / 2d", code);
        Assert.Contains("StableAnchorSlider.IsEnabled = fixedMode", code);
        Assert.Contains("if (fixedMode && !currentRow.HasAnchorReferenceRange", code);
        Assert.Contains("currentRow.AnchorMinimumBytes", code);
        Assert.Contains("currentRow.AnchorMaximumBytes", code);
        Assert.Contains("!_stableAnchorValueChanged", code);
        Assert.Contains("StableAnchorLowerLimitConfirmFormat", code);
        Assert.Contains("Math.Clamp", code);
        Assert.Contains("Math.Ceiling(anchorMinimum / (double)mib)", code);
        Assert.Contains("Math.Floor(anchorMaximum / (double)mib)", code);
        Assert.DoesNotContain("row.AnchorMinimumMiB + 1d", code);
    }

    [Fact]
    public void AdaptiveAnchorEditorCanResetOnlyItsStableLearningScope()
    {
        var document = LoadDocument();
        var code = File.ReadAllText(CodeFixturePath());
        var popup = FindNamedElement(document, "StableAnchorPopup");
        var resetButton = FindNamedElement(popup, "StableAnchorResetButton");
        var handler = MethodBody(
            code,
            "private void StableAnchorResetLearning_OnClick",
            "private void StableAnchorApply_OnClick");

        Assert.Equal("{DynamicResource StableAnchorResetLearning}", (string?)resetButton.Attribute("Content"));
        Assert.Equal("StableAnchorResetLearning_OnClick", (string?)resetButton.Attribute("Click"));
        Assert.Contains("StableAnchorAdaptiveButton.IsChecked != true", handler);
        Assert.Contains("ResetStableAnchorLearning(row.ScopeKey)", handler);
        Assert.Contains("SaveBenefitLearning()", handler);
        Assert.Contains("UpdatePreviewRows()", handler);
        Assert.DoesNotContain("RemoveLearningForFamily", handler);
        Assert.DoesNotContain("ClearLearning()", handler);
    }

    [Fact]
    public void StableAnchorEntryIsDiscoverableBesideTheNameAndEnabledAfterEnoughSamples()
    {
        var document = LoadDocument();
        var code = File.ReadAllText(CodeFixturePath());
        var cell = FindNamedElement(document, "StableAnchorApplicationCell");
        var button = FindNamedElement(cell, "StableAnchorSettingsButton");
        var style = FindKeyedStyle(document, "StableAnchorSettingsButtonStyle");

        Assert.Equal(Presentation + "StackPanel", cell.Name);
        Assert.Equal("Horizontal", (string?)cell.Attribute("Orientation"));
        Assert.Equal("Left", (string?)cell.Attribute("HorizontalAlignment"));
        Assert.Null(cell.Attribute("ToolTip"));
        Assert.Equal("{StaticResource StableAnchorSettingsButtonStyle}", (string?)button.Attribute("Style"));
        Assert.Equal("{Binding CanConfigureAnchor}", (string?)button.Attribute("IsEnabled"));
        Assert.Equal("{Binding StableAnchorSettingsHelp}", (string?)button.Attribute("ToolTip"));
        Assert.Equal("True", (string?)button.Attribute("ToolTipService.ShowOnDisabled"));
        Assert.Equal("Center", (string?)button.Attribute("VerticalAlignment"));
        AssertStyleSetter(style, "Width", "28");
        AssertStyleSetter(style, "Opacity", "0.62");
        Assert.Contains("CanConfigureAnchor = canConfigureAnchor", code);
        Assert.Contains("var canConfigureAnchor = displayedScopeConfigurable;", code);
        Assert.DoesNotContain("anchorSetting is not null || displayedScopeConfigurable", code);
        Assert.Contains("StableAnchorSettingsOffline", code);
        Assert.Contains("StableAnchorSettingsHistoricalScope", code);
        Assert.Contains("StableHistoricalUpperLimitFormat", code);
        Assert.Contains("ResolveLearningApplicationName(group.Key, records, displayedStableRecord)", code);
        Assert.Contains("StableAnchorLearningPolicy.ReferenceRange", code);
        Assert.DoesNotContain("largestConvergedRange", code);
        Assert.DoesNotContain("$\"{anchorSummaryHelp}\\n\\n", code);
    }

    [Fact]
    public void StableAnchorSettingsButtonClosesItsPopupWithoutReopening()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("private bool TryClosePopupFromTrigger", code);
        Assert.Contains("ReferenceEquals(StableAnchorPopup.PlacementTarget, button) ? StableAnchorPopup", code);
        Assert.Contains("popup.IsOpen = false;", code);
        Assert.Contains("_suppressedPopupTriggerClicks.Add(button);", code);
        Assert.Contains("ConsumeSuppressedPopupTriggerClick(sender)", code);
    }

    [Fact]
    public void OnlyLongStableAnchorSummaryAutoScrollsWhenClippedAndHovered()
    {
        var document = LoadDocument();
        var code = File.ReadAllText(CodeFixturePath());
        var workingSetSummary = FindNamedElement(document, "PostOptimizationWorkingSetSummary");
        var scroller = FindNamedElement(document, "StableAnchorSummaryScroller");
        var handler = MethodBody(
            code,
            "private void StableAnchorSummaryScroller_OnMouseEnter",
            "private void StableAnchorSettings_OnClick");

        Assert.Null(scroller.Attribute("FontSize"));
        Assert.Null(workingSetSummary.Attribute("MouseEnter"));
        Assert.Null(workingSetSummary.Attribute("MouseLeave"));
        Assert.DoesNotContain(workingSetSummary, scroller.Descendants());
        Assert.Equal("False", (string?)scroller.Attribute("CanContentScroll"));
        Assert.Contains("scrollViewer.ScrollableWidth <= 0.5d", handler);
        Assert.Contains("ScrollToHorizontalOffset", handler);
        Assert.Contains("ScrollToRightEnd", handler);
        Assert.Contains("ScrollToLeftEnd", handler);
        Assert.Contains("InitialPause", handler);
        Assert.Contains("EdgePause", handler);
    }

    [Fact]
    public void StableAnchorTrendAndScopeRemainVisibleAndUnambiguous()
    {
        var document = LoadDocument();
        var popup = FindNamedElement(document, "StableAnchorPopup");
        var scope = FindNamedElement(popup, "StableAnchorScopeDescription");
        var trend = document.Descendants(Presentation + "TextBlock").Single(element =>
            (string?)element.Attribute("Text") == "{Binding StableTrendGlyph}");
        var downTrigger = trend.Descendants(Presentation + "DataTrigger").Single(element =>
            (string?)element.Attribute("Value") == "↓");
        var upTrigger = trend.Descendants(Presentation + "DataTrigger").Single(element =>
            (string?)element.Attribute("Value") == "↑");
        var trendStyle = trend.Element(Presentation + "TextBlock.Style")!
            .Element(Presentation + "Style")!;

        Assert.Equal("{StaticResource CaptionStyle}", (string?)scope.Attribute("Style"));
        Assert.Equal("15", (string?)trend.Attribute("FontSize"));
        Assert.Equal("Bold", (string?)trend.Attribute("FontWeight"));
        Assert.Null(trend.Attribute("Margin"));
        Assert.Null(trend.Attribute("MinWidth"));
        AssertStyleSetter(trendStyle, "Margin", "0");
        AssertStyleSetter(trendStyle, "MinWidth", "0");
        foreach (var trigger in new[] { upTrigger, downTrigger })
        {
            Assert.Contains(trigger.Elements(Presentation + "Setter"), setter =>
                (string?)setter.Attribute("Property") == "Margin" &&
                (string?)setter.Attribute("Value") == "5,0,0,0");
            Assert.Contains(trigger.Elements(Presentation + "Setter"), setter =>
                (string?)setter.Attribute("Property") == "MinWidth" &&
                (string?)setter.Attribute("Value") == "14");
        }
        Assert.Contains(downTrigger.Descendants(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Foreground" &&
            (string?)setter.Attribute("Value") == "{DynamicResource AccentBrush}");
    }

    [Fact]
    public void ThemedMessagesUseOnlyTheNativeCaptionCloseButton()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var message = MethodBody(
            code,
            "private MessageBoxResult ShowThemedMessage",
            "private async Task CloseApplicationsAsync");

        Assert.DoesNotContain("var close = new Button", message);
        Assert.DoesNotContain("var titleBar = new Grid", message);
        Assert.DoesNotContain("titleBar.Children.Add", message);
        Assert.Contains("System.Windows.Input.Key.Escape", message);
        Assert.Contains("ApplyDialogTheme(dialog)", message);
    }

    [Fact]
    public void BenefitLearningStatusCountsOnlyFamiliesWithValidObservationSamples()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var status = MethodBody(
            code,
            "private void UpdateBenefitLearningStatus",
            "private void RefreshProtectionSuggestions");

        Assert.Contains("var learnedFamilyCount = records", status);
        Assert.Contains(".Where(record => record.ValidSampleCount > 0)", status);
        Assert.Contains(".Distinct(StringComparer.OrdinalIgnoreCase)", status);
        Assert.Contains("learnedFamilyCount,", status);
    }

    [Fact]
    public void LegacyBenefitLearningCleanupIsSeparateFromClearingValidLearning()
    {
        var document = LoadDocument();
        var code = File.ReadAllText(CodeFixturePath());
        var cleanup = MethodBody(
            code,
            "private void ClearLegacyBenefitLearning_OnClick",
            "private void ClearBenefitLearning_OnClick");
        var status = MethodBody(
            code,
            "private void UpdateBenefitLearningStatus",
            "private void RefreshProtectionSuggestions");
        var legacyButton = FindNamedElement(document, "ClearLegacyBenefitLearningButton");
        var clearButton = FindNamedElement(document, "ClearBenefitLearningButton");
        var actions = legacyButton.Parent;

        Assert.NotNull(actions);
        Assert.Same(actions, clearButton.Parent);
        Assert.Equal(Presentation + "StackPanel", actions!.Name);
        Assert.Equal("Horizontal", (string?)actions.Attribute("Orientation"));
        Assert.Equal("Collapsed", (string?)legacyButton.Attribute("Visibility"));
        Assert.Contains(legacyButton.Descendants(Presentation + "TextBlock"), element =>
            (string?)element.Attribute("Text") == "{DynamicResource ClearLegacyBenefitLearning}");
        Assert.Contains("ClearLegacyBenefitLearningButton.Visibility = legacyRecordCount > 0", status);
        Assert.Contains("? Visibility.Visible", status);
        Assert.Contains(": Visibility.Collapsed", status);
        Assert.Contains("_applicationBackoffTracker.RemoveLegacyOnlyLearning()", cleanup);
        Assert.Contains("SaveBenefitLearning()", cleanup);
        Assert.Contains("UpdateBenefitLearningStatus()", cleanup);
        Assert.DoesNotContain("ClearLearning()", cleanup);
    }

    [Fact]
    public void BenefitLearningRowCleanupUsesStableFamilyIdentityAndRefreshesThePage()
    {
        var state = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(CodeFixturePath())!,
            "AppState.cs"));
        var code = File.ReadAllText(CodeFixturePath());
        var handler = MethodBody(
            code,
            "private void ClearApplicationBenefitLearning_OnClick",
            "private void UpdateBenefitLearningStatus");

        Assert.Contains("public sealed record BenefitLearningRow(\n    string FamilyKey,\n    string ScopeKey,", state);
        Assert.Contains("metric.FamilyKey,", code);
        Assert.Contains("_applicationBackoffTracker.RemoveLearningForFamily(row.FamilyKey)", handler);
        Assert.Contains("Select(ProtectionSuggestionPolicy.SuggestionId)", handler);
        Assert.Contains("SaveBenefitLearning()", handler);
        Assert.Contains("UpdateBenefitLearningStatus()", handler);
        Assert.Contains("UpdatePreviewRows()", handler);
    }

    [Fact]
    public void HistoryAnalysisControlsAndLearningTableKeepConsistentGeometry()
    {
        var document = LoadDocument();
        var tabStyle = FindKeyedStyle(document, "AnalysisTabButtonStyle");
        var filterStyle = FindKeyedStyle(document, "AnalysisFilterButtonStyle");
        var leftHeaderStyle = FindKeyedStyle(document, "CandidateLeftHeaderStyle");
        var centerHeaderStyle = FindKeyedStyle(document, "CandidateCenterHeaderStyle");
        var learningPanel = FindNamedElement(document, "BenefitLearningPanel");

        Assert.Contains(tabStyle.Descendants(Presentation + "Border"), element =>
            (string?)element.Attribute("CornerRadius") == "6");
        AssertStyleSetter(filterStyle, "Width", "52");
        AssertStyleSetter(filterStyle, "Height", "32");
        AssertStyleSetter(leftHeaderStyle, "VerticalContentAlignment", "Center");
        AssertStyleSetter(centerHeaderStyle, "VerticalContentAlignment", "Center");

        var reviewButton = FindNamedElement(learningPanel, "ReviewProtectionSuggestionsButton");
        Assert.Equal("1", (string?)reviewButton.Attribute("Grid.Column"));
        Assert.Equal("{StaticResource ButtonStyle}", (string?)reviewButton.Attribute("Style"));
        Assert.Contains(learningPanel.Descendants(Presentation + "Grid"), element =>
            (string?)element.Attribute("Margin") == "0,16,17,9");
        Assert.Contains(learningPanel.Descendants(Presentation + "ScrollViewer"), element =>
            (string?)element.Attribute("VerticalScrollBarVisibility") == "Visible");

        AssertLearningTextAlignment(learningPanel, "{DynamicResource AverageRebound}", "{StaticResource AnalysisHeaderTextStyle}");
        AssertLearningTextAlignment(learningPanel, "{Binding AverageRebound}");
        AssertLearningTextAlignment(learningPanel, "{DynamicResource ProtectionSuggestionStatus}", "{StaticResource AnalysisHeaderTextStyle}");
        AssertLearningTextAlignment(learningPanel, "{Binding Suggestion}");

        Assert.Contains(learningPanel.Descendants(Presentation + "Border"), element =>
            (string?)element.Attribute("Padding") == "0,10" &&
            (string?)element.Attribute("MinHeight") == "92");
        var barGrid = learningPanel.Descendants(Presentation + "Grid").Single(element =>
            (string?)element.Attribute("Grid.Column") == "1" &&
            (string?)element.Attribute("Margin") == "0,0,10,0");
        Assert.Equal(
            ["18", "4", "8", "12", "18", "4", "8"],
            barGrid.Elements(Presentation + "Grid.RowDefinitions")
                .Single()
                .Elements(Presentation + "RowDefinition")
                .Select(element => (string?)element.Attribute("Height")));
    }

    [Fact]
    public void StableSuppressionPausesWithLearningWithoutReplacingTheSelection()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var synchronization = MethodBody(
            code,
            "private void SynchronizeStableStateSuppressionControls",
            "private void CandidateDisplayPopup_OnClosed");

        Assert.Contains("ToolTipService.ShowOnDisabled=\"True\"", layout);
        Assert.Contains("IsEnabled = _settings.IntelligentCandidateSelection", synchronization);
        Assert.Contains("StableSuppressionPausedWithoutLearning", synchronization);
        Assert.Contains("CustomStableStateSuppressionProfiles", synchronization);
        Assert.Contains("ActiveCustomStableStateSuppressionProfileId", synchronization);
        Assert.DoesNotContain("StableStateSuppressionMode =", synchronization);
    }

    [Fact]
    public void ModalDialogsSupportKeyboardDismissal()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var ultimate = MethodBody(code, "private bool ShowUltimateRiskDialog", "private void RefreshProfileSelectors");
        var close = MethodBody(code, "private ClosePromptResult? ShowCloseBehaviorDialog", "private void HideToTray");

        Assert.Contains("System.Windows.Input.Key.Escape", ultimate);
        Assert.Contains("dialog.Close();", ultimate);
        Assert.Contains("System.Windows.Input.Key.Escape", close);
        Assert.Contains("dialog.Close();", close);
        Assert.Contains("ApplyDialogTheme(dialog);", close);
    }

    [Fact]
    public void SettingsExposeSystemThemeFollowing()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("x:Name=\"FollowSystemThemeCheckBox\"", text);
        Assert.Contains("x:Name=\"ThemeButton\"", text);
        Assert.Contains("x:Name=\"QuickThemeButton\"", text);
        Assert.Contains("x:Name=\"CompactThemeButton\"", text);
        Assert.Contains("x:Name=\"ThemeButtonIcon\"", text);
        Assert.Contains("x:Name=\"QuickThemeIcon\"", text);
        Assert.Contains("x:Name=\"CompactThemeIcon\"", text);
        Assert.Contains("{DynamicResource FollowSystemTheme}", text);
    }

    [Fact]
    public void ThemeToggleShowsCurrentThemeAndDescribesTheClickAction()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var applyTheme = MethodBody(code, "private void ApplyTheme(bool light)", "private bool IsLightThemeActive");

        Assert.Contains("var themeIcon = light ? \"\\uE706\" : \"\\uE708\";", applyTheme);
        Assert.Contains("var themeGeometry = (Geometry)FindResource(light ? \"IconSun\" : \"IconMoon\");", applyTheme);
        Assert.Contains(
            "var toggleThemeResourceKey = light ? \"ToggleToDarkTheme\" : \"ToggleToLightTheme\";",
            applyTheme);
        Assert.Contains("QuickThemeButton.SetResourceReference(ToolTipProperty, toggleThemeResourceKey);", applyTheme);
        Assert.Contains("CompactThemeButton.SetResourceReference(ToolTipProperty, toggleThemeResourceKey);", applyTheme);
        Assert.Equal("切换为暗主题", UiTextCatalog.For(UiLanguage.ChineseSimplified)["ToggleToDarkTheme"]);
        Assert.Equal("切换为明主题", UiTextCatalog.For(UiLanguage.ChineseSimplified)["ToggleToLightTheme"]);
        Assert.Equal("Switch to dark theme", UiTextCatalog.For(UiLanguage.English)["ToggleToDarkTheme"]);
        Assert.Equal("Switch to light theme", UiTextCatalog.For(UiLanguage.English)["ToggleToLightTheme"]);
    }

    [Fact]
    public void MainWindowUsesNativeTitleBarForDockCompatibility()
    {
        var document = LoadDocument();
        var text = document.ToString();

        Assert.Equal("SingleBorderWindow", (string?)document.Root!.Attribute("WindowStyle"));
        Assert.DoesNotContain("shell:WindowChrome.WindowChrome", text);
        var rootGrid = document.Root.Element(Presentation + "Grid")!;
        Assert.Equal("0", (string?)rootGrid.Element(Presentation + "Grid.RowDefinitions")?
            .Elements(Presentation + "RowDefinition").First().Attribute("Height"));
        var titleBar = rootGrid.Elements(Presentation + "Border").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "TitleBar");
        Assert.Equal("Collapsed", (string?)titleBar.Attribute("Visibility"));
    }

    [Fact]
    public void NativeWindowUsesThemeColoredCompositionBackground()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var sourceInitialized = MethodBody(
            code,
            "private void MainWindow_OnSourceInitialized",
            "private void ApplyFinalWindowMode");
        var restore = MethodBody(code, "private void RestoreFromTray", "internal void RestoreFromExternalActivation");
        var applyTheme = MethodBody(code, "private void ApplyTheme(bool light)", "private bool IsLightThemeActive");

        Assert.Contains("ApplyNativeWindowTheme(IsLightThemeActive())", sourceInitialized);
        Assert.Contains("ApplyNativeWindowTheme(light)", applyTheme);
        Assert.Contains("private void ApplyCompositionBackground()", code);
        Assert.Contains("source.CompositionTarget.BackgroundColor", sourceInitialized);
        Assert.DoesNotContain("WindowThemeService.ApplyDarkTitleBar", restore);
    }

    [Fact]
    public void NativeWindowDoesNotEraseTheClientAreaBeforeWpfRenders()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var hook = MethodBody(
            code,
            "private IntPtr WindowAnimationMessageHook",
            "private void LogWindowAnimationState");

        Assert.Contains("message == 0x0014", hook);
        Assert.Contains("handled = true;", hook);
        Assert.Contains("return new IntPtr(1);", hook);
    }

    [Fact]
    public void DarkFormControlsOverrideSystemTemplates()
    {
        var document = LoadDocument();
        var comboBoxStyle = FindKeyedStyle(document, "ThemedComboBoxStyle");
        var checkBoxStyle = FindKeyedStyle(document, "ThemedCheckBoxStyle");

        Assert.Contains(comboBoxStyle.Descendants(Presentation + "ControlTemplate"), template =>
            (string?)template.Attribute("TargetType") == "ComboBox");
        Assert.Contains(comboBoxStyle.Descendants(Presentation + "Border"), border =>
            (string?)border.Attribute("Background") == "{DynamicResource SurfaceRaisedBrush}");
        Assert.Contains(checkBoxStyle.Descendants(Presentation + "ControlTemplate"), template =>
            (string?)template.Attribute("TargetType") == "CheckBox");
        Assert.Contains(checkBoxStyle.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Background" &&
            (string?)setter.Attribute("Value") == "{DynamicResource SurfaceRaisedBrush}");
        Assert.Contains(checkBoxStyle.Descendants(Presentation + "Border"), border =>
            (string?)border.Attribute("Background") == "{TemplateBinding Background}" &&
            (string?)border.Attribute("BorderBrush") == "{TemplateBinding BorderBrush}");
        Assert.Contains(checkBoxStyle.Descendants(Presentation + "Path"), path =>
            (string?)path.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "CheckMark");
    }

    [Fact]
    public void OverviewSeparatesTrendAndOptimizationMetricsFromCurrentMemoryStatus()
    {
        var document = LoadDocument();
        var text = document.ToString();
        var trendCard = document.Descendants(Presentation + "Border").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "MemoryTrendCard");
        var trendText = trendCard.ToString();

        Assert.Contains("{DynamicResource MemoryTrend}", trendText);
        Assert.Contains("{Binding MemoryChange}", trendText);
        Assert.Contains("{Binding CumulativeTrim}", trendText);
        Assert.Contains("{Binding CumulativeNetGain}", trendText);
        var code = File.ReadAllText(CodeFixturePath());
        var attributionGuard = code.IndexOf(
            "if (!OptimizationResultAttributionPolicy.CanAttributeSystemMemoryChange(succeeded))",
            StringComparison.Ordinal);
        var cumulativeUpdate = code.IndexOf(
            "_cumulativeNetGainBytes = checked(_cumulativeNetGainBytes + netAvailable)",
            StringComparison.Ordinal);
        Assert.True(attributionGuard >= 0);
        Assert.True(cumulativeUpdate > attributionGuard);
        Assert.Contains("{Binding SelfOverhead}", trendText);
        Assert.DoesNotContain("{Binding MemoryUsage}", trendText);
        Assert.DoesNotContain("{Binding AvailableMemory}", trendText);
        Assert.Contains("local:MemoryHistoryChart x:Name=\"MemoryChart\"", text);
        Assert.DoesNotContain("x:Name=\"TrendCanvas\"", text);
    }

    [Fact]
    public void AutoOptimizationUsesSlidingSafetyToggleStyle()
    {
        var document = LoadDocument();
        var style = FindKeyedStyle(document, "SafetyToggleStyle");

        Assert.Contains(style.Descendants(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Width" && (string?)setter.Attribute("Value") == "44");
        Assert.Equal(2, document.Descendants(Presentation + "CheckBox").Count(element =>
            (string?)element.Attribute("Style") == "{StaticResource SafetyToggleStyle}" &&
            new[] { "AutoToggle", "CompactAutoToggle" }.Contains(
                (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")))));
    }

    [Fact]
    public void InteractiveControlsUseThemedFocusInsteadOfSystemDottedAdorners()
    {
        var document = LoadDocument();

        foreach (var key in new[] { "ButtonStyle", "NavButtonStyle", "ThemedComboBoxStyle", "ThemedCheckBoxStyle", "AutoToggleStyle", "EditorSliderStyle" })
        {
            var style = FindKeyedStyle(document, key);
            Assert.Contains(style.Elements(Presentation + "Setter"), setter =>
                (string?)setter.Attribute("Property") == "FocusVisualStyle" &&
                (string?)setter.Attribute("Value") == "{x:Null}");
        }

        Assert.Contains(document.Descendants(Presentation + "ControlTemplate"), template =>
            (string?)template.Attribute("TargetType") == "ToolTip");
    }

    [Fact]
    public void NavigationDoesNotDrawAFocusOutlineAfterClick()
    {
        var style = FindKeyedStyle(LoadDocument(), "NavButtonStyle");

        Assert.DoesNotContain(style.Descendants(Presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") is "IsKeyboardFocused" or "IsKeyboardFocusWithin");
        Assert.Contains(style.Descendants(Presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsChecked");
    }

    [Fact]
    public void RuntimeProgressToggleLoadsThePersistedSetting()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains(
            "RuntimeProgressPersistenceCheckBox.IsChecked = _settings.RuntimeProgressPersistenceEnabled;",
            code);
    }

    [Fact]
    public void SettingsUsesCompactToggleRowsAndThemedLongIdleSlider()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("x:Key=\"SettingToggleRowStyle\"", text);
        Assert.Contains("x:Name=\"LongIdleMinutesSlider\" Style=\"{StaticResource EditorSliderStyle}\"", text);
        Assert.Contains("Margin=\"0,8,0,0\" Style=\"{StaticResource SettingToggleRowStyle}\"", text);
        Assert.Contains("x:Name=\"SettingsPage\" Visibility=\"Collapsed\">\n            <ScrollViewer VerticalScrollBarVisibility=\"Auto\" HorizontalScrollBarVisibility=\"Disabled\">", text);
    }

    [Fact]
    public void ThemedSlidersDoNotDrawAFocusOutline()
    {
        var style = FindKeyedStyle(LoadDocument(), "EditorSliderStyle");
        var text = style.ToString();

        Assert.DoesNotContain(style.Descendants(Presentation + "Border"), border =>
            (string?)border.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "FocusBorder");
        Assert.DoesNotContain(style.Descendants(Presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsKeyboardFocused");
        Assert.Contains(style.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Height" && (string?)setter.Attribute("Value") == "48");
        Assert.Contains("x:Name=\"ThumbVisual\"", text);
        Assert.Contains("DropShadowEffect BlurRadius=\"6\" ShadowDepth=\"0\" Opacity=\"0.24\"", text);
        Assert.Contains("HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"", text);
        Assert.DoesNotContain("ThumbScale", text);
        Assert.Contains("Property=\"IsDragging\" Value=\"True\"", text);
        Assert.True(style.Descendants(Presentation + "Border").Count(border =>
            (string?)border.Attribute("Height") == "4" && (string?)border.Attribute("CornerRadius") == "2") >= 2);
        Assert.Contains("Text=\"{Binding Minimum, RelativeSource={RelativeSource TemplatedParent}, StringFormat={}{0:0}}\"", text);
        Assert.Contains("Text=\"{Binding Maximum, RelativeSource={RelativeSource TemplatedParent}, StringFormat={}{0:0}}\"", text);
    }

    [Fact]
    public void TitleBarCommandsExposeLocalizedNamesAndKeyboardFocus()
    {
        var document = LoadDocument();
        var style = FindKeyedStyle(document, "TitleBarButtonStyle");
        var titleButtons = document.Descendants(Presentation + "Button")
            .Where(element => (string?)element.Attribute("Style") == "{StaticResource TitleBarButtonStyle}")
            .ToArray();
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains(style.Descendants(Presentation + "Condition"), condition =>
            ((string?)condition.Attribute("Binding"))?.Contains("IsKeyboardFocused", StringComparison.Ordinal) == true &&
            (string?)condition.Attribute("Value") == "True");
        Assert.Contains(style.Descendants(Presentation + "Condition"), condition =>
            ((string?)condition.Attribute("Binding"))?.Contains("InputModality.IsKeyboardMode", StringComparison.Ordinal) == true &&
            (string?)condition.Attribute("Value") == "True");
        Assert.Equal(3, titleButtons.Length);
        Assert.All(titleButtons, button =>
        {
            Assert.StartsWith("{DynamicResource ", (string?)button.Attribute("ToolTip"));
            Assert.StartsWith("{DynamicResource ", (string?)button.Attribute("AutomationProperties.Name"));
        });
        Assert.Contains("SetResourceReference(AutomationProperties.NameProperty, resourceKey)", code);
        Assert.Contains("isMaximized ? \"RestoreWindow\" : \"MaximizeWindow\"", code);
    }

    [Fact]
    public void IconOnlyAndEditorControlsExposeAutomationNames()
    {
        var document = LoadDocument();
        var namedEditorTypes = new[] { "Slider", "TextBox", "ComboBox" };

        foreach (var type in namedEditorTypes)
        {
            var controls = document.Descendants(Presentation + type)
                .Where(element => element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) is not null)
                .ToArray();
            Assert.NotEmpty(controls);
            Assert.All(controls, control =>
                Assert.StartsWith("{DynamicResource ", (string?)control.Attribute("AutomationProperties.Name")));
        }

        var safetyToggles = document.Descendants(Presentation + "CheckBox")
            .Where(element => (string?)element.Attribute("Style") == "{StaticResource SafetyToggleStyle}")
            .ToArray();
        Assert.NotEmpty(safetyToggles);
        Assert.All(safetyToggles, toggle =>
            Assert.StartsWith("{DynamicResource ", (string?)toggle.Attribute("AutomationProperties.Name")));

        var navigation = document.Descendants(Presentation + "ToggleButton")
            .Where(element => (string?)element.Attribute("Style") == "{StaticResource NavButtonStyle}")
            .ToArray();
        Assert.Equal(7, navigation.Length);
        Assert.All(navigation, button =>
            Assert.StartsWith("{DynamicResource ", (string?)button.Attribute("AutomationProperties.Name")));

        Assert.All(document.Descendants(Presentation + "DataGrid"), grid =>
            Assert.StartsWith("{DynamicResource ", (string?)grid.Attribute("AutomationProperties.Name")));
        Assert.Contains(FindKeyedStyle(document, "ProfileHelpButtonStyle").Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "AutomationProperties.Name" &&
            (string?)setter.Attribute("Value") == "{DynamicResource Help}");
        var helpStyle = FindKeyedStyle(document, "ProfileHelpButtonStyle");
        Assert.Contains(helpStyle.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Background" &&
            (string?)setter.Attribute("Value") == "{DynamicResource SurfaceRaisedBrush}");
        Assert.Contains(helpStyle.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "BorderBrush" &&
            (string?)setter.Attribute("Value") == "{DynamicResource BorderBrush}");
        Assert.Contains(helpStyle.Descendants(Presentation + "Border"), border =>
            (string?)border.Attribute("CornerRadius") == "11");
    }

    [Fact]
    public void PageCommandsUseConsistentIconButtonStyles()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("x:Key=\"CommandButtonStyle\"", text);
        Assert.Contains("x:Key=\"ToolbarButtonStyle\"", text);
        Assert.True(CountOccurrences(text, "{StaticResource ButtonIconPathStyle}") >= 8);
        var commandStyle = FindKeyedStyle(LoadDocument(), "CommandButtonStyle");
        Assert.Contains(commandStyle.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Width" &&
            (string?)setter.Attribute("Value") == "190");
        Assert.Contains(commandStyle.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "HorizontalContentAlignment" &&
            (string?)setter.Attribute("Value") == "Center");
    }

    [Fact]
    public void ButtonsHonorPaddingAndDestructiveActionsUseTheirOwnStyle()
    {
        var document = LoadDocument();
        var buttonStyle = FindKeyedStyle(document, "ButtonStyle");
        var presenter = buttonStyle.Descendants(Presentation + "ContentPresenter").Single();

        Assert.Equal("{TemplateBinding Padding}", (string?)presenter.Attribute("Margin"));
        Assert.Equal("Button", (string?)FindKeyedStyle(document, "DestructiveActionButtonStyle").Attribute("TargetType"));

        var destructiveNames = new[] { "DeleteProfileButton", "ClearBenefitLearningButton", "ClearDiagnosticDataButton" };
        var destructiveButtons = document.Descendants(Presentation + "Button")
            .Where(button => destructiveNames.Contains(
                (string?)button.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))))
            .ToArray();
        Assert.Equal(destructiveNames.Length, destructiveButtons.Length);
        Assert.All(destructiveButtons, button =>
            Assert.Equal("{StaticResource DestructiveActionButtonStyle}", (string?)button.Attribute("Style")));

        var code = File.ReadAllText(CodeFixturePath());
        Assert.DoesNotContain("Content = T(\"Cancel\"), Width =", code);
        Assert.Contains("Content = T(\"Cancel\"), MinWidth =", code);
    }

    [Fact]
    public void DangerButtonHoverKeepsActionTextAndStableSuppressionSelectorIsNotClipped()
    {
        var layout = File.ReadAllText(FixturePath());
        var dangerStyle = FindKeyedStyle(LoadDocument(), "DangerButtonStyle").ToString();

        Assert.True(CountOccurrences(
            dangerStyle,
            "Property=\"Foreground\" Value=\"{DynamicResource ActionTextBrush}\"") >= 3);
        Assert.Contains("x:Name=\"OverviewStableSuppressionRow\" Margin=\"0,8,0,0\" Background=\"Transparent\" BorderThickness=\"0\" Padding=\"0\" Height=\"38\"", layout);
    }

    [Fact]
    public void PrimaryButtonHoverAndPressedStatesKeepReadableActionText()
    {
        var primaryStyle = FindKeyedStyle(LoadDocument(), "PrimaryButtonStyle").ToString();

        Assert.True(CountOccurrences(
            primaryStyle,
            "Property=\"Foreground\" Value=\"{DynamicResource ActionTextBrush}\"") >= 3);
    }

    [Fact]
    public void DeepReleaseUsesFullWidthAndLeavesRoomForTheStableSuppressionSelector()
    {
        var document = LoadDocument();
        var deepRelease = document.Descendants(Presentation + "Button").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "DeepReleaseButton");
        var stableRow = document.Descendants(Presentation + "Border").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "OverviewStableSuppressionRow");

        Assert.Equal("Stretch", (string?)deepRelease.Attribute("HorizontalAlignment"));
        Assert.Equal("30", (string?)deepRelease.Attribute("Height"));
        Assert.Null((string?)deepRelease.Attribute("MinWidth"));
        Assert.Equal("14,0", (string?)deepRelease.Attribute("Padding"));
        Assert.Equal("0,12,0,0", (string?)deepRelease.Attribute("Margin"));
        Assert.Equal("38", (string?)stableRow.Attribute("Height"));
        var stableSelector = document.Descendants(Presentation + "ComboBox").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "OverviewStableStateSuppressionModeBox");
        Assert.Equal("36", (string?)stableSelector.Attribute("Height"));
    }

    [Fact]
    public void ComboBoxItemsUseThemedRoundedRows()
    {
        var document = LoadDocument();
        var style = document.Descendants(Presentation + "Style").Single(element =>
            (string?)element.Attribute("TargetType") == "ComboBoxItem" &&
            element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is null);
        var text = style.ToString();

        Assert.Contains("CornerRadius=\"4\"", text);
        Assert.Contains("Property=\"IsHighlighted\" Value=\"True\"", text);
        Assert.Contains("Property=\"IsSelected\" Value=\"True\"", text);
    }

    [Fact]
    public void OverviewShowsRetainedSessionUptimeBesideLastUpdate()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var state = new AppState { SessionUptime = "12 min" };
        var toggle = MethodBody(
            code,
            "private void RuntimeProgressPersistenceCheckBox_OnChanged",
            "private void IgnoreMemoryPressureThresholdCheckBox_OnChanged");
        var save = MethodBody(
            code,
            "private void SaveRuntimeProgressIfDue",
            "private RuntimeProgressDocument CaptureRuntimeProgress");
        var capture = MethodBody(
            code,
            "private RuntimeProgressDocument CaptureRuntimeProgress",
            "private void SaveBenefitLearning");

        Assert.Contains("Text=\"{DynamicResource SessionUptime}\"", layout);
        Assert.Contains("Text=\"{Binding SessionUptime}\"", layout);
        Assert.Contains("_sessionStartedTimestamp = Stopwatch.GetTimestamp()", code);
        Assert.Contains("var elapsed = CurrentSessionUptime()", code);
        Assert.Contains("_restoredSessionUptime = RuntimeProgressPolicy.RestoreDuration(progress.SessionUptimeSeconds)", code);
        Assert.Contains("CurrentSessionUptime().TotalSeconds", capture);
        Assert.DoesNotContain("_pendingRuntimeActivities.Count", save);
        Assert.DoesNotContain("_pendingRuntimeTrimHistory.Count", save);
        Assert.Contains("var activities = _pendingRuntimeActivities.ToList()", capture);
        Assert.Contains("if (activityKeys.Contains(pair.Key)) continue", capture);
        Assert.Contains("var trimHistory = _pendingRuntimeTrimHistory.ToList()", capture);
        Assert.Contains("if (trimProcessIds.Contains(pair.Key)) continue", capture);
        Assert.Contains("_restoredSessionUptime = TimeSpan.Zero", toggle);
        Assert.Contains("UpdateSessionUptime()", toggle);
        Assert.Contains("var now = DateTimeOffset.UtcNow", save);
        Assert.Contains("SaveRuntimeProgressIfDue();", code);
        Assert.DoesNotContain("SaveRuntimeProgressIfDue(activityObservedAt)", code);
        Assert.Equal("12 min", state.SessionUptime);
    }

    [Fact]
    public void CandidateRowsExposeThemedActionsAndScrollBarsUseMuseTemplate()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("x:Key=\"NonSelectableDataGridRowStyle\"", text);
        Assert.Contains("x:Name=\"CandidatesGrid\"", text);
        Assert.Contains("PreviewMouseRightButtonDown=\"CandidatesGrid_OnPreviewMouseRightButtonDown\"", text);
        Assert.Contains("x:Name=\"CandidatesContextMenu\"", text);
        Assert.Contains("Style=\"{StaticResource ThemedContextMenuStyle}\"", text);
        Assert.Contains("x:Key=\"VerticalScrollBarTemplate\"", text);
        Assert.Contains("x:Key=\"HorizontalScrollBarTemplate\"", text);
        Assert.Contains("<Style TargetType=\"ScrollBar\">", text);
        Assert.Contains("x:Key=\"ScrollTrackBrush\"", text);
        Assert.Contains("{DynamicResource ScrollThumbBrush}", text);
        Assert.Contains("{DynamicResource ScrollThumbHoverBrush}", text);
    }

    [Fact]
    public void DataGridsKeepApplicationOrderingAndUseThemedContextSelection()
    {
        var document = LoadDocument();
        var dataGridStyle = document.Descendants(Presentation + "Style").Single(style =>
            (string?)style.Attribute("TargetType") == "DataGrid" &&
            style.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is null);
        var rowStyle = document.Descendants(Presentation + "Style").Single(style =>
            (string?)style.Attribute("TargetType") == "DataGridRow" &&
            style.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is null);
        var cellStyle = document.Descendants(Presentation + "Style").Single(style =>
            (string?)style.Attribute("TargetType") == "DataGridCell" &&
            style.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is null);
        var menuItemStyle = FindKeyedStyle(document, "ThemedMenuItemStyle");
        var processesMenu = document.Descendants(Presentation + "ContextMenu").Single(menu =>
            (string?)menu.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "ProcessesContextMenu");

        Assert.Contains(dataGridStyle.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "CanUserSortColumns" &&
            (string?)setter.Attribute("Value") == "False");
        Assert.Contains(rowStyle.Descendants(Presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsMouseOver" &&
            (string?)trigger.Attribute("Value") == "True");
        Assert.DoesNotContain(rowStyle.Descendants(Presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsSelected");
        Assert.Contains(cellStyle.Descendants(Presentation + "Trigger")
            .Where(trigger =>
                (string?)trigger.Attribute("Property") == "IsSelected" &&
                (string?)trigger.Attribute("Value") == "True")
            .SelectMany(trigger => trigger.Elements(Presentation + "Setter")), setter =>
            (string?)setter.Attribute("Property") == "Background" &&
            (string?)setter.Attribute("Value") == "Transparent");
        Assert.Contains(menuItemStyle.Descendants(Presentation + "ControlTemplate"), template =>
            (string?)template.Attribute("TargetType") == "MenuItem");
        Assert.DoesNotContain(menuItemStyle.Descendants(), element =>
            element.Name.LocalName is "Icon" or "CheckBox");
        Assert.Equal("{StaticResource ThemedContextMenuStyle}", (string?)processesMenu.Attribute("Style"));
        Assert.All(processesMenu.Elements(Presentation + "MenuItem"), item =>
            Assert.Equal("{StaticResource ThemedMenuItemStyle}", (string?)item.Attribute("Style")));
    }

    [Fact]
    public void OptimizationKeepsTheWindowResponsiveAndReportsProgress()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var optimization = MethodBody(code, "private async Task RunOptimizationAsync", "private void ShowNoCandidatesDialog");
        var busyState = MethodBody(code, "private void SetBusyState", "private void StartResponsivenessMonitoring");

        Assert.Contains("x:Name=\"OptimizeNowButton\"", layout);
        Assert.Contains("x:Name=\"CompactOptimizeNowButton\"", layout);
        Assert.Contains("OptimizationPreparing", optimization);
        Assert.Contains("OptimizationProgressFormat", optimization);
        Assert.Contains("Dispatcher.Yield(DispatcherPriority.Render)", optimization);
        Assert.DoesNotContain("ProtectionPage.IsEnabled", busyState);
        Assert.Contains("OptimizeNowButton.IsEnabled = !isBusy", busyState);
        Assert.Contains("CompactOptimizeNowButton.IsEnabled = !isBusy", busyState);
        Assert.Contains("AppendOptimizationProcess", code);
        Assert.Contains("AppendOptimizationRun", code);
        Assert.Contains("AppendResponsivenessStall", code);
        var noActionExit = optimization.IndexOf("if (!plan.ShouldRun)", StringComparison.Ordinal);
        var resourceSampling = optimization.IndexOf("OptimizationResourceSampler.Start()", StringComparison.Ordinal);
        Assert.True(noActionExit >= 0);
        Assert.True(resourceSampling > noActionExit);
        Assert.Contains("if (recordOptimizationRun)", optimization);
        Assert.Contains("LongIdleOptimizationPolicy.CanEvaluate", code);
        Assert.Contains("_lastCandidateCalibrations.TryGetValue(runContext.Trigger", code);
        Assert.Contains("OptimizationTriggerKind.Automatic or OptimizationTriggerKind.LongIdle", code);
    }

    [Fact]
    public void CandidateRowsExposeAutomaticBackoffState()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("Binding=\"{Binding AutoOptimizationStatus}\"", layout);
        Assert.Contains("FormatBackoffStatus(candidate.Family, now)", code);
        Assert.Contains("ApplicationComponentIdentity.GroupProcesses(family).Keys.ToArray()", code);
        Assert.Contains("_applicationBackoffTracker.GetBackoffStatus(", code);
        Assert.Contains("BlockedComponentKeys(now)", code);
        Assert.Contains("PendingObservationComponentKeys(now)", code);
        Assert.Contains("automaticBackoffFamilies: null", code);
        Assert.Contains("pendingReboundObservationFamilies: null", code);
        Assert.Contains("automaticBackoffComponents: learningFilters.BlockedComponents", code);
        Assert.Contains("pendingReboundObservationComponents: learningFilters.PendingComponents", code);
        Assert.Contains("AutoBackoffSecondsFormat", code);
        Assert.Contains("AutoBackoffLongTerm", code);
        Assert.Contains("UpdateLongTermRetryPermissions", code);
        Assert.Contains("OptimizationPlanner.IsSevereMemoryPressure", code);
        Assert.Contains("ReboundObservationPending", code);
        Assert.Contains("null => T(\"NoBackoff\")", code);
        Assert.DoesNotContain("T(\"AutoOptimizationReady\")", code);
    }

    [Fact]
    public void CandidateRowsShowContinuousIdleStatus()
    {
        var layout = File.ReadAllText(FixturePath());

        Assert.Contains("Text=\"{DynamicResource Application}\"", layout);
        Assert.Contains("Text=\"{DynamicResource CandidateMemory}\"", layout);
        Assert.Contains("ToolTip=\"{DynamicResource CandidateMemoryHelp}\"", layout);
        Assert.Contains("\"CandidateMemoryFormat\"", File.ReadAllText(CodeFixturePath()));
        Assert.Contains("Text=\"{DynamicResource IdleStatusColumn}\"", layout);
        Assert.Contains("Binding=\"{Binding IdleStatus}\"", layout);
        Assert.Contains("Text=\"{DynamicResource CandidateBenefitLearning}\"", layout);
        Assert.Contains("Binding=\"{Binding Ranking}\"", layout);
    }

    [Fact]
    public void CandidateHeadersAndCellsUseMatchingColumnAlignment()
    {
        var layout = File.ReadAllText(FixturePath());
        var candidateStart = layout.IndexOf("ItemsSource=\"{Binding Candidates}\"", StringComparison.Ordinal);
        var candidateEnd = layout.IndexOf("</DataGrid>", candidateStart, StringComparison.Ordinal);
        Assert.True(candidateStart >= 0 && candidateEnd > candidateStart);
        var candidateGrid = layout[candidateStart..candidateEnd];

        Assert.Contains("x:Key=\"CandidateLeftCellTextStyle\"", layout);
        Assert.Contains("x:Key=\"CandidateCenterCellTextStyle\"", layout);
        Assert.Contains("x:Key=\"CandidateIdleStatusCellTextStyle\"", layout);
        Assert.Contains("x:Key=\"CandidateLeftHeaderStyle\"", layout);
        Assert.Contains("x:Key=\"CandidateCenterHeaderStyle\"", layout);
        Assert.Contains("DataGridTemplateColumn Width=\"1.55*\" MinWidth=\"120\" HeaderStyle=\"{StaticResource CandidateLeftHeaderStyle}\"", candidateGrid);
        Assert.Contains("Text=\"{Binding Name}\" Style=\"{StaticResource CandidateLeftCellTextStyle}\"", candidateGrid);
        Assert.Contains("Visibility=\"{Binding HasPartialProtectionBadge, Converter={StaticResource BooleanToVisibilityConverter}}\"", candidateGrid);
        Assert.Contains("Margin=\"8,0,0,0\" TextTrimming=\"CharacterEllipsis\"", candidateGrid);
        Assert.Contains("Width=\"15\" Height=\"15\" Margin=\"4,0,0,0\" Visibility=\"{Binding HasPartialProtectionBadge", candidateGrid);
        Assert.Contains("Visibility=\"{Binding HasRetentionIcon, Converter={StaticResource BooleanToVisibilityConverter}}\" Content=\"{Binding}\" ContentTemplate=\"{StaticResource RetentionStatusIconTemplate}\"", candidateGrid);
        Assert.Contains("Text=\"{Binding IdleStatus}\"", candidateGrid);
        Assert.Contains("ToolTip=\"{Binding IdleStatusDetail}\"", candidateGrid);
        Assert.Contains("<TranslateTransform Y=\"0\" />", layout);
        Assert.Contains("<Setter Property=\"Width\" Value=\"14\" />", layout);
        Assert.Contains("<Grid Width=\"14\" Height=\"14\">", layout);
        Assert.Contains("Binding=\"{Binding AutoOptimizationStatus}\" Width=\"1.5*\" MinWidth=\"120\"", layout);
        Assert.Contains("<Setter Property=\"ToolTip\" Value=\"{Binding Text, RelativeSource={RelativeSource Self}}\" />", layout);
        foreach (var binding in new[] { "Memory", "Ranking", "AutoOptimizationStatus" })
        {
            Assert.Contains($"Binding=\"{{Binding {binding}}}\"", layout);
        }
        Assert.DoesNotContain("Binding=\"{Binding Activity}\"", candidateGrid);
        Assert.Equal(2, candidateGrid.Split("HeaderStyle=\"{StaticResource CandidateCenterHeaderStyle}\" ElementStyle=\"{StaticResource CandidateCenterCellTextStyle}\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Binding=\"{Binding Memory}\" Width=\"1.35*\" MinWidth=\"130\" HeaderStyle=\"{StaticResource CandidateCenterHeaderStyle}\" ElementStyle=\"{StaticResource CandidateMemoryCellTextStyle}\"", candidateGrid);
        Assert.Contains("DataGridTemplateColumn Width=\"1.45*\" MinWidth=\"130\" HeaderStyle=\"{StaticResource CandidateCenterHeaderStyle}\"", candidateGrid);
        Assert.Contains("<Setter Property=\"ToolTip\" Value=\"{Binding IdleStatusDetail}\" />", layout);
    }

    [Fact]
    public void MainWindowUsesStableNativeStyleWithoutRuntimeRewrites()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("SourceInitialized=\"MainWindow_OnSourceInitialized\"", layout);
        Assert.Contains("private void MainWindow_OnSourceInitialized", code);
        Assert.DoesNotContain("WindowThemeService.EnableNativeWindowAnimations(this)", code);
        Assert.DoesNotContain("WsCaption", code);
        Assert.DoesNotContain("SetWindowLong", code);
        Assert.DoesNotContain("GetWindowLong", code);
    }

    [Fact]
    public void IgnoredPressureThresholdUsesTextInsteadOfMaximumSliderValue()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"TriggerPercentValueRun\"", layout);
        Assert.Contains("T(\"TriggerThresholdIgnored\")", code);
        Assert.Contains("TriggerPercentSlider.Visibility = ignored ? Visibility.Collapsed : Visibility.Visible", code);
    }

    [Fact]
    public void CandidatePreviewUsesAutomaticProfileRulesWithoutMemoryPressureFiltering()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("ResolveOptimizationSettings(manual: false)", code);
        Assert.Contains("IgnoreMemoryPressureThreshold = true", code);
        Assert.Contains("MaxApplications = int.MaxValue", code);
        Assert.Contains("CandidateDisplayLimitPolicy.Normalize(_settings.CandidateDisplayLimit)", code);
        Assert.Contains("previewCandidates.Take(displayLimit)", code);
        Assert.DoesNotContain("UpdatePreviewRows(manual: true)", code);
    }

    [Fact]
    public void CandidateHeaderExposesASeparateCompactDisplayLimitMenu()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"CandidateDisplayMenuButton\"", layout);
        Assert.Contains("x:Name=\"CandidateDisplayPopup\"", layout);
        Assert.Contains("x:Name=\"CandidateDisplay10Button\" Tag=\"10\"", layout);
        Assert.Contains("x:Name=\"CandidateDisplay20Button\" Tag=\"20\"", layout);
        Assert.Contains("x:Name=\"CandidateDisplay40Button\" Tag=\"40\"", layout);
        Assert.Contains("x:Name=\"CandidateDisplayUnlimitedButton\" Tag=\"0\"", layout);
        Assert.DoesNotContain("CandidateDisplayMenuButton_OnPreviewMouseLeftButtonUp", layout);
        Assert.Contains("settings.CandidateDisplayLimit = requested", code);
        Assert.Contains("ReferenceEquals(button, CandidateDisplayMenuButton) ? CandidateDisplayPopup", code);
        Assert.DoesNotContain("_candidateDisplayPopupClosedOnButtonPress", code);
    }

    [Fact]
    public void CandidateModeMenuIsCompactAndClearlyMarksTheSelection()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        var popupStart = layout.IndexOf("x:Name=\"CandidateModePopup\"", StringComparison.Ordinal);
        var popupEnd = layout.IndexOf("</Popup>", popupStart, StringComparison.Ordinal);
        Assert.True(popupStart >= 0 && popupEnd > popupStart);
        Assert.Contains("Width=\"132\"", layout[popupStart..popupEnd]);
        Assert.Contains("ApplyPopupSelectionVisual(StandardCandidateModeButton", code);
        Assert.Contains("ApplyPopupSelectionVisual(QuickCandidateModeButton", code);
        Assert.Contains("button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, \"AccentSoftBrush\")", code);
        Assert.DoesNotContain("button.BorderBrush = (MediaBrush)FindResource(\"AccentBrush\")", code);
        Assert.Contains("x:Name=\"StandardCandidateModeCheck\" FontFamily=\"{StaticResource IconFontFamily}\" Text=\"&#xE73E;\" HorizontalAlignment=\"Left\"", layout[popupStart..popupEnd]);
        Assert.Contains("Text=\"{DynamicResource StandardCandidateMode}\" HorizontalAlignment=\"Center\"", layout[popupStart..popupEnd]);
        Assert.Contains("Text=\"{DynamicResource QuickCandidateMode}\" HorizontalAlignment=\"Center\"", layout[popupStart..popupEnd]);
    }

    [Fact]
    public void OptimizeSplitButtonExposesManualStyleScheduledIntervals()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"ScheduledOptimizationCheckBox\"", layout);
        Assert.Contains("x:Name=\"ScheduledOptimizationIntervalBox\"", layout);
        Assert.Contains("x:Name=\"ScheduleMenuButton\"", layout);
        Assert.Contains("x:Name=\"SchedulePopup\"", layout);
        Assert.Contains("Foreground=\"{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}\"", layout);
        Assert.DoesNotContain("ScheduleMenuButton_OnPreviewMouseLeftButtonUp", layout);
        Assert.Contains("Closed=\"SchedulePopup_OnClosed\"", layout);
        Assert.Contains("Deactivated=\"MainWindow_OnDeactivated\"", layout);
        Assert.Contains("Tag=\"1\"", layout);
        Assert.Contains("Tag=\"60\"", layout);
        Assert.Contains("Tag=\"custom\"", layout);
        Assert.Contains("x:Name=\"ScheduledCustomIntervalTextBox\"", layout);
        Assert.Contains("ScheduledOptimizationPolicy.MaximumIntervalMinutes", code);
        Assert.Contains("!IsScheduledOptimizationUnavailable()", code);
        Assert.Contains("ScheduleMenuButton.IsEnabled = !unavailable", code);
        Assert.Contains("private bool TryClosePopupFromTrigger", code);
        Assert.Contains("ReferenceEquals(button, ScheduleMenuButton) ? SchedulePopup", code);
        Assert.Contains("ReferenceEquals(button, CandidateModeMenuButton) ? CandidateModePopup", code);
        Assert.Contains("_suppressedPopupTriggerClicks.Add(button);", code);
        Assert.Contains("ConsumeSuppressedPopupTriggerClick(sender)", code);
        Assert.Contains("<Style x:Key=\"AnimatedPopupStyle\" TargetType=\"Popup\">", layout);
        Assert.Contains("<Style TargetType=\"Popup\" BasedOn=\"{StaticResource AnimatedPopupStyle}\" />", layout);
        Assert.Contains("<Setter Property=\"PopupAnimation\" Value=\"Slide\" />", layout);
        Assert.Contains("<Setter Property=\"PopupAnimation\" Value=\"None\" />", layout);
        Assert.Contains("scheduled: true,\n                        snapshotAlreadyRefreshed: true", code);
        Assert.Contains("RunOptimizationAsync(manual: false, snapshotAlreadyRefreshed: true)", code);
        var scheduledDue = code.IndexOf("var scheduledOptimizationDue", StringComparison.Ordinal);
        Assert.True(scheduledDue >= 0);
        var scheduledBranch = code.IndexOf("if (scheduledOptimizationDue)", scheduledDue, StringComparison.Ordinal);
        var sharedSafetyCheck = code.IndexOf("CanRunUnattendedOptimization()", scheduledDue, StringComparison.Ordinal);
        Assert.True(sharedSafetyCheck > scheduledDue && sharedSafetyCheck < scheduledBranch);
        Assert.Contains("AutomaticOptimizationSafetyWindow.ShouldStart(manual, scheduled)", code);
        Assert.Contains("enforceUnattendedSafety: scheduled", code);
        Assert.Contains("learnOutcome: applicationRule is null", code);
        Assert.DoesNotContain("_settings.AutoOptimization && _settings.ScheduledOptimizationEnabled", code);
    }

    [Fact]
    public void ApplicationRuleDialogDisablesIntervalForSingleExecution()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var dialog = MethodBody(
            code,
            "private ApplicationOptimizationRule? ShowApplicationOptimizationRuleDialog",
            "private System.Windows.Controls.TextBox RuleTextBox");

        Assert.Contains("UpdateExecutionIntervalState", dialog);
        Assert.Contains("executionInterval.IsEnabled", dialog);
        Assert.Contains("count != 1", dialog);
        Assert.Contains("executionCount.TextChanged", dialog);
        Assert.Contains("repeatIndefinitely.IsChecked == true", dialog);
        Assert.Contains("repeatIndefinitely.Checked", dialog);
        Assert.Contains("result.RepeatIndefinitely = delayed", dialog);
    }

    [Fact]
    public void ProtectionGroupRuleSnapshotsProtectedExecutablesAsOneCombinedTarget()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var group = MethodBody(
            code,
            "private async void SetApplicationRule_OnClick",
            "private async void SetApplicationRuleExecutable_OnClick");
        var executable = MethodBody(
            code,
            "private async void SetApplicationRuleExecutable_OnClick",
            "private async void EditProtectionGroup_OnClick");

        Assert.Contains("ApplicationOptimizationTargetType.ExecutableGroup", group);
        Assert.Contains("group.Executables.Count > 0", group);
        Assert.Contains("group.Executables.Select(executable => executable.Path).ToList()", group);
        Assert.Contains("new List<string> { group.Path }", group);
        Assert.Contains("ApplicationOptimizationTargetType.Executable", executable);
        Assert.DoesNotContain("ExecutableGroup", executable);
    }

    [Fact]
    public void ApplicationRuleSaveRejectsTargetsAlreadyInsideAFixedGroup()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var configure = MethodBody(
            code,
            "private async Task ConfigureApplicationRuleAsync",
            "private void RefreshApplicationRuleList");
        var edit = MethodBody(
            code,
            "private void EditApplicationRule_OnClick",
            "private void ToggleApplicationRule_OnClick");
        var dialog = MethodBody(
            code,
            "private ApplicationOptimizationRule? ShowApplicationOptimizationRuleDialog",
            "private System.Windows.Controls.TextBox RuleTextBox");

        Assert.Contains("HasApplicationRuleTargetConflict", configure);
        Assert.Contains("HasApplicationRuleTargetConflict(edited, edited.Id)", edit);
        Assert.Contains("ApplicationOptimizationRulePolicy.TargetsOverlap", dialog);
        Assert.Contains("ApplicationRuleTargetConflictInRule", dialog);
        Assert.Contains("ApplicationRuleTargetConflictFormat", code);
    }

    [Fact]
    public void ProtectionRuleMenusUseLeftClickThemedPopups()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var start = layout.IndexOf("<Grid x:Name=\"ProtectionPage\"", StringComparison.Ordinal);
        var end = layout.IndexOf("<Grid x:Name=\"HistoryPage\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var protectionPage = layout[start..end];

        Assert.DoesNotContain("<Button.ContextMenu>", protectionPage);
        Assert.Equal(2, CountOccurrences(protectionPage, "Click=\"ApplicationRuleMenuButton_OnClick\""));
        Assert.Equal(2, CountOccurrences(protectionPage, "Closed=\"ApplicationRulePopup_OnClosed\""));
        Assert.Equal(2, CountOccurrences(protectionPage, "StaysOpen=\"True\" AllowsTransparency=\"True\""));
        Assert.Equal(2, CountOccurrences(protectionPage, "PreviewMouseLeftButtonDown=\"ManagedPopupTrigger_OnPreviewMouseLeftButtonDown\""));
        Assert.Contains("Background=\"{DynamicResource SurfaceBrush}\"", protectionPage);
        Assert.Contains("BorderBrush=\"{DynamicResource BorderBrush}\"", protectionPage);
        Assert.Equal(2, CountOccurrences(protectionPage, "<Border MinWidth=\"188\" Padding=\"6\""));
        Assert.DoesNotContain("<Border Width=\"240\" Padding=\"6\"", protectionPage);
        Assert.DoesNotContain("<Border Width=\"220\" Padding=\"6\"", protectionPage);
        Assert.Contains("private void ApplicationRuleMenuButton_OnClick", code);
        Assert.Contains("parent.Children.OfType<Popup>().FirstOrDefault", code);
        Assert.DoesNotContain("_applicationRulePopupButtonsClosedOnPress", code);
        Assert.Contains("<Style x:Key=\"AnimatedPopupStyle\" TargetType=\"Popup\">", layout);
        Assert.Contains("Style=\"{StaticResource PopupMenuButtonStyle}\"", protectionPage);
        Assert.Contains("x:Key=\"AnimatedExpansionItemsStyle\"", layout);
        Assert.Equal(2, CountOccurrences(protectionPage, "Style=\"{StaticResource AnimatedExpansionItemsStyle}\""));
        Assert.Contains("Property=\"local:ExpansionMotion.IsExpanded\" Value=\"{Binding IsExpanded}\"", layout);
        Assert.DoesNotContain("Storyboard.TargetProperty=\"MaxHeight\"", layout);
        var expansionMotion = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ExpansionMotion.cs"));
        Assert.Contains("element.DesiredSize.Height", expansionMotion);
        Assert.Contains("MotionDuration(targetHeight)", expansionMotion);
        Assert.Contains("BeginTime = TimeSpan.FromMilliseconds(35)", expansionMotion);
        Assert.Contains("Math.Max(element.ActualHeight, cachedHeight)", expansionMotion);
        Assert.Contains("MotionDuration(currentHeight)", expansionMotion);
        Assert.Contains("TimeSpan.FromMilliseconds(340)", expansionMotion);
        Assert.Contains("<Setter Property=\"PopupAnimation\" Value=\"Slide\" />", layout);
    }

    [Fact]
    public void ApplicationRuleDialogStartsOptInAndGroupsRelatedControls()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var dialog = MethodBody(
            code,
            "private ApplicationOptimizationRule? ShowApplicationOptimizationRuleDialog",
            "private System.Windows.Controls.TextBox RuleTextBox");

        Assert.Contains("ApplicationRuleFollowAutomatic", dialog);
        Assert.Contains("ApplicationOptimizationRuleTriggerMode.FollowAutomatic", dialog);
        Assert.Contains("existing?.DelayTriggerEnabled ?? false", dialog);
        Assert.Contains("existing?.WorkingSetTriggerEnabled ?? false", dialog);
        Assert.Contains("existing?.RestartWithApplication ?? true", dialog);
        Assert.Contains("existing?.WorkingSetThresholdFollowsProfile ?? existing is null", dialog);
        Assert.Contains("Visibility = Visibility.Collapsed", dialog);
        Assert.Contains("T(\"ApplicationRuleAddTarget\")", dialog);
        Assert.Contains("T(\"ApplicationRuleTriggerSettings\")", dialog);
        Assert.Contains("SelectTriggerMode(followAutomatic, delayEnabled)", dialog);
        Assert.Contains("SelectTriggerMode(delayEnabled, followAutomatic)", dialog);
        Assert.Contains("workingSetEnabled.IsChecked = true", dialog);
        Assert.Contains("followProfileThreshold.IsChecked = false", dialog);
        Assert.Contains("workingSetMiB.IsEnabled = followsAutomatic", dialog);
        Assert.Contains("followProfileThreshold.IsChecked != true", dialog);
        Assert.Contains("ApplicationRuleWorkingSetThresholdHelp", dialog);
        Assert.Contains("validation.Visibility = Visibility.Visible", dialog);
        Assert.DoesNotContain("MessageBox.Show(dialog", dialog);
        Assert.Contains("Visibility = draftTargets.Count > 1 ? Visibility.Visible : Visibility.Collapsed", dialog);
        Assert.Contains("Content = T(\"DeleteApplicationRule\")", dialog);
        Assert.Contains("Grid.SetColumn(delete, 0)", dialog);
        Assert.Contains("Grid.SetColumn(buttons, 2)", dialog);
    }

    [Fact]
    public void ApplicationRulesCanStartFromMultipleRunningApplications()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"NewRunningApplicationRuleButton\"", layout);
        Assert.Contains("Click=\"NewRunningApplicationRule_OnClick\"", layout);
        var handler = MethodBody(
            code,
            "private async void NewRunningApplicationRule_OnClick",
            "private IReadOnlyList<ApplicationOptimizationRuleTarget>? ShowRunningApplicationRulePicker");
        var picker = MethodBody(
            code,
            "private IReadOnlyList<ApplicationOptimizationRuleTarget>? ShowRunningApplicationRulePicker",
            "private void EditApplicationRule_OnClick");

        Assert.Contains("RunningProtectionCandidateCatalog.Create", handler);
        Assert.Contains("existingTargets", handler);
        Assert.Contains("targets.Skip(1).ToArray()", handler);
        Assert.Contains("ApplicationOptimizationTargetType.Executable", picker);
        Assert.Contains("ApplicationOptimizationTargetType.ApplicationFamily", picker);
        Assert.Contains("new System.Windows.Controls.CheckBox", picker);
        Assert.Contains("selectionReaders.Any(read => read().Count > 0)", picker);
        Assert.Contains("ApplyDialogTheme(dialog)", picker);
    }

    [Fact]
    public void TransientListSelectionsClearWhenClickingElsewhere()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("PreviewMouseLeftButtonDown=\"MainWindow_OnPreviewMouseLeftButtonDown\"", layout);
        Assert.Contains("x:Name=\"HistoryList\"", layout);
        Assert.Contains("ProcessesGrid.UnselectAll()", code);
        Assert.Contains("HistoryList.UnselectAll()", code);
        Assert.Contains("CandidatesGrid.UnselectAll()", code);
    }

    [Fact]
    public void ApplicationContextMenuCommandsUseTheTargetCapturedWhenTheMenuOpened()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var update = MethodBody(
            code,
            "private void UpdateApplicationContextMenu",
            "private RunningProtectionCandidate? CreateProtectionCandidate");
        var protectProcess = MethodBody(
            code,
            "private void ProtectSelectedApplication_OnClick",
            "private void ProtectCandidate_OnClick");
        var protectCandidate = MethodBody(
            code,
            "private void ProtectCandidate_OnClick",
            "private void ProtectApplication");
        var optimizeProcess = MethodBody(
            code,
            "private async void OptimizeSelectedApplication_OnClick",
            "private async void OptimizeCandidate_OnClick");
        var optimizeCandidate = MethodBody(
            code,
            "private async void OptimizeCandidate_OnClick",
            "private async Task OptimizeApplicationAsync");

        Assert.Contains("optimizeItem.Tag = row", update);
        Assert.Contains("protectItem.Tag = row", update);
        foreach (var handler in new[] { protectProcess, protectCandidate, optimizeProcess, optimizeCandidate })
        {
            Assert.Contains("Tag is not ProcessRow row", handler);
            Assert.DoesNotContain("SelectedItem is not ProcessRow row", handler);
        }
    }

    [Fact]
    public void ProgrammaticBorderlessDialogsReceiveOneThemeFrame()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var theme = MethodBody(code, "private void ApplyDialogTheme", "private async Task CloseApplicationsAsync");

        Assert.Contains("dialog.Content is UIElement content", theme);
        Assert.Contains("content is not Border", theme);
        Assert.Contains("BorderBrush = (MediaBrush)FindResource(\"BorderBrush\")", theme);
        Assert.Contains("BorderThickness = new Thickness(1)", theme);
        Assert.Contains("WindowThemeService.EnableNativeWindowAnimations(dialog)", theme);
        var detach = theme.IndexOf("dialog.Content = null", StringComparison.Ordinal);
        var attachChild = theme.IndexOf("frame.Child = content", StringComparison.Ordinal);
        var attachFrame = theme.IndexOf("dialog.Content = frame", StringComparison.Ordinal);
        Assert.True(detach >= 0 && attachChild > detach && attachFrame > attachChild);
    }

    [Fact]
    public void AppOwnedMessagesAndTraySurfacesUseMuseTheme()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var app = File.ReadAllText(AppFixturePath());
        var startupDialog = File.ReadAllText(StartupDialogFixturePath());

        Assert.DoesNotContain("MessageBox.Show", code);
        Assert.DoesNotContain("MessageBox.Show", app);
        Assert.DoesNotContain("ContextMenuStrip", code);
        Assert.DoesNotContain("ShowBalloonTip", code);
        Assert.Contains("private MessageBoxResult ShowThemedMessage", code);
        Assert.Contains("StartupThemedDialog.Show", app);
        Assert.Contains("WindowStyle = WindowStyle.None", startupDialog);
        Assert.Contains("WindowThemeService.EnableNativeWindowAnimations(dialog)", startupDialog);
        Assert.Contains("AllowsTransparency = false", startupDialog);
        Assert.Contains("new System.Windows.Controls.ContextMenu", code);
        Assert.Contains("Style = (Style)FindResource(\"ThemedContextMenuStyle\")", code);
        Assert.DoesNotContain("ShowTrayNotice", code);
        Assert.DoesNotContain("_trayNoticeWindow", code);
        Assert.Contains("Placement = PlacementMode.MousePoint", code);
        Assert.DoesNotContain("var cursor = Forms.Cursor.Position", code);
        Assert.DoesNotContain("HorizontalOffset = args.X", code);
        Assert.DoesNotContain("VerticalOffset = args.Y", code);
        Assert.Contains("_trayMenu.Opened += TrayMenu_OnOpened", code);
        Assert.Contains("PresentationSource.FromVisual(_trayMenu) is not HwndSource source", code);
        Assert.Contains("SetForegroundWindow(source.Handle)", code);
        Assert.Contains("_trayMenu.Focus()", code);
        Assert.Equal(3, CountOccurrences(code, "new OpenFileDialog"));
    }

    [Fact]
    public void TrayMenuUsesMousePointPlacementAndCurrentThemeResources()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var constructor = MethodBody(
            code,
            "public MainWindow()",
            "private async void MainWindow_OnLoaded");
        var createTrayIcon = MethodBody(
            code,
            "private Forms.NotifyIcon CreateTrayIcon()",
            "private void TrayMenu_OnOpened");
        var applyTheme = MethodBody(
            code,
            "private void ApplyTheme(bool light)",
            "private bool IsLightThemeActive");

        Assert.Contains("Placement = PlacementMode.MousePoint", createTrayIcon);
        Assert.DoesNotContain("PlacementMode.AbsolutePoint", createTrayIcon);
        Assert.DoesNotContain("Forms.Cursor.Position", createTrayIcon);
        Assert.DoesNotContain("VisualTreeHelper.GetDpi(this)", createTrayIcon);
        Assert.DoesNotContain("_trayMenu.HorizontalOffset", createTrayIcon);
        Assert.DoesNotContain("_trayMenu.VerticalOffset", createTrayIcon);
        Assert.DoesNotContain("_trayMenu.Opacity", createTrayIcon);
        Assert.True(CountOccurrences(createTrayIcon, "CopyThemeResources(_trayMenu.Resources)") >= 2);
        Assert.Contains("CopyThemeResources(_trayMenu.Resources)", applyTheme);
        Assert.True(
            constructor.IndexOf("ApplyTheme(IsLightThemeActive())", StringComparison.Ordinal) <
            constructor.IndexOf("_trayIcon = CreateTrayIcon()", StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticOptimizationExposesStandardAndQuickCandidateModes()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"CandidateModeMenuButton\"", layout);
        Assert.Contains("x:Name=\"CandidateModePopup\"", layout);
        Assert.Contains("x:Name=\"StandardCandidateModeButton\"", layout);
        Assert.Contains("x:Name=\"QuickCandidateModeButton\"", layout);
        Assert.Contains("ToolTip=\"{DynamicResource StandardCandidateModeHelp}\"", layout);
        Assert.Contains("ToolTip=\"{DynamicResource QuickCandidateModeHelp}\"", layout);
        Assert.Contains("<Border Width=\"132\" Padding=\"6\"", layout);
        Assert.DoesNotContain("CandidateModeMenuButton_OnPreviewMouseLeftButtonUp", layout);
        Assert.Contains("Closed=\"CandidateModePopup_OnClosed\"", layout);
        Assert.Contains("TryUpdateSettings(settings => settings.QuickCandidateSelection = enabled)", code);
        Assert.Contains("QuickCandidateSelection = !longIdle && (!manual || scheduled) && runSettings.QuickCandidateSelection", code);
        Assert.Contains("ReferenceEquals(button, CandidateModeMenuButton) ? CandidateModePopup", code);
        Assert.DoesNotContain("_candidateModePopupClosedOnButtonPress", code);
    }

    [Fact]
    public void SettingsExposeLongIdleTestStrategyAndIndependentTrigger()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"LongIdleOptimizationCheckBox\"", layout);
        Assert.Contains("x:Name=\"LongIdleMinutesSlider\" Style=\"{StaticResource EditorSliderStyle}\" Minimum=\"30\" Maximum=\"360\"", layout);
        Assert.Contains("LongIdleMinutesPanel.Visibility = requested ? Visibility.Visible : Visibility.Collapsed", code);
        Assert.Contains("Binding=\"{Binding IdleStatus}\"", layout);
        Assert.Contains("Text=\"{DynamicResource IdleStatusColumn}\"", layout);
        Assert.Contains("Text=\"{DynamicResource LongIdleOptimizationHelp}\"", layout);
        Assert.Contains("ToolTip=\"{DynamicResource LongIdleMinutesHelp}\"", layout);
        var appearancePanel = layout.IndexOf("Text=\"{DynamicResource AppearanceMaintenance}\"", StringComparison.Ordinal);
        var languageBox = layout.IndexOf("x:Name=\"LanguageBox\"", StringComparison.Ordinal);
        Assert.True(appearancePanel >= 0 && languageBox > appearancePanel);
        Assert.Contains("OptimizationTriggerKind.LongIdle", code);
        Assert.Contains("CandidatePlanCalibrationPolicy.ApplyLongIdleFilter(", code);
        Assert.Contains("IgnoreMemoryPressureThreshold = longIdle || settings.IgnoreMemoryPressureThreshold", code);
        Assert.DoesNotContain("MaxApplications = longIdle ?", code);
        Assert.Contains("var planningSettings = longIdle", code);
        Assert.Contains("settings with { MaxApplications = 0 }", code);
        Assert.Contains("settings.MaxApplications);", code);
        Assert.Contains("!OptimizationPlanner.HasMemoryPressure", code);
        Assert.Contains("_lastSuccessfulOptimizationAt", code);
    }

    [Fact]
    public void ProtectionControlsExplainScopeAndShowRunningProcessCount()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("ToolTip=\"{DynamicResource AddRunningHelp}\"", layout);
        Assert.Contains("ToolTip=\"{DynamicResource AddExeHelp}\"", layout);
        Assert.Contains("FontSize=\"14\" Margin=\"0,0,6,0\" Text=\"&#xE710;\"", layout);
        Assert.Contains("FontSize=\"14\" Margin=\"0,0,6,0\" Text=\"&#xE8E5;\"", layout);
        Assert.Contains("ToolTip=\"{DynamicResource ProtectedStateHelp}\"", layout);
        Assert.Contains("Text=\"{Binding ProtectedInstanceCount, Mode=OneWay}\"", layout);
        Assert.Contains("Text=\"{Binding InstanceCount, Mode=OneWay}\"", layout);
        Assert.Contains("Text=\"{DynamicResource ProtectedProcessCountSuffix}\"", layout);
        Assert.Contains("parent.ToolTip = T(\"RunningProtectionFamilyCheckHelp\")", code);
        Assert.Contains("child.ToolTip = T(\"RunningProtectionExecutableCheckHelp\")", code);
        Assert.Contains("status.ToolTip = T(\"RunningProtectionStatusHelp\")", code);
        Assert.Contains("Visibility = application.ProcessCount > 1", code);
        Assert.Contains("foreach (var process in executable.Processes)", code);
        Assert.Contains("Visibility = executable.Processes.Count > 1", code);
        Assert.Contains("RunningProtectionProcessFormat", code);
        Assert.Contains("RunningProtectionProcessHelp", code);
    }

    [Fact]
    public void RunningApplicationContextMenuPerformsOneApplicationManualOptimization()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var optimization = MethodBody(code, "private async Task RunOptimizationAsync", "private void ShowNoCandidatesDialog");

        Assert.Contains("x:Name=\"ProcessesContextMenu\"", layout);
        Assert.Contains("Header=\"{DynamicResource OptimizeThisApplication}\"", layout);
        Assert.Contains("await RunOptimizationAsync(manual: true, target: row);", code);
        Assert.Contains("!_settings.SelectedApplicationOptimizationPromptSuppressed", code);
        Assert.Contains("ShowSelectedApplicationOptimizationPrompt(row.Name)", code);
        Assert.Contains("SelectedOptimizationPromptFormat", code);
        Assert.Contains("SelectedOptimizationDoNotRemind", code);
        Assert.Contains("settings.SelectedApplicationOptimizationPromptSuppressed = true", code);
        Assert.Contains("ResolveCurrentTargetFamily(target)", optimization);
        Assert.Contains("ForegroundOptimizationEnhancedSafetyBlocked", optimization);
        Assert.Contains("ForegroundOptimizationConfirmTitle", optimization);
        Assert.Contains("settings = settings with { AllowForegroundProcessTrim = true };", optimization);
        Assert.Contains("SelectedApplicationOptimizationPolicy.Apply(settings)", optimization);
        Assert.Contains("FormatSelectedApplicationExclusion(", optimization);
        Assert.Contains("targetDisplayName ?? targetFamilies[0].DisplayName", optimization);
        Assert.Contains("Candidates = targetCandidates", optimization);
    }

    [Fact]
    public void UnattendedOptimizationPassesStartTheAutomaticSafetyWindowOnEveryExitPath()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var noRunGuard = code.IndexOf("if (!plan.ShouldRun)", StringComparison.Ordinal);
        var startWindow = code.IndexOf(
            "AutomaticOptimizationSafetyWindow.ShouldStart(manual, scheduled)",
            StringComparison.Ordinal);
        var finallyBlock = code.IndexOf("finally", startWindow, StringComparison.Ordinal);
        var recordWindow = code.IndexOf(
            "if (startAutomaticSafetyWindow)",
            StringComparison.Ordinal);
        var recordAnchor = code.IndexOf(
            "_automaticOptimizationSafetyAnchor = DateTimeOffset.Now;",
            StringComparison.Ordinal);
        var clearBusy = code.IndexOf("SetBusyState(false);", recordAnchor, StringComparison.Ordinal);

        Assert.True(noRunGuard >= 0);
        Assert.True(startWindow > noRunGuard);
        Assert.True(finallyBlock > startWindow);
        Assert.True(recordWindow > finallyBlock);
        Assert.True(recordAnchor > recordWindow);
        Assert.True(clearBusy > recordAnchor);
    }

    [Fact]
    public void ManualNoCandidatePromptUsesMuseBorderlessDialog()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var optimization = MethodBody(code, "private async Task RunOptimizationAsync", "private void ShowNoCandidatesDialog");
        var noRunStart = optimization.IndexOf("if (!plan.ShouldRun)", StringComparison.Ordinal);
        var noRunEnd = optimization.IndexOf("startAutomaticSafetyWindow", noRunStart, StringComparison.Ordinal);
        var noRun = optimization[noRunStart..noRunEnd];

        Assert.Contains("ShowNoCandidatesDialog(targetFamilies.Length == 0", code);
        Assert.Contains("messageText ?? T(\"NoCandidatesDialogMessage\")", code);
        Assert.Contains("WindowStyle = WindowStyle.None", code);
        Assert.Contains("AllowsTransparency = true", code);
        Assert.DoesNotContain("MessageBox.Show(this, T(\"PlanNoCandidates\")", code);
        Assert.Contains("if (manual && !scheduled)", noRun);
        Assert.Equal(1, CountOccurrences(noRun, "AddHistory("));
        Assert.Contains("OptimizationPlanOutcome.LowMemoryPressure => \"PlanLowPressure\"", noRun);
        Assert.Contains("OptimizationPlanOutcome.NoCandidates => \"PlanNoCandidates\"", noRun);
    }

    [Fact]
    public void ProfilePressureOverrideShowsTheEffectiveStateAndIsReadOnlyWhenBuiltIn()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Name=\"IgnoreMemoryPressureThresholdCheckBox\"", layout);
        Assert.Contains("ActiveProfileIgnoresMemoryPressureThreshold", code);
        Assert.Contains("ResolveOptimizationSettings(manual: false).IgnoreMemoryPressureThreshold", code);
        Assert.Contains("IsEnabled = !profileIgnoresMemoryPressure", code);
    }

    [Fact]
    public void OptimizationCenterShowsLearningAndPressureOverrideSideBySide()
    {
        var layout = File.ReadAllText(FixturePath());

        Assert.Contains("<Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width=\"12\"/><ColumnDefinition/></Grid.ColumnDefinitions>", layout);
        Assert.Contains("x:Name=\"OverviewBenefitLearningCheckBox\"", layout);
        Assert.Contains("x:Name=\"IgnoreMemoryPressureThresholdCheckBox\"", layout);
        Assert.Contains("{DynamicResource IgnoreMemoryPressureShort}", layout);
        Assert.Equal(1, CountOccurrences(layout, "x:Name=\"IgnoreMemoryPressureThresholdCheckBox\""));
    }

    [Fact]
    public void CandidateRefreshDoesNotResetTheWholeCollection()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("SynchronizeCollection(_state.Candidates", code);
        Assert.DoesNotContain("ReplaceCollection(_state.Candidates", code);
        Assert.Contains("activityFamily.HasMinimizedWindow", code);
    }

    [Fact]
    public void ProcessRefreshDoesNotResetTheWholeCollection()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var method = MethodBody(
            code,
            "private void UpdateProcessRows()",
            "private void UpdatePreviewRows()");

        Assert.Contains("SynchronizeCollection(", method);
        Assert.DoesNotContain("ReplaceCollection(", method);
        Assert.Contains("selectedFamilyKey", method);
        Assert.Contains("ProcessesGrid.SelectedItem", method);
        Assert.Contains("row.Family.Key", method);
        Assert.Contains("ProcessRetentionPresentation.Resolve", code);
        Assert.Contains("ProcessStatusSessionStable", code);
        Assert.Contains("CandidateStableStateSuppressed", code);
        Assert.Contains("ProcessStatusStableObservation", code);
        Assert.Contains("ProcessStatusStableObservationActiveDetailFormat", code);
        Assert.Contains("ProcessStatusStableReviewActiveDetailFormat", code);
        Assert.Contains("ProcessStatusStableReviewRollingDetailFormat", code);
        Assert.Contains("ProcessStatusStableReviewRollingActiveDetailFormat", code);
        Assert.Contains("StableStateRetainedDuringReviewFormat", code);
        Assert.Contains("observation.HasFiniteDeadline", code);
        Assert.Contains("ProcessStatusStableReview", code);
        var stableReviewStart = code.IndexOf(
            "ProcessRetentionIndicator.NaturalStableReview =>", StringComparison.Ordinal);
        var stableGrowthReviewStart = code.IndexOf(
            "ProcessRetentionIndicator.NaturalStableGrowthReview =>",
            stableReviewStart,
            StringComparison.Ordinal);
        Assert.True(stableReviewStart >= 0 && stableGrowthReviewStart > stableReviewStart);
        var stableReviewBranch = code[stableReviewStart..stableGrowthReviewStart];
        Assert.Contains("StableObservationPhase.ProvisionalValidation", stableReviewBranch);
        Assert.Contains("FormatStableValidationObservation", stableReviewBranch);
        Assert.Contains("FormatStableReviewObservation", stableReviewBranch);
        Assert.Contains("ProcessStatusBenefitObservation", code);
        Assert.Contains("ProcessStatusBenefitObservationWithHistoricalStable", code);
        Assert.Contains("ProcessRetentionIndicator.BenefitObservationWithHistoricalStable =>\n                (T(\"ProcessStatusBenefitObservation\")", code);
        Assert.Contains("ProcessStatusCandidateReady", code);
        Assert.Contains("HasActiveLongTermStableReference(", code);
        Assert.Contains("record.ComponentKeys.All(components.ContainsKey)", code);
        Assert.Contains("return workingSet <= limit;", code);
    }

    [Fact]
    public void RetentionStatusIconsDistinguishStableHoldingFromReview()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("ProcessRetentionIndicator.SessionStableState => RetentionStatusIcon.SessionStable", code);
        Assert.Contains("ProcessRetentionIndicator.LongTermStableState => RetentionStatusIcon.Stable", code);
        Assert.Contains("ProcessRetentionIndicator.NaturalStableObservation => RetentionStatusIcon.StableObserving", code);
        Assert.Contains("Stroke=\"{DynamicResource WarningBrush}\"", layout);
        Assert.Contains("RetentionStatusIcon.SessionStable", layout);
        Assert.Contains("RetentionStatusIcon.StableObserving", layout);
        Assert.Contains("ProcessRetentionIndicator.NaturalStableReview => RetentionStatusIcon.Review", code);
        Assert.Contains("ProcessRetentionIndicator.NaturalStableGrowthReview => RetentionStatusIcon.GrowthReview", code);
        Assert.Contains("x:Name=\"GrowthReviewIcon\"", layout);
        Assert.Contains("RetentionStatusIcon.GrowthReview", layout);
        Assert.Contains("IconCircleCheck", layout);
        Assert.Contains("IconLoaderCircle", layout);
        Assert.Contains("Angle=\"{Binding ReviewSpinnerAngle, Source={x:Static local:MotionPolicy.Current}}\"", layout);
        Assert.DoesNotContain("RetentionReviewSpinner", layout);
        Assert.Contains("RetentionStatusIcon.Idle", layout);
        Assert.Contains("IconClock3", layout);
        Assert.Contains("retentionIndicator is ProcessRetentionIndicator.None", code);
    }

    [Fact]
    public void RetentionStatusIconsDistinguishPartialProtectionAndObservation()
    {
        var layout = File.ReadAllText(FixturePath());
        var resources = File.ReadAllText(IconResourcesFixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("ProcessRetentionIndicator.PartialProtection => RetentionStatusIcon.PartiallyProtected", code);
        Assert.Contains("RetentionStatusIcon.PartiallyProtected", layout);
        Assert.Contains("IconShieldPartial", resources);
        Assert.Contains("ProcessRetentionIndicator.BenefitObservationWithHistoricalStable => RetentionStatusIcon.Observing", code);
        Assert.Contains("RetentionStatusIcon.Observing", layout);
        Assert.Contains("x:Name=\"ActivityObservingIcon\"", layout);
        Assert.Contains("RetentionStatusIcon.ActivityObserving", layout);
        Assert.Contains("string.Equals(row.IdleStatus, T(\"ActivityMinimized\"), StringComparison.Ordinal)", code);
        Assert.Contains("ProcessRetentionIndicator.PartialProtection", code);
        Assert.Contains("row = row with { RetentionIcon = RetentionStatusIcon.ActivityObserving };", code);
        Assert.Contains("IconEye", resources);
        Assert.Contains("ProcessRetentionIndicator.AutomaticBackoff => RetentionStatusIcon.Backoff", code);
        Assert.Contains("RetentionStatusIcon.Backoff", layout);
        Assert.Contains("IconCirclePause", resources);
        Assert.Contains("ProcessRetentionIndicator.Foreground or", code);
    }

    [Fact]
    public void CandidatePauseUsesSharedLifecycleResolverAndFormatter()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var method = MethodBody(
            code,
            "private (string Status, string? Detail, RetentionStatusIcon Icon) FormatCandidatePause(",
            "private ProcessRow CreateCandidateRow(");

        Assert.Contains("ProcessRetentionPresentation.Resolve(", method);
        Assert.Contains("FormatRetentionIndicator", method);
        Assert.DoesNotContain("DateTimeOffset.Now", method);
        Assert.Contains("PendingObservationComponentKeys(now)", method);
        Assert.Contains("evaluation.ExclusionReasons", method);
        Assert.Contains("isEligible: evaluation.IsEligible", method);
        Assert.Contains("hasProcessableTargets: evaluation.TargetProcessCount > 0", method);
        Assert.Contains("stableComponentKeys = activeStableRecord", method);
        Assert.Contains("!pendingBenefitObservation && stableComponentKeys.Any(", method);
        Assert.Contains("family.Key,", method);
        Assert.Contains("stableComponentKeys,", method);
        Assert.Contains("StableStateSuppressionPolicy.CurrentNaturalStableLaunchSignature(", method);
        Assert.Contains("family, stableComponentKeys, statuses", method);
        Assert.Contains("NaturalStableGrowthReviewComponentKeys", method);
        Assert.Contains("NaturalStableProvisionalValidationComponentKeys", method);
        Assert.Contains("NaturalStableReviewComponentKeys", method);
        Assert.Contains("NaturalStableObservationComponentKeys", method);
        Assert.Contains("StableStateSuppressionPolicy.ActiveStableRecord(", method);
        Assert.Contains("StableStateSuppressionPolicy.SuppressionLimitBytes(", method);
        Assert.Contains("hasLongTermStableReference: hasLongTermStableReference", method);
        Assert.Contains("RetentionIconFor(indicator)", method);

        var refresh = MethodBody(
            code,
            "private void UpdatePreviewRows()",
            "private (string Status, string? Detail, RetentionStatusIcon Icon) FormatCandidatePause(");
        Assert.Contains("pausedEvaluations[family.Key] = evaluation", refresh);
        Assert.Contains("DisplayProcessableWorkingSetBytes(candidate.Family)", refresh);
        Assert.Contains("DisplayFormat.Bytes(candidate.Family.WorkingSetBytes)", refresh);
        Assert.Contains("MemoryBytes = displayWorkingSetBytes", refresh);
        Assert.Contains("RetentionIcon = pause.Icon", refresh);
    }

    [Fact]
    public void MemoryDisplayUsesTechnicalAndWholeFamilyWorkingSetsWithoutChangingDecisions()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var candidate = MethodBody(
            code,
            "private ProcessRow CreateCandidateRow(",
            "private ProcessRow CreateProcessRow(");
        var running = MethodBody(
            code,
            "private ProcessRow CreateProcessRow(",
            "private RunningProtectionCandidate? CreateProtectionCandidate(");

        Assert.Contains("DisplayProcessableWorkingSetBytes(originalFamily)", candidate);
        Assert.Contains("DisplayFormat.Bytes(originalFamily.WorkingSetBytes)", candidate);
        Assert.Contains("DisplayProcessableWorkingSetBytes(family)", running);
        Assert.Contains("DisplayFormat.Bytes(family.WorkingSetBytes)", running);
        Assert.Contains("evaluation?.ExclusionReasons", running);
    }

    [Fact]
    public void TrimHistoryIsBoundToProcessIdentityAndPrunedDuringRefresh()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var refresh = MethodBody(
            code,
            "private async Task<bool> RefreshSnapshotAsync",
            "private bool CanRunUnattendedOptimization");
        var optimization = MethodBody(
            code,
            "private async Task RunOptimizationAsync",
            "private void ShowNoCandidatesDialog");
        var ioTelemetry = MethodBody(
            code,
            "private void RecordProcessActivitySamples",
            "private void RecordLargeMemoryOpportunityIfDue");

        Assert.Contains("PruneLastTrimHistory(Array.Empty<ProcessSnapshot>()", refresh);
        Assert.Contains("_processSampler.Capture(\n                    lastTrimTimes,\n                    lastTrimProcessStartTimes)", refresh);
        Assert.Contains("Stopwatch.GetElapsedTime(started)", refresh);
        Assert.Contains("PruneLastTrimHistory(processes", refresh);
        Assert.Contains("_lastTrimProcessStartTimes[process.ProcessId] = startTimeFileTimeUtc", optimization);
        Assert.Contains("ProcessTrimHistoryPolicy.ShouldDiscard", code);
        Assert.Contains("lastTrimProcessStartTimes: _lastTrimProcessStartTimes", ioTelemetry);
        Assert.Equal(5, CountOccurrences(code, "lastTrimProcessStartTimes: _lastTrimProcessStartTimes"));
        Assert.Contains("RecordProcessActivitySamples(activityObservedAt)", refresh);
        var expireBeforeCapture = refresh.IndexOf(
            "PruneLastTrimHistory(Array.Empty<ProcessSnapshot>()",
            StringComparison.Ordinal);
        var capture = refresh.IndexOf("_processSampler.Capture(", StringComparison.Ordinal);
        var reconcileAfterCapture = refresh.IndexOf("PruneLastTrimHistory(processes", StringComparison.Ordinal);
        var groupFamilies = refresh.IndexOf("_families = ApplicationFamilyGrouper.Group(processes)", StringComparison.Ordinal);
        Assert.True(expireBeforeCapture >= 0 && expireBeforeCapture < capture);
        Assert.True(capture < reconcileAfterCapture && reconcileAfterCapture < groupFamilies);
    }

    [Fact]
    public void ProtectedGroupRefreshReplacesOnlyChangedContent()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var method = MethodBody(
            code,
            "private void RefreshProtectedList()",
            "private static bool ProtectedGroupsEqual");

        var equalityCheck = method.IndexOf("ProtectedGroupsEqual(", StringComparison.Ordinal);
        var replace = method.IndexOf("ReplaceCollection(_state.ProtectedApplications", StringComparison.Ordinal);
        Assert.True(equalityCheck >= 0 && replace > equalityCheck);
        Assert.Contains("ExpansionMotion.IsAnyAnimationActive", method);
        Assert.DoesNotContain("ProtectedPathEntries", method);
        Assert.DoesNotContain("ProtectedApplications.Clear()", method);
    }

    [Fact]
    public void ProtectionPageSupportsOneTimeFamilyAndExecutableOptimization()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var optimization = MethodBody(
            code,
            "private async Task RunOptimizationAsync",
            "private void ShowNoCandidatesDialog");

        Assert.Contains("Click=\"OptimizeProtectedGroup_OnClick\"", layout);
        Assert.Contains("Click=\"OptimizeProtectedExecutable_OnClick\"", layout);
        Assert.Equal(2, CountOccurrences(layout, "IsEnabled=\"{Binding IsRunning}\""));
        Assert.Contains("ProtectedOptimizationTarget", code);
        Assert.Contains("new ProtectionRules(Array.Empty<ApplicationProtectionRule>())", optimization);
        Assert.Contains("protectedTarget?.ExecutablePaths", optimization);
        Assert.Contains("ResolveProtectedTargetFamilies(protectedTarget)", optimization);
        Assert.Contains("TargetProcesses = targetProcesses", optimization);
    }

    [Fact]
    public void ProtectionSuggestionsShimmerUntilOpeningAndAreMarkedViewedFirst()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var click = MethodBody(
            code,
            "private async void ProtectionSuggestions_OnClick",
            "private void MarkProtectionSuggestionsViewed");
        var markViewed = MethodBody(
            code,
            "private void MarkProtectionSuggestionsViewed",
            "private void ReviewProtectionSuggestions_OnClick");
        var review = MethodBody(
            code,
            "private void ReviewProtectionSuggestions_OnClick",
            "private void RefreshBenefitLearningAnalysis");

        Assert.Contains("x:Name=\"ProtectionSuggestionShimmer\"", layout);
        Assert.Contains("<TextBlock.OpacityMask>", layout);
        Assert.Contains("x:Name=\"ProtectionSuggestionShimmerStart\" Color=\"#00FFFFFF\" Offset=\"0.38\"", layout);
        Assert.Contains("x:Name=\"ProtectionSuggestionShimmerEnd\" Color=\"#00FFFFFF\" Offset=\"0.62\"", layout);
        Assert.Contains("RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever", code);
        Assert.Contains("ProtectionSuggestionShimmerTransform.BeginAnimation(", code);
        Assert.Contains("Duration = TimeSpan.FromSeconds(2.8)", code);
        Assert.Contains("ProtectionSuggestionButtonText.Opacity = 0.55", code);
        Assert.Contains("ProtectionSuggestionButtonText.Opacity = 1", code);
        Assert.Contains("NavigateToHistoryAnalysis(\"Learning\")", click);
        Assert.Contains("_dismissedSuggestionIds.Add(suggestion.SuggestionId)", markViewed);
        Assert.Contains("SaveBenefitLearning()", markViewed);
        Assert.DoesNotContain("StopProtectionSuggestionShimmer()", markViewed);
        Assert.Contains("ShowProtectionSuggestionsDialog(suggestions)", review);
        Assert.Contains("IsEnabled = suggested", code);
        Assert.Contains("ProtectEntireSuggestedFamily", code);
    }

    [Fact]
    public void SettingsShowsVersionSummaryAtTheBottomOfTheLeftColumn()
    {
        var layout = File.ReadAllText(FixturePath());
        var settingsStart = layout.IndexOf("<Grid x:Name=\"SettingsPage\"", StringComparison.Ordinal);
        Assert.True(settingsStart >= 0);

        var settings = layout[settingsStart..];
        Assert.Contains("<StackPanel Grid.Row=\"2\" Margin=\"14,18,14,8\">", settings);
        Assert.Equal(1, CountOccurrences(settings, "Text=\"{DynamicResource CurrentVersion}\""));
        Assert.Contains("x:Name=\"LatestVersionText\"", settings);
        Assert.Contains("Text=\"{DynamicResource Tagline}\" Style=\"{StaticResource CaptionStyle}\"", settings);
        Assert.Contains("(_availableUpdate?.Version ?? AppVersion.Current).ToString()", File.ReadAllText(CodeFixturePath()));
    }

    [Fact]
    public void ProtectedGroupRefreshKeepsMainExecutablePidsAndExpansionState()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var method = MethodBody(
            code,
            "private void RefreshProtectedList()",
            "private static bool ProtectedGroupsEqual");

        Assert.Contains("expandedExecutableKeys", method);
        Assert.Contains("executable.Processes", method);
        Assert.Contains("new ProtectedProcessEntry(", method);
        Assert.DoesNotContain(
            "!string.Equals(executable.ExecutablePath, applicationPath, StringComparison.OrdinalIgnoreCase)",
            method);
    }

    [Fact]
    public void ProtectedGroupReadOnlyRunBindingsAreOneWay()
    {
        var layout = File.ReadAllText(FixturePath());

        Assert.Equal(2, CountOccurrences(layout, "<Run Text=\"{Binding Name, Mode=OneWay}\""));
        Assert.Equal(2, CountOccurrences(layout, "<Run Text=\"{Binding InstanceCount, Mode=OneWay}\""));
        Assert.DoesNotContain("<Run Text=\"{Binding Name}\"", layout);
        Assert.DoesNotContain("<Run Text=\"{Binding InstanceCount}\"", layout);
    }

    [Fact]
    public void ActivityObservationKeepsUnprotectedPartsOfPartiallyProtectedApplications()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("observableFamilies", code);
        Assert.Contains("activityProtection.FilterUnprotectedProcesses", code);
        Assert.Contains("_activityTracker.Observe(", code);
        Assert.Contains("candidateIdleSettings.ActiveCpuThresholdPercent", code);
        Assert.Contains("candidateIdleSettings.ActiveIoThresholdBytesPerSecond", code);
        Assert.Contains("_strictActivityTracker.Observe(", code);
        Assert.Contains("_strictActivity,", code);
        Assert.Contains("UpdateLongTermRetryPermissions(\n                _families,\n                severeMemoryPressure,", code);
        Assert.Contains("var activityFamily = unprotectedFamily ?? family", code);
        Assert.Contains("var activity = isProtected", code);
        Assert.Contains("? \"--\"", code);
    }

    [Fact]
    public void ProfileCanBeChangedFromOverviewSettingsAndCompactMode()
    {
        var document = LoadDocument();
        var names = document.Descendants(Presentation + "ComboBox")
            .Select(element => (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")))
            .Where(name => name is not null)
            .ToArray();
        var text = document.ToString();

        Assert.Contains("OverviewProfileBox", names);
        Assert.Contains("ProfileBox", names);
        Assert.Contains("CompactProfileBox", names);
        Assert.Equal(3, CountOccurrences(text, "SelectionChanged=\"ProfileBox_OnSelectionChanged\""));
        Assert.Contains("{DynamicResource LiteBrush}", text);
        Assert.Contains("{DynamicResource TurboBrush}", text);
        Assert.Contains("{DynamicResource UltimateBrush}", text);
        Assert.Contains("x:Key=\"ProfileHelpTemplate\"", text);
    }

    [Fact]
    public void CompactModeUsesDenseCenteredLayout()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("x:Name=\"CompactShell\" Visibility=\"Collapsed\" Margin=\"10\" Padding=\"14\"", text);
        Assert.Contains("<ColumnDefinition Width=\"104\"/>", text);
        Assert.Contains("<ColumnDefinition Width=\"148\"/>", text);
        Assert.Contains("<RowDefinition Height=\"22\"/><RowDefinition Height=\"34\"/>", text);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\" TextAlignment=\"Center\"", text);
        Assert.Contains("x:Name=\"CompactProfileBox\"", text);
        Assert.Contains("Width=\"148\" Height=\"32\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"", text);
        Assert.Contains("x:Name=\"DropDownToggle\"", text);
        Assert.Contains("Height=\"Auto\"", text);
        Assert.Contains("VerticalAlignment=\"Stretch\"", text);
    }

    [Fact]
    public void CompactModeUsesFixedBoundsAndKeepsNativeMinimizeAvailable()
    {
        var text = File.ReadAllText(CodeFixturePath());

        Assert.Contains("ResizeMode = ResizeMode.CanMinimize", text);
        Assert.Contains("MaximizeRestoreButton.Visibility = Visibility.Collapsed", text);
        Assert.Contains("MinWidth = 540", text);
        Assert.Contains("MaxWidth = 540", text);
        Assert.Contains("MinHeight = 266", text);
        Assert.Contains("MaxHeight = 266", text);
        Assert.Contains("ResizeMode = ResizeMode.CanResize", text);
        Assert.Contains("MaximizeRestoreButton.Visibility = Visibility.Visible", text);
        Assert.Contains("WindowBoundsPolicy.CenterAndClamp(currentBounds, 540, 266, workingArea)", text);
        Assert.Contains("WindowBoundsPolicy.CenterAndClamp(currentBounds, 1240, 800, workingArea)", text);
        Assert.Contains("Forms.Screen.FromHandle(handle).WorkingArea", text);
    }

    [Fact]
    public void ModeSwitchButtonsExposeTargetModeAndDedicatedStates()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("x:Key=\"ModeToggleButtonStyle\"", layout);
        Assert.Contains("ToolTip=\"{DynamicResource SwitchToCompactMode}\"", layout);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource SwitchToCompactMode}\"", layout);
        Assert.Contains("ToolTip=\"{DynamicResource SwitchToFullConsole}\"", layout);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource SwitchToFullConsole}\"", layout);
        Assert.Contains("Data=\"{StaticResource IconShrink}\"", layout);
        Assert.Contains("Data=\"{StaticResource IconExpand}\"", layout);
        Assert.Contains("x:Key=\"IconShrink\"", File.ReadAllText(IconResourcesFixturePath()));
        Assert.Contains("x:Key=\"IconExpand\"", File.ReadAllText(IconResourcesFixturePath()));
        Assert.Contains("NavigationHoverBrush", layout);
        Assert.Contains("NavigationPressedBrush", layout);
        Assert.Contains("IsKeyboardFocused", layout);
        Assert.Contains("IsEnabled\" Value=\"False\"", layout);
        Assert.Contains("CustomProfileCatalogList.SelectedItem as ProfileCatalogItem", code);
        Assert.Contains("StableSuppressionCatalogList.SelectedItem as StableSuppressionCatalogItem", code);
    }

    [Fact]
    public void CustomProfileCopySupportsSavedCustomSourcesAndDraftChoice()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("AutomationProperties.Name=\"{DynamicResource CopyProfile}\"", layout);
        Assert.Contains("if (_customProfileDraftDirty)", code);
        Assert.Contains("TryBuildCustomProfileDraft(custom, out var draft)", code);
        Assert.Contains("AddCopy(settings, source, name)", code);
        Assert.Contains("CustomProfileCopyUnavailable", code);
        Assert.Contains("CustomProfileCopyBusy", code);
    }

    [Fact]
    public void CustomNavigationResetsCatalogsAndEditorScrollToLite()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var navigation = MethodBody(code, "private void Nav_OnClick", "private void ResetCustomPageEntryState");
        var reset = MethodBody(code, "private void ResetCustomPageEntryState", "private void SelectNavigation");

        Assert.Contains("if (page == CustomPage) ResetCustomPageEntryState();", navigation);
        Assert.Contains("RefreshCustomProfileCatalog(selectBuiltInProfile: OptimizationProfile.Lite)", reset);
        Assert.Contains("ShowCustomConfigurationSection(showStableSuppression: false)", reset);
        Assert.Contains("CustomEditorScroll.ScrollToTop()", reset);
        Assert.Contains("StableSuppressionEditorScroll.ScrollToTop()", reset);
    }

    [Fact]
    public void StableSuppressionEditorAlwaysLoadsAndEditableLabelsUseBodyColor()
    {
        var document = LoadDocument();
        var code = File.ReadAllText(CodeFixturePath());
        var loadSettings = MethodBody(code, "private void LoadSettingsIntoControls", "private async Task<bool> RefreshSnapshotAsync");
        var stableTab = MethodBody(code, "private void CustomStableSuppressionTab_OnClick", "private void ShowCustomConfigurationSection");
        var editable = MethodBody(code, "private void SetStableSuppressionEditorEditable", "private void CopyStableSuppressionProfile_OnClick");

        Assert.Contains("EnsureStableSuppressionEditorSelection();", loadSettings);
        Assert.Contains("EnsureStableSuppressionEditorSelection();", stableTab);
        var stablePanel = FindNamedElement(document, "CustomStableSuppressionPanel");
        string[] editableLabels =
        [
            "StableSuppressionProfileNameLabel",
            "StableMinimumSamplesLabel",
            "StableRecordAgeDaysLabel",
            "StableRelativeMarginLabel",
            "StableAbsoluteMarginLabel",
            "StableObservationMinutesLabel",
            "StableSampleIntervalMinutesLabel",
            "StableMaximumSamplesPerLaunchLabel",
            "StableSamplePoolLabel",
        ];
        Assert.All(editableLabels, name => FindNamedElement(stablePanel, name));
        Assert.Contains("editable ? \"TextBrush\" : \"MutedBrush\"", editable);
    }

    [Fact]
    public void WindowModeSwitchAppliesAcceptedBoundsWithoutRejectedAnimation()
    {
        var text = File.ReadAllText(CodeFixturePath());
        var method = MethodBody(
            text,
            "private void CompactMode_OnClick",
            "private WindowBounds CurrentWorkingArea");

        Assert.Contains("FullShell.Visibility = compact ? Visibility.Collapsed : Visibility.Visible", method);
        Assert.Contains("CompactShell.Visibility = compact ? Visibility.Visible : Visibility.Collapsed", method);
        Assert.DoesNotContain("_windowModeTransitioning", method);
        Assert.DoesNotContain("AnimateWindowModeTransitionAsync", method);
        Assert.DoesNotContain("Storyboard.SetTargetProperty", method);
    }

    [Fact]
    public void CurrentMemoryCardUsesARealPercentageBoundProgressBar()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("x:Key=\"MemoryBarStyle\"", text);
        Assert.Contains("Value=\"{Binding MemoryLoadPercent}\"", text);
        Assert.Contains("Text=\"{Binding PhysicalMemorySummary}\"", text);
        Assert.Contains("Text=\"{Binding AvailableMemory}\" FontSize=\"15\" Foreground=\"{DynamicResource TextBrush}\" FontWeight=\"SemiBold\"", text);
        Assert.Contains("{DynamicResource VirtualMemoryCommit}", text);
        Assert.Contains("Value=\"{Binding CommitLoadPercent}\"", text);
        Assert.Contains("Text=\"{Binding CommittedMemorySummary}\"", text);
        Assert.Contains("x:Name=\"PART_Track\"", text);
        Assert.Contains("x:Name=\"PART_Indicator\"", text);
    }

    [Fact]
    public void MemoryGaugeUsesOneDrawingElementInsteadOfRebuildingCanvasChildren()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("local:MemoryGauge", text);
        Assert.Contains("Value=\"{Binding MemoryLoadPercent}\"", text);
        Assert.DoesNotContain("x:Name=\"GaugeCanvas\"", text);
        Assert.DoesNotContain("GaugeCanvas_OnSizeChanged", text);
    }

    [Fact]
    public void SettingsExposeCloseBehaviorAndCandidateSafetyControls()
    {
        var text = File.ReadAllText(FixturePath());

        Assert.Contains("x:Name=\"CloseBehaviorBox\"", text);
        Assert.Contains("x:Name=\"EnhancedSafetyCheckBox\"", text);
        Assert.Contains("x:Name=\"IntelligentCandidateSelectionCheckBox\"", text);
        Assert.Contains("x:Name=\"DiagnosticDataCollectionCheckBox\"", text);
        Assert.Contains("x:Name=\"ClearDiagnosticDataButton\"", text);
        Assert.Contains("x:Name=\"OverviewBenefitLearningCheckBox\"", text);
        Assert.Contains("x:Name=\"ClearBenefitLearningButton\"", text);
        Assert.Contains("x:Key=\"SafetyToggleStyle\"", text);
        Assert.Contains("{DynamicResource EnhancedSafetyDescription}", text);
        Assert.Contains("{DynamicResource IntelligentCandidateSelectionDescription}", text);
        Assert.Contains("{DynamicResource ClearBenefitLearning}", text);
        Assert.Contains("Text=\".\\settings.json\"", text);
        Assert.Contains("Text=\".\\benefit-learning.json\"", text);
        Assert.Contains("Text=\".\\history.json\"", text);
        Assert.Contains("Text=\".\\rebound-history.json\"", text);
        Assert.Contains("Text=\".\\diagnostics\\calibration-metrics.jsonl\"", text);
        Assert.Contains("Text=\".\\diagnostics\\museram.log\"", text);
        Assert.Contains("x:Name=\"OpenDataFolderButton\"", text);
        Assert.DoesNotContain("StateChanged=\"MainWindow_OnStateChanged\"", text);
    }

    [Fact]
    public void DiagnosticCollectionGatesCalibrationWorkAndDefaultsToExplicitControl()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var refresh = MethodBody(
            code,
            "private async Task<bool> RefreshSnapshotAsync",
            "private bool CanRunUnattendedOptimization");
        var queue = MethodBody(
            code,
            "private void QueueCalibrationWrite",
            "private void FlushCalibrationWrites");
        var optimization = MethodBody(
            code,
            "private async Task RunOptimizationAsync",
            "private string FormatSelectedApplicationExclusion");

        Assert.Contains("if (_settings.DiagnosticDataCollectionEnabled)", refresh);
        Assert.Contains("_activityThresholdShadowTracker.Observe(", refresh);
        Assert.Contains("RecordProcessActivitySamples(activityObservedAt)", refresh);
        Assert.Contains("if (!_settings.DiagnosticDataCollectionEnabled) return;", queue);
        Assert.Contains("recordOptimizationRun = runSettings.DiagnosticDataCollectionEnabled", optimization);
        Assert.Contains("if (recordOptimizationRun)", optimization);
        Assert.Contains("OptimizationResourceSampler.Start()", optimization);
        Assert.Contains("applicationRule is not null,\n            runId", optimization.Replace("\r\n", "\n"));
        Assert.Contains("DiagnosticDataCollectionCheckBox.IsChecked = _settings.DiagnosticDataCollectionEnabled", code);
        Assert.Contains("if (_settings.DiagnosticDataCollectionEnabled) StartResponsivenessMonitoring()", code);
        var diagnosticToggle = MethodBody(
            code,
            "private void DiagnosticDataCollectionCheckBox_OnChanged",
            "private void ClearDiagnosticData_OnClick");
        Assert.Contains("_activityThresholdShadowTracker.Reset()", diagnosticToggle);
        var diagnosticClear = MethodBody(
            code,
            "private void ClearDiagnosticData_OnClick",
            "private void RuntimeProgressPersistenceCheckBox_OnChanged");
        Assert.Contains("_processIoCalibrationTracker.Reset()", diagnosticClear);
        Assert.Contains("_processCpuCalibrationTracker.Reset()", diagnosticClear);
        Assert.Contains("_activityThresholdShadowTracker.Reset()", diagnosticClear);
        Assert.Contains("_calibrationMetricsStore.Delete()", diagnosticClear);
        Assert.Contains("_diagnosticLog.Delete()", diagnosticClear);
    }

    [Fact]
    public void TrimExecutionReceivesBothForegroundPermissionAndEnhancedSafety()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("enhancedSafety: settings.EnhancedSafety", code);
        Assert.Contains("allowForegroundProcessTrim: settings.AllowForegroundProcessTrim", code);
    }

    [Fact]
    public void OptimizationUsesOneSettingsSnapshotAndGuardsProtectionChangesWithoutFreezingThePage()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var optimization = MethodBody(code, "private async Task RunOptimizationAsync", "private void ShowNoCandidatesDialog");

        Assert.Contains("!await RefreshSnapshotAsync(waitForCurrentRefresh: true)", optimization);
        Assert.Contains("snapshotAlreadyRefreshed", optimization);
        Assert.Contains("var runSettings = _settings;", optimization);
        Assert.Contains("runSettings.ResolveOptimizationSettings(manual)", optimization);
        Assert.Contains("runSettings.ResolveReboundSettings()", optimization);
        Assert.Contains("runSettings,\n                manual,\n                scheduled,\n                longIdle,\n                applicationRule is not null", optimization);
        Assert.Contains("PostTrimSamplingDelay(settings.EnhancedSafety)", optimization);
        Assert.DoesNotContain("_settings.ResolveReboundSettings()", optimization);
        Assert.DoesNotContain("_settings.IntelligentCandidateSelection", optimization);
        Assert.DoesNotContain("PostTrimSamplingDelay(_settings.EnhancedSafety)", optimization);
        Assert.Contains("if (_state.IsBusy) return;", code);
        Assert.Equal(10, CountOccurrences(code, "SetBusyState("));
    }

    [Fact]
    public void UpdateFlowUsesTheSharedBusyWindow()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var update = MethodBody(code, "private async Task CheckForUpdatesAsync", "private async Task HandleAvailableUpdateAsync");
        var install = MethodBody(code, "private async Task HandleAvailableUpdateAsync", "private UpdateDialogChoice ShowUpdateDialog");
        var dialog = MethodBody(code, "private UpdateDialogChoice ShowUpdateDialog", "private void ShowUpdateStatusDialog");

        var guard = update.IndexOf("if (_updateCheckInProgress || (manual && _state.IsBusy)) return;", StringComparison.Ordinal);
        var setBusy = update.IndexOf("SetBusyState(true);", StringComparison.Ordinal);
        var finallyBlock = update.IndexOf("finally", StringComparison.Ordinal);
        var clearBusy = update.IndexOf("SetBusyState(false);", StringComparison.Ordinal);
        Assert.True(guard >= 0);
        Assert.True(setBusy > guard);
        Assert.Contains("UpdateLauncher.LaunchReplacement(package);", install);
        Assert.True(finallyBlock > setBusy);
        Assert.True(clearBusy > finallyBlock);
        Assert.Contains("settings.SuppressedUpdateVersion = string.Empty", update);
        Assert.DoesNotContain("_startHidden || !IsVisible", update);
        Assert.Contains("RefreshOverviewAttention();", update[finallyBlock..]);
        Assert.Contains("asset.ReleaseNotes", dialog);
    }

    [Fact]
    public void OverviewAndSettingsBenefitLearningTogglesShareOneSetting()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("private void SetBenefitLearning(bool enabled)", code);
        Assert.Contains("IntelligentCandidateSelectionCheckBox.IsChecked = enabled", code);
        Assert.Contains("OverviewBenefitLearningCheckBox.IsChecked = enabled", code);
        Assert.Contains(
            "TryUpdateSettings(settings => settings.IntelligentCandidateSelection = enabled)",
            code);
    }

    [Fact]
    public void CustomProfileRiskOverridesAreLimitedToUltimateDerivedProfiles()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("{DynamicResource CustomProfileIgnoreMemoryPressureHelp}", layout);
        Assert.Contains("baseProfile == OptimizationProfile.Ultimate", code);
        Assert.Contains(
            "SetCustomProfileCheckBoxEditable(IgnorePressureCheckBox, editable && sourceAllowsRiskOverrides)",
            code);
        Assert.Contains(
            "SetCustomProfileCheckBoxEditable(AllowForegroundCheckBox, editable && sourceAllowsRiskOverrides)",
            code);
    }

    [Fact]
    public void UltimateDerivedCustomProfilesUseTheUltimateRiskPrompt()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var profileHandler = MethodBody(code, "private void ProfileBox_OnSelectionChanged", "private bool ShowUltimateRiskDialog");

        Assert.Contains("var selectedBaseProfile = choice.BuiltInProfile ?? _settings.CustomProfiles", profileHandler);
        Assert.Contains("?.BaseProfile;", profileHandler);
        Assert.Contains("selectedBaseProfile == OptimizationProfile.Ultimate", profileHandler);
        Assert.DoesNotContain("choice.BuiltInProfile == OptimizationProfile.Ultimate", profileHandler);
    }

    [Fact]
    public void ProtectionChangesRecheckBusyAfterDialogsBeforePersisting()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var addFile = MethodBody(code, "private void AddProtected_OnClick", "private async void AddRunningProtected_OnClick");
        var addRunning = MethodBody(code, "private async void AddRunningProtected_OnClick", "private IReadOnlyList<RunningProtectionSelection>? ShowRunningProtectionDialog");
        var removeGroup = MethodBody(code, "private void RemoveProtectedGroup_OnClick", "private bool ConfirmProtectionRemoval");
        var remove = MethodBody(code, "private void RemoveProtectionRules", "private async void DeepRelease_OnClick");

        Assert.True(addFile.LastIndexOf("if (_state.IsBusy) return;", StringComparison.Ordinal) >
                    addFile.IndexOf("dialog.ShowDialog(this)", StringComparison.Ordinal));
        Assert.True(addRunning.LastIndexOf("if (_state.IsBusy) return;", StringComparison.Ordinal) >
                    addRunning.IndexOf("ShowRunningProtectionDialog", StringComparison.Ordinal));
        Assert.True(removeGroup.LastIndexOf("if (_state.IsBusy) return;", StringComparison.Ordinal) >
                    removeGroup.IndexOf("ConfirmProtectionRemoval", StringComparison.Ordinal));
        Assert.Contains("if (_state.IsBusy) return;", remove);
    }

    [Fact]
    public void RunningProtectionManagementDistinguishesCancelFromConfirmedEmptyProtection()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var caller = MethodBody(code, "private async void AddRunningProtected_OnClick", "private IReadOnlyList<RunningProtectionSelection>? ShowRunningProtectionDialog");
        var dialog = MethodBody(code, "private IReadOnlyList<RunningProtectionSelection>? ShowRunningProtectionDialog", "private void RemoveProtectedGroup_OnClick");

        Assert.Contains("if (selections is null) return;", caller);
        Assert.DoesNotContain("if (selections.Count == 0) return;", caller);
        Assert.Contains("IReadOnlyList<RunningProtectionSelection>? selected = null;", dialog);
        Assert.Contains("selected = selectionReaders.Select(read => read()).ToArray();", dialog);
        Assert.Contains("dialog.DialogResult = true;", dialog);
    }

    [Fact]
    public void MainWindowUsesApplicationProtectionRulesWithoutWritingLegacyFieldsDirectly()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.Contains("private ProtectionRules CurrentProtectionRules() => _settings.CreateProtectionRules();", code);
        Assert.Contains("ApplicationProtectionSettings.ProtectSelectedExecutables", code);
        Assert.Contains("ApplicationProtectionSettings.ProtectEntireFamily", code);
        Assert.Contains("ApplicationProtectionSettings.Remove", code);
        Assert.Contains("ApplicationProtectionSettings.Replace", code);
        Assert.DoesNotContain("settings.ProtectedPaths", code);
        Assert.DoesNotContain("_settings.ProtectedPaths", code);
        Assert.DoesNotContain("_settings.ProtectRelatedProcesses", code);
    }

    [Fact]
    public void ProcessContextMenuCanProtectTheSelectedApplicationFamily()
    {
        var layout = File.ReadAllText(FixturePath());
        var code = File.ReadAllText(CodeFixturePath());
        var handler = MethodBody(
            code,
            "private void ProtectSelectedApplication_OnClick",
            "private async void OptimizeSelectedApplication_OnClick");

        Assert.Contains("Header=\"{DynamicResource AddApplicationToProtection}\"", layout);
        Assert.Contains("Click=\"ProtectSelectedApplication_OnClick\"", layout);
        Assert.Contains("ApplicationProtectionSettings.ProtectEntireFamily", handler);
        Assert.Contains("RefreshProtectedList();", handler);
        Assert.Contains("UpdateProcessRows();", handler);
        Assert.Contains("UpdatePreviewRows();", handler);
    }

    [Fact]
    public void SettingsSuccessSideEffectsRunOnlyAfterTransactionalPersistence()
    {
        var code = File.ReadAllText(CodeFixturePath());

        var profileHandler = MethodBody(code, "private void ProfileBox_OnSelectionChanged", "private bool ShowUltimateRiskDialog");
        var profileCommit = profileHandler.IndexOf("TryUpdateSettings", StringComparison.Ordinal);
        var previewUpdate = profileHandler.IndexOf("UpdatePreviewRows();", StringComparison.Ordinal);
        var profileHistory = profileHandler.IndexOf("AddHistory", StringComparison.Ordinal);
        Assert.True(profileCommit >= 0);
        Assert.True(previewUpdate > profileCommit);
        Assert.True(profileHistory > profileCommit);

        var ignoreHandler = MethodBody(code, "private void IgnoreMemoryPressureThresholdCheckBox_OnChanged", "private void ScheduledOptimizationCheckBox_OnChanged");
        var ignoreCommit = ignoreHandler.IndexOf("TryUpdateSettings", StringComparison.Ordinal);
        var scheduledUpdate = ignoreHandler.IndexOf("UpdateScheduledOptimizationAvailability", StringComparison.Ordinal);
        Assert.True(ignoreCommit >= 0);
        Assert.True(scheduledUpdate > ignoreCommit);
        Assert.Contains("ScheduledOptimizationCheckBox.IsEnabled = !unavailable", code);

        var customHandler = MethodBody(code, "private void SaveCustomProfile_OnClick", "private static bool TryReadNumber");
        var customCommit = customHandler.IndexOf("TryUpdateSettings", StringComparison.Ordinal);
        var customSuccess = customHandler.IndexOf("CustomProfileSaved", StringComparison.Ordinal);
        Assert.True(customCommit >= 0);
        Assert.True(customSuccess > customCommit);
        Assert.Contains("catch (OverflowException)", customHandler);
        Assert.DoesNotContain("private void SaveSettings()", code);
    }

    [Fact]
    public void IndirectActiveProfileChangesResetTheScheduledOptimizationAnchor()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var delete = MethodBody(code, "private void DeleteProfile_OnClick", "private void ShowBuiltInProfilesCheckBox_OnChanged");
        var visibility = MethodBody(code, "private void ShowBuiltInProfilesCheckBox_OnChanged", "private void AdvancedProfileModeCheckBox_OnChanged");
        var save = MethodBody(code, "private void SaveCustomProfile_OnClick", "private static bool TryReadNumber");

        Assert.Contains("resetAnchor: removedActiveProfile", delete);
        Assert.Contains("previousActiveCustomProfileId", visibility);
        Assert.Contains("UpdateScheduledOptimizationAvailability(resetAnchor: !string.Equals(", visibility);
        Assert.Contains("resetAnchor: updatedActiveProfile", save);
    }

    [Fact]
    public void DeletingCustomProfilesSelectsThePreviousCatalogEntry()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var deleteProfile = MethodBody(
            code,
            "private void DeleteProfile_OnClick",
            "private void ShowBuiltInProfilesCheckBox_OnChanged");
        var deleteStable = MethodBody(
            code,
            "private void DeleteStableSuppressionProfile_OnClick",
            "private void ShowBuiltInStableSuppressionProfilesCheckBox_OnChanged");

        Assert.Contains("previousProfileId", deleteProfile);
        Assert.Contains("OptimizationProfile.Ultimate", deleteProfile);
        Assert.Contains("previousProfileId", deleteStable);
        Assert.Contains("OptimizationProfile.Ultimate", deleteStable);
    }

    [Fact]
    public void StartupRegistrationIsSynchronizedOnlyAfterSettingsLoadSafely()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var constructor = MethodBody(code, "public MainWindow()", "private async void MainWindow_OnLoaded");
        var loadedHandler = MethodBody(code, "private async void MainWindow_OnLoaded", "private void LoadSettingsIntoControls");
        var startupHandler = MethodBody(code, "private void StartupCheckBox_OnChanged", "private void CloseBehaviorBox_OnSelectionChanged");

        Assert.Contains("_settingsLoadedSafely = settingsLoad.ErrorMessage is null;", constructor);
        var guard = loadedHandler.IndexOf("if (_settingsLoadedSafely)", StringComparison.Ordinal);
        var synchronization = loadedHandler.IndexOf("StartupRegistration.", StringComparison.Ordinal);
        Assert.True(guard >= 0);
        Assert.True(synchronization > guard);
        Assert.DoesNotContain("StartupRegistration.", loadedHandler[..guard]);
        Assert.DoesNotContain("settings.StartWithWindows = false", loadedHandler);
        Assert.Contains("StartupCheckBox.IsEnabled = _settingsLoadedSafely;", code);
        Assert.Contains("StartupPreferenceTransaction.TryCommit", startupHandler);
        Assert.Contains("StartupRegistration.SetEnabled", startupHandler);
        Assert.Contains("StartupRegistration.IsEnabled", startupHandler);
        Assert.Contains("StartupCheckBox.IsThreeState = result.CompensationError is not null", startupHandler);
    }

    [Fact]
    public void BackgroundStartupDoesNotShowTheMainWindow()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var app = File.ReadAllText(AppFixturePath());
        var constructor = MethodBody(code, "public MainWindow()", "private async void MainWindow_OnLoaded");
        var initialization = MethodBody(code, "private async Task InitializeCoreAsync", "private void LoadSettingsIntoControls");
        var hideToTray = MethodBody(code, "private void HideToTray()", "private void RestoreFromTray");
        var restore = MethodBody(code, "private void RestoreFromTray", "private void ExitApplication");

        Assert.Contains("ShowActivated = false", constructor);
        Assert.Contains("ShowInTaskbar = false", constructor);
        Assert.DoesNotContain("Opacity = 0", constructor);
        Assert.DoesNotContain("HideToTray(showNotice: false)", initialization);
        Assert.Contains("private void HideToTray()", code);
        Assert.DoesNotContain("ShowInTaskbar", hideToTray);
        Assert.Contains("Hide();", hideToTray);
        Assert.DoesNotContain("WindowState.Minimized", hideToTray);
        Assert.DoesNotContain("MinimizeAnimation", hideToTray);
        Assert.DoesNotContain("Task.Delay", hideToTray);
        Assert.Contains("if (mainWindow.StartsHidden)", app);
        Assert.Contains("await mainWindow.InitializeAsync();", app);
        Assert.Contains("mainWindow.Show();", app);
        Assert.True(
            app.IndexOf("await mainWindow.InitializeAsync();", StringComparison.Ordinal) <
            app.IndexOf("mainWindow.Show();", StringComparison.Ordinal));
        Assert.Contains("Opacity = 1", restore);
        Assert.Contains("ShowInTaskbar = true", restore);
    }

    [Fact]
    public void RestoringFromTrayDoesNotRewriteNativeWindowStyle()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var restore = MethodBody(
            code,
            "private void RestoreFromTray",
            "private void ExitApplication");

        var show = restore.IndexOf("Show();", StringComparison.Ordinal);
        var normal = restore.IndexOf("WindowState = WindowState.Normal", StringComparison.Ordinal);
        Assert.True(show >= 0);
        Assert.True(normal > show);
        Assert.DoesNotContain("WindowThemeService.EnableNativeWindowAnimations(this)", restore);
    }

    [Fact]
    public void RestoringFromTrayRevealsOnlyAfterTheFirstComposedFrame()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var hide = MethodBody(code, "private void HideToTray()", "private void RestoreFromTray");
        var restore = MethodBody(code, "private void RestoreFromTray", "internal void RestoreFromExternalActivation");
        var reveal = MethodBody(
            code,
            "private void CompositionTarget_OnRendering",
            "internal void RestoreFromExternalActivation");

        Assert.DoesNotContain("TrySetCloaked", hide);
        Assert.DoesNotContain("WindowState.Minimized", hide);
        Assert.DoesNotContain("Task.Delay", hide);

        var cloak = restore.IndexOf("WindowThemeService.TrySetCloaked(this, true)", StringComparison.Ordinal);
        var subscribe = restore.IndexOf("CompositionTarget.Rendering += CompositionTarget_OnRendering", StringComparison.Ordinal);
        var show = restore.IndexOf("Show();", StringComparison.Ordinal);
        Assert.True(cloak >= 0);
        Assert.True(subscribe > cloak);
        Assert.True(show > subscribe);
        Assert.DoesNotContain("Task.Delay", restore);

        var flush = reveal.IndexOf("WindowThemeService.FlushComposition();", StringComparison.Ordinal);
        var uncloak = reveal.IndexOf("WindowThemeService.TrySetCloaked(this, false)", StringComparison.Ordinal);
        Assert.True(flush >= 0);
        Assert.True(uncloak > flush);
        Assert.DoesNotContain("Task.Delay", reveal);
    }

    [Fact]
    public void EnergyStarXPromptIsRemoved()
    {
        var code = File.ReadAllText(CodeFixturePath());

        Assert.DoesNotContain("EnergyStarX", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            UiTextCatalog.For(UiLanguage.ChineseSimplified).Keys,
            key => key.Contains("EnergyStarX", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            UiTextCatalog.For(UiLanguage.English).Keys,
            key => key.Contains("EnergyStarX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LargeMemoryOpportunityPlanningIsLoggingOnly()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var run = MethodBody(code, "private async Task RunOptimizationAsync", "private void ShowNoCandidatesDialog");
        var shadow = MethodBody(
            code,
            "private void RecordLargeMemoryOpportunityIfDue",
            "private bool TryUpdateSettings");

        Assert.Contains("RecordLargeMemoryOpportunityIfDue(plan, settings, runContext, before, planNow)", run);
        Assert.Contains("IgnoreMemoryPressureThreshold = true", shadow);
        Assert.Contains("MaxApplications = 0", shadow);
        Assert.Contains("_planner.CreatePlan(", shadow);
        Assert.Contains("AppendLargeMemoryOpportunity", shadow);
        Assert.DoesNotContain("_trimmer", shadow);
        Assert.DoesNotContain("TrimAsync", shadow);
    }

    [Fact]
    public void WindowRoundsControlLayoutToDevicePixels()
    {
        var document = LoadDocument();
        var window = document.Root!;

        Assert.Equal("True", (string?)window.Attribute("UseLayoutRounding"));
        Assert.Equal("True", (string?)window.Attribute("SnapsToDevicePixels"));
    }

    [Fact]
    public void ProtectionGroupShowsTotalWorkingSetWithoutExpansion()
    {
        var layout = File.ReadAllText(FixturePath());
        var start = layout.IndexOf("x:Name=\"ProtectionPage\"", StringComparison.Ordinal);
        var end = layout.IndexOf("x:Name=\"HistoryPage\"", start, StringComparison.Ordinal);
        var protection = layout[start..end];

        Assert.Contains("Grid.Column=\"1\" Text=\"{Binding Memory}\"", protection);
        Assert.Contains("ItemsSource=\"{Binding Executables}\"", protection);
    }

    [Fact]
    public void ProtectionGroupMemoryColumnFitsTotalAndProtectedWorkingSet()
    {
        var document = LoadDocument();
        var memoryText = FindNamedElement(document, "ProtectedGroupMemoryText");
        var grid = memoryText.Ancestors(Presentation + "Grid").First();
        var columns = grid
            .Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements(Presentation + "ColumnDefinition")
            .ToArray();

        Assert.Equal("150", (string?)columns[1].Attribute("Width"));
        Assert.Equal("{Binding Memory}", (string?)memoryText.Attribute("Text"));
        Assert.Equal("{Binding Memory}", (string?)memoryText.Attribute("ToolTip"));
    }

    [Fact]
    public void CustomFamilyWorkingSetSliderUsesFourMiBStepsFromTwoMiB()
    {
        var document = LoadDocument();
        var slider = FindNamedElement(document, "MinFamilyMemorySlider");

        Assert.Equal("{StaticResource EditorSliderStyle}", (string?)slider.Attribute("Style"));
        Assert.Equal("2", (string?)slider.Attribute("Minimum"));
        Assert.Equal("1024", (string?)slider.Attribute("Maximum"));
        Assert.Equal("4", (string?)slider.Attribute("TickFrequency"));
    }

    [Fact]
    public void PointerHoverUsesSurfaceFeedbackInsteadOfAccentOutlines()
    {
        var document = LoadDocument();
        var buttonStyle = FindKeyedStyle(document, "ButtonStyle").ToString();
        var comboStyle = FindKeyedStyle(document, "ThemedComboBoxStyle").ToString();

        Assert.Contains("NavigationHoverBrush", buttonStyle);
        Assert.DoesNotContain("Property=\"BorderBrush\" Value=\"{DynamicResource AccentBrush}\"", buttonStyle);
        Assert.Contains("Property=\"Background\" Value=\"{DynamicResource NavigationHoverBrush}\"", comboStyle);
    }

    [Fact]
    public void ThemedComboBoxUsesOneInsetOutlineForAllFourEdges()
    {
        var comboStyle = FindKeyedStyle(LoadDocument(), "ThemedComboBoxStyle");
        var selectionBorder = comboStyle.Descendants(Presentation + "Border").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "SelectionBorder");

        Assert.Equal("0", (string?)selectionBorder.Attribute("BorderThickness"));
        Assert.DoesNotContain(comboStyle.Descendants(Presentation + "Border"), element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "SelectionBottomBorder");
        var outline = comboStyle.Descendants(Presentation + "Rectangle").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "SelectionOutline");
        Assert.Equal("0.5", (string?)outline.Attribute("Margin"));
        Assert.Equal("1", (string?)outline.Attribute("StrokeThickness"));
        Assert.Equal("4", (string?)outline.Attribute("RadiusX"));
        Assert.Equal("4", (string?)outline.Attribute("RadiusY"));
    }

    [Fact]
    public void CollapsedComboBoxesIgnoreWheelSelectionAndPreservePageScrolling()
    {
        var comboStyle = FindKeyedStyle(LoadDocument(), "ThemedComboBoxStyle");
        var eventSetter = comboStyle.Elements(Presentation + "EventSetter").Single();
        var code = File.ReadAllText(CodeFixturePath());
        var handler = MethodBody(
            code,
            "private void ComboBox_OnPreviewMouseWheel",
            "private static bool IsDescendantOf");

        Assert.Equal("PreviewMouseWheel", (string?)eventSetter.Attribute("Event"));
        Assert.Equal("ComboBox_OnPreviewMouseWheel", (string?)eventSetter.Attribute("Handler"));
        Assert.Contains("comboBox.IsDropDownOpen", handler);
        Assert.Contains("e.Handled = true", handler);
        Assert.Contains("parent.RaiseEvent", handler);
        Assert.Contains("Mouse.MouseWheelEvent", handler);
    }

    [Fact]
    public void PointerAncestorTraversalSupportsInlineTextContent()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var descendantMethod = MethodBody(
            code,
            "private static bool IsDescendantOf",
            "private static Button? FindAncestorButton");
        var buttonMethod = MethodBody(
            code,
            "private static Button? FindAncestorButton",
            "private static DependencyObject? ParentOf");
        var parentMethod = MethodBody(
            code,
            "private static DependencyObject? ParentOf",
            "private Popup? ResolveManagedPopup");

        Assert.Contains("ParentOf(current)", descendantMethod);
        Assert.Contains("ParentOf(current)", buttonMethod);
        Assert.Contains("FrameworkContentElement content => content.Parent", parentMethod);
        Assert.Contains("ContentElement content => ContentOperations.GetParent(content)", parentMethod);
        Assert.Contains("Visual or Visual3D => VisualTreeHelper.GetParent(current)", parentMethod);

        var popupTriggerMethod = MethodBody(
            code,
            "private bool TryClosePopupFromTrigger",
            "private bool ConsumeSuppressedPopupTriggerClick");
        Assert.Contains("FindAncestorButton(source)", popupTriggerMethod);
        Assert.DoesNotContain("VisualTreeHelper.GetParent", popupTriggerMethod);
    }

    [Fact]
    public void ApplicationUsesPerMonitorV2DpiMode()
    {
        var project = XDocument.Load(ProjectFixturePath());
        var dpiMode = project.Descendants("ApplicationHighDpiMode").Single();

        Assert.Equal("PerMonitorV2", dpiMode.Value);
    }

    [Fact]
    public void ApplicationVersionMatchesCurrentRelease()
    {
        var project = XDocument.Load(ProjectFixturePath());

        Assert.Equal("0.1.7.5", project.Descendants("Version").Single().Value);
    }

    [Fact]
    public void MainWindowUsesMuseRamApplicationIcon()
    {
        var document = LoadDocument();

        Assert.Equal("Assets/MuseRAM.ico", (string?)document.Root!.Attribute("Icon"));
    }

    [Fact]
    public void DisabledStableSuppressionDoesNotObserveOrBlockNaturalStableState()
    {
        var code = File.ReadAllText(CodeFixturePath());
        var refresh = MethodBody(
            code,
            "private async Task<bool> RefreshSnapshotAsync",
            "private bool CanRunUnattendedOptimization");
        var filters = MethodBody(
            code,
            "IReadOnlySet<string> StableComponents) CurrentLearningFilters",
            "private void SynchronizeApplicationRuleStates");

        Assert.Contains("stableObservationEnabled", refresh);
        Assert.Contains("naturalStableSettings is not null", refresh);
        Assert.Contains("Array.Empty<NaturalStableStateSnapshot>()", refresh);
        Assert.Contains("if (stableSettings is not null)", filters);
        Assert.Contains("NaturalStableRecoveryEligibleComponentKeys", filters);
        Assert.Contains("NaturalStableProvisionalValidationComponentKeys", filters);
    }

    private static XDocument LoadDocument() => XDocument.Load(FixturePath());

    private static XElement FindKeyedStyle(XDocument document, string key) =>
        document.Descendants(Presentation + "Style").Single(element =>
            (string?)element.Attribute(Xaml + "Key") == key);

    private static XElement FindNamedElement(XContainer container, string name) =>
        container.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == name);

    private static void AssertStyleSetter(XElement style, string property, string value) =>
        Assert.Contains(style.Elements(Presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == property &&
            (string?)setter.Attribute("Value") == value);

    private static void AssertLearningTextAlignment(XElement panel, string text, string? style = null)
    {
        var element = panel.Descendants(Presentation + "TextBlock").Single(candidate =>
            (string?)candidate.Attribute("Text") == text);
        Assert.Equal("Stretch", (string?)element.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)element.Attribute("TextAlignment"));
        if (style is not null) Assert.Equal(style, (string?)element.Attribute("Style"));
    }

    private static int CountOccurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static string MethodBody(string code, string startMarker, string endMarker)
    {
        var start = code.IndexOf(startMarker, StringComparison.Ordinal);
        var end = code.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        return code[start..end];
    }

    private static string FixturePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml");

    private static string IconResourcesFixturePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "IconResources.xaml");

    private static string CodeFixturePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml.cs");

    private static string AppFixturePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml.cs");

    private static string StartupDialogFixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "StartupThemedDialog.cs");

    private static string ProjectFixturePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "MuseRAM.App.csproj");

}
