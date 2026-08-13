using MuseRAM.App;
using MuseRAM.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MuseRAM.App.Tests;

public sealed class LocalSettingsContractTests
{
    [Fact]
    public void DefaultStorageRootIsTheProgramDirectory()
    {
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), AppDataPaths.RootDirectory);
        Assert.Equal(Path.Combine(AppDataPaths.RootDirectory, "settings.json"), AppDataPaths.SettingsFile);
        Assert.Equal(Path.Combine(AppDataPaths.RootDirectory, "benefit-learning.json"), AppDataPaths.BenefitLearningFile);
        Assert.Equal(Path.Combine(AppDataPaths.RootDirectory, "diagnostics"), AppDataPaths.DiagnosticsDirectory);
        Assert.Equal(
            Path.Combine(AppDataPaths.DiagnosticsDirectory, "calibration-metrics.jsonl"),
            AppDataPaths.CalibrationMetricsFile);
        Assert.Equal(
            Path.Combine(AppDataPaths.DiagnosticsDirectory, "museram.log"),
            AppDataPaths.DiagnosticLogFile);
    }

    [Fact]
    public void LegacyAuxiliaryFilesMoveIntoTheDiagnosticsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"museram-paths-{Guid.NewGuid():N}");
        var legacyLogs = Path.Combine(root, "logs");
        var diagnostics = Path.Combine(root, "diagnostics");
        Directory.CreateDirectory(legacyLogs);
        File.WriteAllText(Path.Combine(root, "calibration-metrics.jsonl"), "current");
        File.WriteAllText(Path.Combine(root, "calibration-metrics.jsonl.previous"), "previous");
        File.WriteAllText(Path.Combine(legacyLogs, "museram.log"), "log");
        File.WriteAllText(Path.Combine(legacyLogs, "museram.log.previous"), "old-log");

        try
        {
            AppDataPaths.MigrateLegacyAuxiliaryFiles(root);

            Assert.Equal("current", File.ReadAllText(Path.Combine(diagnostics, "calibration-metrics.jsonl")));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(diagnostics, "calibration-metrics.jsonl.previous")));
            Assert.Equal("log", File.ReadAllText(Path.Combine(diagnostics, "museram.log")));
            Assert.Equal("old-log", File.ReadAllText(Path.Combine(diagnostics, "museram.log.previous")));
            Assert.False(File.Exists(Path.Combine(root, "calibration-metrics.jsonl")));
            Assert.False(Directory.Exists(legacyLogs));
        }
        finally
        {
            foreach (var name in new[]
                     {
                         "calibration-metrics.jsonl",
                         "calibration-metrics.jsonl.previous",
                         "museram.log",
                         "museram.log.previous"
                     })
            {
                var path = Path.Combine(diagnostics, name);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(diagnostics)) Directory.Delete(diagnostics);
            if (Directory.Exists(legacyLogs)) Directory.Delete(legacyLogs);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Fact]
    public void MissingFileReturnsMuseDefaults()
    {
        var path = TestPath();

        var settings = new LocalSettingsStore(path).Load();

        Assert.Equal(OptimizationProfile.Turbo, settings.Profile);
        Assert.False(settings.AutoOptimization);
        Assert.False(settings.ScheduledOptimizationEnabled);
        Assert.Equal(60, settings.ScheduledOptimizationIntervalMinutes);
        Assert.False(settings.GlobalReclaimIntervalEnabled);
        Assert.Equal(60, settings.GlobalReclaimIntervalMinutes);
        Assert.False(settings.GlobalReclaimStartupDelayEnabled);
        Assert.Equal(5, settings.GlobalReclaimStartupDelayMinutes);
        Assert.False(settings.LongIdleOptimizationEnabled);
        Assert.Equal(60, settings.LongIdleOptimizationMinutes);
        Assert.False(settings.StartWithWindows);
        Assert.True(settings.LightTheme);
        Assert.False(settings.FollowSystemTheme);
        Assert.False(settings.EnhancedSafety);
        Assert.False(settings.IgnoreMemoryPressureThreshold);
        Assert.False(settings.IntelligentCandidateSelection);
        Assert.Equal(
            StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
            settings.CustomStableStateSuppression);
        Assert.False(settings.DiagnosticDataCollectionEnabled);
        Assert.False(settings.RuntimeProgressPersistenceEnabled);
        Assert.False(settings.QuickCandidateSelection);
        Assert.Equal(CandidateDisplayLimitPolicy.Default, settings.CandidateDisplayLimit);
        Assert.True(settings.ProtectRelatedProcesses);
        Assert.True(settings.ShowBuiltInProfiles);
        Assert.Null(settings.ActiveCustomProfileId);
        Assert.Empty(settings.CustomProfiles);
        Assert.True(settings.ShowBuiltInStableStateSuppressionProfiles);
        Assert.Null(settings.ActiveCustomStableStateSuppressionProfileId);
        Assert.Empty(settings.CustomStableStateSuppressionProfiles);
        Assert.Empty(settings.StableAnchorSettings);
        Assert.False(settings.UltimateRiskPromptSuppressed);
        Assert.False(settings.SelectedApplicationOptimizationPromptSuppressed);
        Assert.Equal(CloseButtonBehavior.Ask, settings.CloseButtonBehavior);
        Assert.Equal("zh-CN", settings.LanguageCode);
        Assert.Null(settings.ApplicationProtectionRules);
        Assert.Empty(settings.ProtectedPaths);
        Assert.Empty(ApplicationProtectionSettings.Resolve(settings));
    }

    [Fact]
    public void StableAnchorSettingsRoundTripAndSchemaFourMigratesToAnEmptyCollection()
    {
        var path = TestPath();
        try
        {
            var anchor = new ApplicationStableAnchorSetting(
                "suite",
                "suite|scope:main",
                StableAnchorMode.Fixed,
                404L * 1024 * 1024);
            var store = new LocalSettingsStore(path);
            store.Save(new LocalSettings { StableAnchorSettings = new List<ApplicationStableAnchorSetting> { anchor } });

            var loaded = store.LoadWithStatus();

            Assert.Null(loaded.ErrorMessage);
            Assert.False(loaded.Migrated);
            Assert.Equal(anchor, Assert.Single(loaded.Settings.StableAnchorSettings));

            File.WriteAllText(path, "{\"SettingsVersion\":4}");
            var migrated = new LocalSettingsStore(path).LoadWithStatus();

            Assert.True(migrated.Migrated);
            Assert.Empty(migrated.Settings.StableAnchorSettings);
            Assert.False(File.Exists(path + ".bak"));
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Theory]
    [InlineData(0, OptimizationProfile.Lite)]
    [InlineData(1, OptimizationProfile.Turbo)]
    [InlineData(2, OptimizationProfile.Ultimate)]
    public void ExistingNumericProfileValuesRemainCompatible(
        int storedValue,
        OptimizationProfile expected)
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(
                path,
                $$"""{"SettingsVersion":{{SettingsSchema.CurrentVersion}},"Profile":{{storedValue}}}""");

            var result = new LocalSettingsStore(path).LoadWithStatus();

            Assert.Null(result.ErrorMessage);
            Assert.False(result.Migrated);
            Assert.Equal(expected, result.Settings.Profile);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void DeepCloneCopiesNestedSettingsWithoutSharingMutableState()
    {
        var profile = CustomProfilePolicy.Create(OptimizationProfile.Turbo, "Work", 0);
        profile.Settings = profile.Settings with { EnhancedSafety = true };
        var settings = new LocalSettings
        {
            ActiveCustomProfileId = profile.Id,
            DiagnosticDataCollectionEnabled = true,
            RuntimeProgressPersistenceEnabled = true,
            QuickCandidateSelection = true,
            CandidateDisplayLimit = 40,
            StableStateSuppressionMode = StableStateSuppressionMode.FasterReevaluation,
            CustomStableStateSuppression = new StableStateSuppressionSettings(
                9,
                TimeSpan.FromDays(11),
                0.28,
                80L * 1024 * 1024),
            ActiveCustomStableStateSuppressionProfileId = "steady-1",
            ShowBuiltInStableStateSuppressionProfiles = false,
            CustomStableStateSuppressionProfiles = new List<CustomStableStateSuppressionProfile>
            {
                new()
                {
                    Id = "steady-1",
                    Name = "Quiet memory",
                    Settings = new StableStateSuppressionSettings(
                        6,
                        TimeSpan.FromDays(14),
                        0.22,
                        72L * 1024 * 1024)
                }
            },
            CustomProfiles = new List<CustomOptimizationProfile> { profile },
            ApplicationProtectionRules = new List<ApplicationProtectionRule>
            {
                new()
                {
                    ApplicationExecutablePath = @"F:\Apps\Media\media.exe",
                    ProtectedExecutablePaths = new List<string> { @"F:\Apps\Media\capture.exe" }
                }
            },
            ApplicationOptimizationRules = new List<ApplicationOptimizationRule>
            {
                new()
                {
                    Targets = new()
                    {
                        new()
                        {
                            TargetType = ApplicationOptimizationTargetType.ExecutableGroup,
                            Path = @"F:\Apps\Media\media.exe",
                            ExecutablePaths = new() { @"F:\Apps\Media\media.exe", @"F:\Apps\Media\capture.exe" }
                        }
                    }
                }
            },
            ProtectedPaths = new List<string> { @"F:\Apps\Editor\editor.exe" }
        };

        var clone = settings.DeepClone();

        Assert.NotSame(settings, clone);
        Assert.NotSame(settings.CustomProfiles, clone.CustomProfiles);
        Assert.NotSame(profile, clone.CustomProfiles[0]);
        Assert.NotSame(profile.Settings, clone.CustomProfiles[0].Settings);
        Assert.NotSame(profile.Rebound, clone.CustomProfiles[0].Rebound);
        Assert.NotSame(profile.StableStateSuppression, clone.CustomProfiles[0].StableStateSuppression);
        Assert.NotSame(settings.CustomStableStateSuppression, clone.CustomStableStateSuppression);
        Assert.NotSame(settings.CustomStableStateSuppressionProfiles, clone.CustomStableStateSuppressionProfiles);
        Assert.NotSame(
            settings.CustomStableStateSuppressionProfiles[0],
            clone.CustomStableStateSuppressionProfiles[0]);
        Assert.NotSame(
            settings.CustomStableStateSuppressionProfiles[0].Settings,
            clone.CustomStableStateSuppressionProfiles[0].Settings);
        Assert.NotSame(settings.ApplicationProtectionRules, clone.ApplicationProtectionRules);
        Assert.NotSame(settings.ApplicationOptimizationRules, clone.ApplicationOptimizationRules);
        Assert.NotSame(
            settings.ApplicationOptimizationRules![0].Targets[0].ExecutablePaths,
            clone.ApplicationOptimizationRules![0].Targets[0].ExecutablePaths);
        Assert.NotSame(
            settings.ApplicationProtectionRules![0].ProtectedExecutablePaths,
            clone.ApplicationProtectionRules![0].ProtectedExecutablePaths);
        Assert.NotSame(settings.ProtectedPaths, clone.ProtectedPaths);
        Assert.True(clone.CustomProfiles[0].Settings.EnhancedSafety);
        Assert.True(clone.DiagnosticDataCollectionEnabled);
        Assert.True(clone.RuntimeProgressPersistenceEnabled);
        Assert.True(clone.QuickCandidateSelection);
        Assert.Equal(40, clone.CandidateDisplayLimit);
        Assert.Equal(StableStateSuppressionMode.FasterReevaluation, clone.StableStateSuppressionMode);
        Assert.Equal(9, clone.CustomStableStateSuppression.MinimumSamples);
        Assert.False(clone.ShowBuiltInStableStateSuppressionProfiles);
        Assert.Equal("steady-1", clone.ActiveCustomStableStateSuppressionProfileId);

        clone.CustomProfiles[0].Name = "Changed";
        clone.CustomProfiles[0].Settings = clone.CustomProfiles[0].Settings with { MaxApplications = 19 };
        clone.CustomProfiles[0].Rebound = clone.CustomProfiles[0].Rebound with { EarlyReboundPercent = 91 };
        clone.CustomProfiles[0].StableStateSuppression = clone.CustomProfiles[0].StableStateSuppression with { MinimumSamples = 8 };
        clone.CustomStableStateSuppression = clone.CustomStableStateSuppression with { MinimumSamples = 2 };
        clone.CustomStableStateSuppressionProfiles[0].Settings =
            clone.CustomStableStateSuppressionProfiles[0].Settings with { MinimumSamples = 2 };
        clone.ApplicationProtectionRules![0].ProtectedExecutablePaths[0] = @"F:\Apps\Media\other.exe";
        clone.ApplicationOptimizationRules![0].Targets[0].ExecutablePaths[0] = @"F:\Apps\Media\other.exe";
        clone.ProtectedPaths[0] = @"F:\Apps\Other\other.exe";

        Assert.Equal("Work", profile.Name);
        Assert.NotEqual(19, profile.Settings.MaxApplications);
        Assert.NotEqual(91, profile.Rebound.EarlyReboundPercent);
        Assert.NotEqual(8, profile.StableStateSuppression.MinimumSamples);
        Assert.Equal(9, settings.CustomStableStateSuppression.MinimumSamples);
        Assert.Equal(6, settings.CustomStableStateSuppressionProfiles[0].Settings.MinimumSamples);
        Assert.Equal(
            @"F:\Apps\Media\capture.exe",
            settings.ApplicationProtectionRules![0].ProtectedExecutablePaths[0]);
        Assert.Equal(
            @"F:\Apps\Media\media.exe",
            settings.ApplicationOptimizationRules![0].Targets[0].ExecutablePaths[0]);
        Assert.Equal(@"F:\Apps\Editor\editor.exe", settings.ProtectedPaths[0]);
    }

    [Fact]
    public void SchemaTwoCustomSuppressionMigratesToANamedProfile()
    {
        var path = TestPath();
        try
        {
            var legacy = new StableStateSuppressionSettings(
                7,
                TimeSpan.FromDays(21),
                0.44,
                88L * 1024 * 1024);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                SettingsVersion = 2,
                LanguageCode = "zh-CN",
                StableStateSuppressionMode = StableStateSuppressionMode.Custom,
                CustomStableStateSuppression = legacy
            }));

            var result = new LocalSettingsStore(path).LoadWithStatus();

            Assert.True(result.Migrated);
            var profile = Assert.Single(result.Settings.CustomStableStateSuppressionProfiles);
            Assert.Equal("自定义稳态", profile.Name);
            Assert.Equal(legacy, profile.Settings);
            Assert.Equal(profile.Id, result.Settings.ActiveCustomStableStateSuppressionProfileId);
            Assert.Equal(profile.Settings, result.Settings.ResolveStableStateSuppressionSettings());
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void RemovingTheOnlyStableSuppressionProfileRestoresBuiltInVisibilityAndFollowMode()
    {
        var profile = CustomStableStateSuppressionProfilePolicy.Create(
            OptimizationProfile.Turbo,
            "Quiet memory",
            0);
        var settings = new LocalSettings
        {
            StableStateSuppressionMode = StableStateSuppressionMode.Custom,
            ActiveCustomStableStateSuppressionProfileId = profile.Id,
            ShowBuiltInStableStateSuppressionProfiles = false,
            CustomStableStateSuppressionProfiles = new List<CustomStableStateSuppressionProfile> { profile }
        };

        Assert.True(CustomStableStateSuppressionProfileSettingsOperations.Remove(settings, profile.Id));
        Assert.Empty(settings.CustomStableStateSuppressionProfiles);
        Assert.True(settings.ShowBuiltInStableStateSuppressionProfiles);
        Assert.Null(settings.ActiveCustomStableStateSuppressionProfileId);
        Assert.Equal(StableStateSuppressionMode.FollowBaseProfile, settings.StableStateSuppressionMode);
    }

    [Theory]
    [InlineData(StableStateSuppressionMode.FollowBaseProfile, StableStateSuppressionMode.FollowBaseProfile)]
    [InlineData(StableStateSuppressionMode.Disabled, StableStateSuppressionMode.Disabled)]
    [InlineData(StableStateSuppressionMode.ReduceRepeatedOptimization, StableStateSuppressionMode.Custom)]
    [InlineData(StableStateSuppressionMode.Balanced, StableStateSuppressionMode.Custom)]
    [InlineData(StableStateSuppressionMode.FasterReevaluation, StableStateSuppressionMode.Custom)]
    public void HidingBuiltInStableProfilesOnlyReplacesExplicitBuiltInModes(
        StableStateSuppressionMode configuredMode,
        StableStateSuppressionMode expectedMode)
    {
        var path = TestPath();
        try
        {
            var custom = CustomStableStateSuppressionProfilePolicy.Create(
                OptimizationProfile.Turbo,
                "Custom steady state",
                0);
            new LocalSettingsStore(path).Save(new LocalSettings
            {
                StableStateSuppressionMode = configuredMode,
                ShowBuiltInStableStateSuppressionProfiles = false,
                CustomStableStateSuppressionProfiles = new List<CustomStableStateSuppressionProfile> { custom }
            });

            var loaded = new LocalSettingsStore(path).Load();

            Assert.Equal(expectedMode, loaded.StableStateSuppressionMode);
            if (expectedMode == StableStateSuppressionMode.Custom)
                Assert.Equal(custom.Id, loaded.ActiveCustomStableStateSuppressionProfileId);
            else
                Assert.Null(loaded.ActiveCustomStableStateSuppressionProfileId);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void TransactionPersistsAndReturnsTheMutatedCandidateOnSuccess()
    {
        var current = new LocalSettings
        {
            Profile = OptimizationProfile.Lite,
            ProtectedPaths = new List<string> { @"F:\Apps\Editor\editor.exe" }
        };
        LocalSettings? persisted = null;

        var result = LocalSettingsTransaction.TryCommit(
            current,
            candidate =>
            {
                candidate.Profile = OptimizationProfile.Ultimate;
                candidate.ProtectedPaths.Add(@"F:\Apps\Browser\browser.exe");
            },
            candidate => persisted = candidate);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.NotSame(current, result.Settings);
        Assert.Same(result.Settings, persisted);
        Assert.Equal(OptimizationProfile.Ultimate, result.Settings.Profile);
        Assert.Equal(2, result.Settings.ProtectedPaths.Count);
        Assert.Equal(OptimizationProfile.Lite, current.Profile);
        Assert.Single(current.ProtectedPaths);
    }

    [Fact]
    public void TransactionReturnsTheOriginalObjectWhenPersistenceFails()
    {
        var profile = CustomProfilePolicy.Create(OptimizationProfile.Turbo, "Work", 0);
        var current = new LocalSettings
        {
            ActiveCustomProfileId = profile.Id,
            CustomProfiles = new List<CustomOptimizationProfile> { profile }
        };
        var failure = new IOException("read only");
        LocalSettings? attempted = null;

        var result = LocalSettingsTransaction.TryCommit(
            current,
            candidate => candidate.CustomProfiles[0].Name = "Changed",
            candidate =>
            {
                attempted = candidate;
                throw failure;
            });

        Assert.False(result.Succeeded);
        Assert.Same(failure, result.Error);
        Assert.Same(current, result.Settings);
        Assert.NotSame(current, attempted);
        Assert.Equal("Work", current.CustomProfiles[0].Name);
    }

    [Fact]
    public void TransactionWithWriteBlockedStoreKeepsTheOriginalFileAndObject()
    {
        var path = TestPath();
        try
        {
            const string malformed = "{ not-json";
            File.WriteAllText(path, malformed);
            var store = new LocalSettingsStore(path);
            var current = store.LoadWithStatus().Settings;

            var result = LocalSettingsTransaction.TryCommit(
                current,
                candidate => candidate.AutoOptimization = true,
                store.Save);

            Assert.False(result.Succeeded);
            Assert.IsType<InvalidOperationException>(result.Error);
            Assert.Same(current, result.Settings);
            Assert.False(current.AutoOptimization);
            Assert.Equal(malformed, File.ReadAllText(path));
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void StartupPreferenceTransactionRestoresThePreviousStateWhenDisableVerificationFails()
    {
        var enabled = true;
        var reads = 0;
        var persisted = false;

        var result = StartupPreferenceTransaction.TryCommit(
            previous: true,
            requested: false,
            value => enabled = value,
            () => ++reads == 1 ? true : enabled,
            _ => persisted = true);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RegistrationError);
        Assert.Null(result.PersistenceError);
        Assert.Null(result.CompensationError);
        Assert.True(enabled);
        Assert.False(persisted);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void StartupPreferenceTransactionCompensatesWhenSettingsPersistenceFails()
    {
        var enabled = false;
        var persistenceFailure = new IOException("read only");

        var result = StartupPreferenceTransaction.TryCommit(
            previous: false,
            requested: true,
            value => enabled = value,
            () => enabled,
            _ => throw persistenceFailure);

        Assert.False(result.Succeeded);
        Assert.Null(result.RegistrationError);
        Assert.Same(persistenceFailure, result.PersistenceError);
        Assert.Null(result.CompensationError);
        Assert.False(enabled);
    }

    [Fact]
    public void StartupPreferenceTransactionReportsCompensationFailure()
    {
        var enabled = false;
        var applyCount = 0;
        var persistenceFailure = new IOException("read only");

        var result = StartupPreferenceTransaction.TryCommit(
            previous: false,
            requested: true,
            value =>
            {
                applyCount++;
                if (applyCount > 1) throw new InvalidOperationException("restore failed");
                enabled = value;
            },
            () => enabled,
            _ => throw persistenceFailure);

        Assert.False(result.Succeeded);
        Assert.Same(persistenceFailure, result.PersistenceError);
        Assert.Equal("restore failed", result.CompensationError?.Message);
        Assert.True(enabled);
    }

    [Fact]
    public void StartupPreferenceTransactionVerifiesBeforePersistingTheRequestedValue()
    {
        var enabled = false;
        var events = new List<string>();
        bool? persisted = null;

        var result = StartupPreferenceTransaction.TryCommit(
            previous: false,
            requested: true,
            value =>
            {
                events.Add($"apply:{value}");
                enabled = value;
            },
            () =>
            {
                events.Add("read");
                return enabled;
            },
            value =>
            {
                events.Add($"persist:{value}");
                persisted = value;
            });

        Assert.True(result.Succeeded);
        Assert.Null(result.RegistrationError);
        Assert.Null(result.PersistenceError);
        Assert.Null(result.CompensationError);
        Assert.True(persisted);
        Assert.Equal(new[] { "apply:True", "read", "persist:True" }, events);
    }

    [Fact]
    public void MalformedFileReturnsMuseDefaults()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(path, "{ not-json");

            var original = File.ReadAllText(path);
            var store = new LocalSettingsStore(path);
            var result = store.LoadWithStatus();
            var settings = result.Settings;

            Assert.Equal(OptimizationProfile.Turbo, settings.Profile);
            Assert.False(settings.AutoOptimization);
            Assert.False(settings.StartWithWindows);
            Assert.True(settings.LightTheme);
            Assert.Equal("zh-CN", settings.LanguageCode);
            Assert.NotNull(result.ErrorMessage);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".bak"));
            Assert.Throws<InvalidOperationException>(() => store.Save(settings));
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void UnversionedSettingsAreBackedUpAndMigratedWithoutLosingChoices()
    {
        var path = TestPath();
        try
        {
            const string legacy = """{"Profile":0,"AutoOptimization":true,"LanguageCode":"en","ProtectedPaths":["F:\\Apps\\Editor\\editor.exe"]}""";
            File.WriteAllText(path, legacy);

            var store = new LocalSettingsStore(path);
            var result = store.LoadWithStatus();

            Assert.True(result.Migrated);
            Assert.Null(result.ErrorMessage);
            Assert.False(File.Exists(path + ".bak"));
            Assert.Equal(SettingsSchema.CurrentVersion, result.Settings.SettingsVersion);
            Assert.Equal(OptimizationProfile.Lite, result.Settings.Profile);
            Assert.True(result.Settings.AutoOptimization);
            Assert.Equal("en", result.Settings.LanguageCode);
            Assert.Equal(@"F:\Apps\Editor\editor.exe", Assert.Single(result.Settings.ProtectedPaths));
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(SettingsSchema.CurrentVersion, document.RootElement.GetProperty("SettingsVersion").GetInt32());
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void SchemaFiveStableProfilesMigrateValidationWindowAndPressureDefaults()
    {
        var path = TestPath();
        try
        {
            const string legacy = """
            {
              "SettingsVersion": 5,
              "CustomStableStateSuppressionProfiles": [
                {
                  "Id": "lite-custom",
                  "Name": "Lite custom",
                  "BaseProfile": 0,
                  "SortOrder": 0,
                  "Settings": { "NaturalStableObservationWindow": "00:07:00" }
                },
                {
                  "Id": "ultimate-custom",
                  "Name": "Ultimate custom",
                  "BaseProfile": 2,
                  "SortOrder": 1,
                  "Settings": { "NaturalStableObservationWindow": "00:04:00" }
                }
              ]
            }
            """;
            File.WriteAllText(path, legacy);

            var result = new LocalSettingsStore(path).LoadWithStatus();

            Assert.True(result.Migrated);
            Assert.Null(result.ErrorMessage);
            Assert.Equal(SettingsSchema.CurrentVersion, result.Settings.SettingsVersion);
            var lite = Assert.Single(result.Settings.CustomStableStateSuppressionProfiles,
                profile => profile.Id == "lite-custom");
            Assert.Equal(TimeSpan.FromMinutes(7), lite.Settings.MaximumStableValidationDuration);
            Assert.False(lite.Settings.IgnoreRegularObservationUnderSeverePressure);
            var ultimate = Assert.Single(result.Settings.CustomStableStateSuppressionProfiles,
                profile => profile.Id == "ultimate-custom");
            Assert.Equal(TimeSpan.FromMinutes(4), ultimate.Settings.MaximumStableValidationDuration);
            Assert.True(ultimate.Settings.IgnoreRegularObservationUnderSeverePressure);

            var migratedJson = File.ReadAllText(path);
            Assert.Contains("MaximumStableValidationDuration", migratedJson);
            Assert.Contains("IgnoreRegularObservationUnderSeverePressure", migratedJson);
            Assert.DoesNotContain("NaturalStableObservationWindow", migratedJson);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void FutureSettingsVersionIsNotOverwritten()
    {
        var path = TestPath();
        try
        {
            var future = $$"""{"SettingsVersion":{{SettingsSchema.CurrentVersion + 1}},"AutoOptimization":true}""";
            File.WriteAllText(path, future);

            var store = new LocalSettingsStore(path);
            var result = store.LoadWithStatus();

            Assert.NotNull(result.ErrorMessage);
            Assert.False(result.Settings.AutoOptimization);
            Assert.Equal(future, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".bak"));
            Assert.Throws<InvalidOperationException>(() => store.Save(result.Settings));
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void LegacySettingsWithoutRelatedProtectionFieldDefaultToEnabled()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(path, "{}");

            var settings = new LocalSettingsStore(path).Load();

            Assert.True(settings.ProtectRelatedProcesses);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void DisabledRelatedProtectionAndSuppressedPromptsArePersisted()
    {
        var path = TestPath();
        try
        {
            var store = new LocalSettingsStore(path);
            store.Save(new LocalSettings
            {
                ProtectRelatedProcesses = false,
                UltimateRiskPromptSuppressed = true,
                SelectedApplicationOptimizationPromptSuppressed = true
            });

            var settings = store.Load();

            Assert.False(settings.ProtectRelatedProcesses);
            Assert.True(settings.UltimateRiskPromptSuppressed);
            Assert.True(settings.SelectedApplicationOptimizationPromptSuppressed);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("ja")]
    [InlineData("ko")]
    public void RemovedLanguageCodesAreMigratedToSimplifiedChinese(string legacyCode)
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(path, $$"""{"LanguageCode":"{{legacyCode}}"}""");

            var settings = new LocalSettingsStore(path).Load();

            Assert.Equal("zh-CN", settings.LanguageCode);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void CreatingACustomProfileDoesNotChangeTheActiveProfile()
    {
        var existing = CustomProfilePolicy.Create(OptimizationProfile.Turbo, "Existing", 0);
        var settings = new LocalSettings
        {
            Profile = OptimizationProfile.Lite,
            ActiveCustomProfileId = existing.Id,
            CustomProfiles = new List<CustomOptimizationProfile> { existing }
        };

        var created = CustomProfileSettingsOperations.AddCopy(settings, OptimizationProfile.Ultimate, "New profile");

        Assert.Equal(existing.Id, settings.ActiveCustomProfileId);
        Assert.Equal(OptimizationProfile.Lite, settings.Profile);
        Assert.Equal(created, settings.CustomProfiles[1]);
    }

    [Fact]
    public void CopyingCustomProfilesUsesSavedValuesAndDoesNotShareMutableObjects()
    {
        var source = CustomProfilePolicy.Create(OptimizationProfile.Turbo, "Source", 0);
        source.Settings = source.Settings with { MaxApplications = 13 };
        var stableSource = CustomStableStateSuppressionProfilePolicy.Create(
            OptimizationProfile.Ultimate,
            "Stable source",
            0);
        stableSource.Settings = stableSource.Settings with { MinimumSamples = 6 };
        var settings = new LocalSettings
        {
            CustomProfiles = new List<CustomOptimizationProfile> { source },
            CustomStableStateSuppressionProfiles =
                new List<CustomStableStateSuppressionProfile> { stableSource }
        };

        var copy = CustomProfileSettingsOperations.AddCopy(settings, source, "Source copy");
        var stableCopy = CustomStableStateSuppressionProfileSettingsOperations.AddCopy(
            settings,
            stableSource,
            "Stable source copy");

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(source.Settings, copy.Settings);
        Assert.NotEqual(stableSource.Id, stableCopy.Id);
        Assert.Equal(stableSource.Settings, stableCopy.Settings);

        copy.Settings = copy.Settings with { MaxApplications = 14 };
        stableCopy.Settings = stableCopy.Settings with { MinimumSamples = 7 };

        Assert.Equal(13, source.Settings.MaxApplications);
        Assert.Equal(6, stableSource.Settings.MinimumSamples);
    }

    [Fact]
    public void RemovingTheActiveCustomProfileKeepsARemainingCustomProfileActiveWhenBuiltInsAreHidden()
    {
        var first = CustomProfilePolicy.Create(OptimizationProfile.Lite, "First", 0);
        var active = CustomProfilePolicy.Create(OptimizationProfile.Turbo, "Active", 1);
        var last = CustomProfilePolicy.Create(OptimizationProfile.Ultimate, "Last", 2);
        var settings = new LocalSettings
        {
            ActiveCustomProfileId = active.Id,
            ShowBuiltInProfiles = false,
            CustomProfiles = new List<CustomOptimizationProfile> { first, active, last }
        };

        var removed = CustomProfileSettingsOperations.Remove(settings, active.Id);

        Assert.True(removed);
        Assert.False(settings.ShowBuiltInProfiles);
        Assert.Equal(first.Id, settings.ActiveCustomProfileId);
        Assert.Equal(new[] { first.Id, last.Id }, settings.CustomProfiles.Select(profile => profile.Id));
        Assert.Equal(new[] { 0, 1 }, settings.CustomProfiles.Select(profile => profile.SortOrder));
    }

    [Fact]
    public void SavingOneChangedFieldPreservesOtherChoices()
    {
        var path = TestPath();
        try
        {
            var store = new LocalSettingsStore(path);
            store.Save(new LocalSettings
            {
                Profile = OptimizationProfile.Ultimate,
                AutoOptimization = true,
                ScheduledOptimizationEnabled = true,
                ScheduledOptimizationIntervalMinutes = 720,
                LongIdleOptimizationEnabled = true,
                LongIdleOptimizationMinutes = 180,
                StartWithWindows = true,
                LightTheme = false,
                FollowSystemTheme = true,
                EnhancedSafety = true,
                IgnoreMemoryPressureThreshold = true,
                IntelligentCandidateSelection = true,
                DiagnosticDataCollectionEnabled = true,
                RuntimeProgressPersistenceEnabled = true,
                QuickCandidateSelection = true,
                CandidateDisplayLimit = CandidateDisplayLimitPolicy.Unlimited,
                CloseButtonBehavior = CloseButtonBehavior.MinimizeToTray,
                LanguageCode = "en",
                ProtectedPaths = new List<string> { @"F:\Apps\Editor\editor.exe" }
            });

            var settings = store.Load();
            settings.LightTheme = true;
            store.Save(settings);
            var reloaded = store.Load();

            Assert.Equal(OptimizationProfile.Ultimate, reloaded.Profile);
            Assert.True(reloaded.AutoOptimization);
            Assert.True(reloaded.ScheduledOptimizationEnabled);
            Assert.Equal(720, reloaded.ScheduledOptimizationIntervalMinutes);
            Assert.True(reloaded.LongIdleOptimizationEnabled);
            Assert.Equal(180, reloaded.LongIdleOptimizationMinutes);
            Assert.True(reloaded.StartWithWindows);
            Assert.True(reloaded.LightTheme);
            Assert.True(reloaded.FollowSystemTheme);
            Assert.True(reloaded.EnhancedSafety);
            Assert.True(reloaded.IgnoreMemoryPressureThreshold);
            Assert.True(reloaded.IntelligentCandidateSelection);
            Assert.True(reloaded.DiagnosticDataCollectionEnabled);
            Assert.True(reloaded.RuntimeProgressPersistenceEnabled);
            Assert.True(reloaded.QuickCandidateSelection);
            Assert.Equal(CandidateDisplayLimitPolicy.Unlimited, reloaded.CandidateDisplayLimit);
            Assert.Equal(CloseButtonBehavior.MinimizeToTray, reloaded.CloseButtonBehavior);
            Assert.Equal("en", reloaded.LanguageCode);
            Assert.Equal(@"F:\Apps\Editor\editor.exe", Assert.Single(reloaded.ProtectedPaths));
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void QuickCandidateSelectionOnlyAppliesToUnattendedSettingsSnapshots()
    {
        var settings = new LocalSettings
        {
            Profile = OptimizationProfile.Turbo,
            QuickCandidateSelection = true
        };

        Assert.True(settings.ResolveOptimizationSettings(manual: false).QuickCandidateSelection);
        Assert.False(settings.ResolveOptimizationSettings(manual: true).QuickCandidateSelection);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 10)]
    [InlineData(20, 20)]
    [InlineData(40, 40)]
    [InlineData(13, 20)]
    [InlineData(-1, 20)]
    public void CandidateDisplayLimitAcceptsOnlySupportedChoices(int value, int expected)
    {
        var path = TestPath();
        try
        {
            var store = new LocalSettingsStore(path);
            store.Save(new LocalSettings { CandidateDisplayLimit = value });

            Assert.Equal(expected, store.Load().CandidateDisplayLimit);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Theory]
    [InlineData(OptimizationProfile.Lite)]
    [InlineData(OptimizationProfile.Turbo)]
    public void GlobalPressureOverrideAppliesToEveryNonUltimateBuiltInProfile(
        OptimizationProfile profile)
    {
        var settings = new LocalSettings
        {
            Profile = profile,
            IgnoreMemoryPressureThreshold = true
        };

        Assert.False(settings.ActiveProfileIgnoresMemoryPressureThreshold);
        var resolved = settings.ResolveOptimizationSettings(manual: false);
        Assert.True(resolved.IgnoreMemoryPressureThreshold);
        Assert.True(ScheduledOptimizationPolicy.IsUnavailable(
            autoOptimizationEnabled: true,
            ignoresMemoryPressure: resolved.IgnoreMemoryPressureThreshold));
    }

    [Theory]
    [InlineData(OptimizationProfile.Lite)]
    [InlineData(OptimizationProfile.Turbo)]
    public void GlobalPressureOverrideAlsoAppliesToCustomNonUltimateProfiles(
        OptimizationProfile baseProfile)
    {
        var custom = CustomProfilePolicy.Create(baseProfile, "Custom", 0);
        var settings = new LocalSettings
        {
            ActiveCustomProfileId = custom.Id,
            CustomProfiles = new List<CustomOptimizationProfile> { custom },
            IgnoreMemoryPressureThreshold = true
        };

        Assert.False(settings.ActiveProfileIgnoresMemoryPressureThreshold);
        Assert.True(settings.ResolveOptimizationSettings(manual: false).IgnoreMemoryPressureThreshold);
    }

    [Fact]
    public void UltimateDerivedProfileReportsItsOwnPressureOverrideSeparatelyFromGlobalState()
    {
        var custom = CustomProfilePolicy.Create(OptimizationProfile.Ultimate, "Maximum", 0);
        var settings = new LocalSettings
        {
            Profile = OptimizationProfile.Lite,
            ActiveCustomProfileId = custom.Id,
            CustomProfiles = new List<CustomOptimizationProfile> { custom },
            IgnoreMemoryPressureThreshold = false
        };

        Assert.True(settings.ActiveProfileIgnoresMemoryPressureThreshold);
        Assert.True(settings.ResolveOptimizationSettings(manual: false).IgnoreMemoryPressureThreshold);
    }

    [Fact]
    public void InvalidProfileFallsBackToGaming()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(path, """{"Profile":999}""");

            Assert.Equal(OptimizationProfile.Turbo, new LocalSettingsStore(path).Load().Profile);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void InvalidCustomProfileSourceDoesNotInvalidateTheWholeSettingsFile()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(
                path,
                """{"AutoOptimization":true,"CustomProfiles":[null,{"Id":"recovered","Name":"Recovered","BaseProfile":999,"SortOrder":0}]}""");

            var result = new LocalSettingsStore(path).LoadWithStatus();

            Assert.Null(result.ErrorMessage);
            Assert.True(result.Settings.AutoOptimization);
            var custom = Assert.Single(result.Settings.CustomProfiles);
            Assert.Equal("Recovered", custom.Name);
            Assert.Equal(OptimizationProfile.Turbo, custom.BaseProfile);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void InvalidCloseButtonBehaviorFallsBackToAsk()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(path, """{"CloseButtonBehavior":999}""");

            Assert.Equal(CloseButtonBehavior.Ask, new LocalSettingsStore(path).Load().CloseButtonBehavior);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void InvalidStableStateSuppressionModeFallsBackToFollowCurrentProfile()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(path, """{"StableStateSuppressionMode":999}""");

            Assert.Equal(
                StableStateSuppressionMode.FollowBaseProfile,
                new LocalSettingsStore(path).Load().StableStateSuppressionMode);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void ProtectedPathsAreTrimmedOfEmptyAndDuplicateEntries()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(path, """{"ProtectedPaths":["F:\\Apps\\Editor\\editor.exe","","f:\\apps\\editor\\EDITOR.exe",null]}""");

            var pathEntry = Assert.Single(new LocalSettingsStore(path).Load().ProtectedPaths);

            Assert.Equal(@"F:\Apps\Editor\editor.exe", pathEntry);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyProtectionSettingsResolveWithoutChangingTheirMeaning(bool protectEntireFamily)
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(
                path,
                $$"""{"SettingsVersion":{{SettingsSchema.CurrentVersion}},"ProtectRelatedProcesses":{{protectEntireFamily.ToString().ToLowerInvariant()}},"ProtectedPaths":["F:\\Apps\\Editor\\editor.exe"]}""");

            var settings = new LocalSettingsStore(path).Load();
            var rule = Assert.Single(ApplicationProtectionSettings.Resolve(settings));

            Assert.Null(settings.ApplicationProtectionRules);
            Assert.Equal(@"F:\Apps\Editor\editor.exe", rule.ApplicationExecutablePath);
            Assert.Equal(protectEntireFamily, rule.ProtectEntireFamily);
            if (protectEntireFamily)
                Assert.Empty(rule.ProtectedExecutablePaths);
            else
                Assert.Equal(@"F:\Apps\Editor\editor.exe", Assert.Single(rule.ProtectedExecutablePaths));
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void MixedProtectionRulesRoundTripWithConservativeLegacyMirror()
    {
        var path = TestPath();
        try
        {
            var settings = new LocalSettings();
            ApplicationProtectionSettings.ProtectEntireFamily(
                settings,
                @"F:\Apps\Editor\editor.exe");
            ApplicationProtectionSettings.ProtectSelectedExecutables(
                settings,
                @"F:\Apps\Media\media.exe",
                new[]
                {
                    @"F:\Apps\Media\.\capture.exe",
                    @"f:\apps\media\CAPTURE.exe"
                });

            var store = new LocalSettingsStore(path);
            store.Save(settings);
            var loaded = store.Load();

            var rules = Assert.IsAssignableFrom<IReadOnlyList<ApplicationProtectionRule>>(
                loaded.ApplicationProtectionRules);
            Assert.Equal(2, rules.Count);
            Assert.True(Assert.Single(rules.Where(rule =>
                rule.ApplicationExecutablePath.EndsWith("editor.exe", StringComparison.OrdinalIgnoreCase)))
                .ProtectEntireFamily);
            var partial = Assert.Single(rules.Where(rule =>
                rule.ApplicationExecutablePath.EndsWith("media.exe", StringComparison.OrdinalIgnoreCase)));
            Assert.False(partial.ProtectEntireFamily);
            Assert.Equal(@"F:\Apps\Media\capture.exe", Assert.Single(partial.ProtectedExecutablePaths));

            Assert.True(loaded.ProtectRelatedProcesses);
            Assert.Equal(
                new[] { @"F:\Apps\Editor\editor.exe", @"F:\Apps\Media\capture.exe" },
                loaded.ProtectedPaths);
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                SettingsSchema.CurrentVersion,
                document.RootElement.GetProperty("SettingsVersion").GetInt32());
            Assert.Equal(
                2,
                document.RootElement.GetProperty("ApplicationProtectionRules").GetArrayLength());
            var legacy = System.Text.Json.JsonSerializer.Deserialize<LegacyProtectionSettings>(
                File.ReadAllText(path));
            Assert.NotNull(legacy);
            Assert.Equal(SettingsSchema.CurrentVersion, legacy.SettingsVersion);
            Assert.True(legacy.ProtectRelatedProcesses);
            Assert.Equal(2, legacy.ProtectedPaths.Count);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void PartialOnlyRulesUseExactLegacyRollbackSemantics()
    {
        var settings = new LocalSettings();

        ApplicationProtectionSettings.ProtectSelectedExecutables(
            settings,
            @"F:\Apps\Media\media.exe",
            new[] { @"F:\Apps\Media\capture.exe" });

        Assert.False(settings.ProtectRelatedProcesses);
        Assert.Equal(@"F:\Apps\Media\capture.exe", Assert.Single(settings.ProtectedPaths));
    }

    [Fact]
    public void ExplicitEmptyNewRulesOverrideStaleLegacyFields()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(
                path,
                $$"""{"SettingsVersion":{{SettingsSchema.CurrentVersion}},"ApplicationProtectionRules":[],"ProtectRelatedProcesses":true,"ProtectedPaths":["F:\\Apps\\Old\\old.exe"]}""");

            var settings = new LocalSettingsStore(path).Load();

            Assert.NotNull(settings.ApplicationProtectionRules);
            Assert.Empty(settings.ApplicationProtectionRules);
            Assert.Empty(settings.ProtectedPaths);
            Assert.Empty(ApplicationProtectionSettings.Resolve(settings));
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void CustomProfilesAreNormalizedLimitedAndKeepAValidActiveSelection()
    {
        var path = TestPath();
        try
        {
            var profiles = Enumerable.Range(0, 10)
                .Select(index => CustomProfilePolicy.Create(
                    OptimizationProfile.Turbo,
                    $"Custom {index}",
                    index))
                .ToList();
            var store = new LocalSettingsStore(path);
            store.Save(new LocalSettings
            {
                CustomProfiles = profiles,
                ActiveCustomProfileId = profiles[2].Id,
                ShowBuiltInProfiles = false
            });

            var loaded = store.Load();

            Assert.Equal(CustomProfilePolicy.MaximumCustomProfiles, loaded.CustomProfiles.Count);
            Assert.Equal(profiles[2].Id, loaded.ActiveCustomProfileId);
            Assert.False(loaded.ShowBuiltInProfiles);
            Assert.Equal(Enumerable.Range(0, 8), loaded.CustomProfiles.Select(profile => profile.SortOrder));
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void HidingBuiltInsFallsBackToFirstCustomProfile()
    {
        var path = TestPath();
        try
        {
            var custom = CustomProfilePolicy.Create(OptimizationProfile.Lite, "Quiet", 0);
            custom.StableStateSuppression = new StableStateSuppressionSettings(
                6,
                TimeSpan.FromDays(14),
                0.44,
                72L * 1024 * 1024);
            new LocalSettingsStore(path).Save(new LocalSettings
            {
                CustomProfiles = new List<CustomOptimizationProfile> { custom },
                ActiveCustomProfileId = "missing",
                ShowBuiltInProfiles = false
            });

            var loaded = new LocalSettingsStore(path).Load();

            Assert.Equal(custom.Id, loaded.ActiveCustomProfileId);
            Assert.Equal(custom.Settings, loaded.ResolveOptimizationSettings(manual: false));
            Assert.Equal(
                StableStateSuppressionSettings.For(OptimizationProfile.Lite),
                loaded.ResolveStableStateSuppressionSettings());
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void VersionOneFixedStablePresetMigratesToIndependentCustomSettings()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(path, """
                {
                  "SettingsVersion": 1,
                  "StableStateSuppressionMode": 2
                }
                """);

            var result = new LocalSettingsStore(path).LoadWithStatus();

            Assert.True(result.Migrated);
            Assert.Equal(StableStateSuppressionMode.Custom, result.Settings.StableStateSuppressionMode);
            Assert.Equal(
                StableStateSuppressionSettings.For(OptimizationProfile.Turbo),
                result.Settings.CustomStableStateSuppression);
            Assert.False(File.Exists(path + ".bak"));
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void VersionOneActiveCustomProfileMigratesItsStableParameters()
    {
        var path = TestPath();
        try
        {
            var profile = CustomProfilePolicy.Create(OptimizationProfile.Lite, "Quiet", 0);
            profile.StableStateSuppression = new StableStateSuppressionSettings(
                7,
                TimeSpan.FromDays(12),
                0.42,
                77L * 1024 * 1024);
            var root = JsonSerializer.SerializeToNode(new LocalSettings
            {
                SettingsVersion = 1,
                ActiveCustomProfileId = profile.Id,
                CustomProfiles = new List<CustomOptimizationProfile> { profile },
                StableStateSuppressionMode = StableStateSuppressionMode.FollowBaseProfile
            })!.AsObject();
            root.Remove(nameof(LocalSettings.CustomStableStateSuppression));
            File.WriteAllText(path, root.ToJsonString());

            var result = new LocalSettingsStore(path).LoadWithStatus();

            Assert.True(result.Migrated);
            Assert.Equal(StableStateSuppressionMode.Custom, result.Settings.StableStateSuppressionMode);
            Assert.Equal(profile.StableStateSuppression, result.Settings.CustomStableStateSuppression);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    [Fact]
    public void LegacyBalancedProfileFallsBackToGamingWithoutADeadProfile()
    {
        var path = TestPath();
        try
        {
            File.WriteAllText(path, """{"ProfileCode":"Balanced","LanguageCode":"ko"}""");

            var settings = new LocalSettingsStore(path).Load();

            Assert.Equal(OptimizationProfile.Turbo, settings.Profile);
            Assert.Equal("zh-CN", settings.LanguageCode);
        }
        finally
        {
            DeleteSettingsFiles(path);
        }
    }

    private static string TestPath() =>
        Path.Combine(AppContext.BaseDirectory, $"{Guid.NewGuid():N}-settings.json");

    private static void DeleteSettingsFiles(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
    }

    private sealed class LegacyProtectionSettings
    {
        public int SettingsVersion { get; set; }
        public bool ProtectRelatedProcesses { get; set; }
        public List<string> ProtectedPaths { get; set; } = new();
    }
}
