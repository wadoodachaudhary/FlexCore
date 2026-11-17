using System.Globalization;
using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Telemetry;

/// <summary>
/// Generalized, zero-round-trip Product Analytics computation engine over <see cref="FlexTelemetryEvent"/>
/// and <see cref="FlexSqlTelemetryEntry"/> streams.
/// Computes 2x2 Feature Adoption Matrices, Stickiness (DAU/WAU/MAU), Period-over-Period Deltas,
/// Conversion Funnels, Multi-Stage User Journeys, Ping-Pong Workflow Loops, Cohort Retention Matrices,
/// UX Friction Intelligence (Rage Clicks, Form Abandonment, Filter Thrash, Click-to-SQL Apdex),
/// Session Flight-Recorder Timelines, and OLAP Pivot rows.
/// </summary>
public static class FlexProductAnalyticsEngine
{
    private static readonly string[] DefaultHierarchyColors =
    {
        "#0a246a", "#2563eb", "#0284c7", "#0d9488", "#16a34a", "#7c3aed", "#d97706", "#dc2626"
    };

    private static readonly string[] CommitKeywords =
    {
        "save", "submit", "ok", "apply", "commit", "issue", "post", "create", "approve", "confirm", "finish", "export", "print"
    };

    public static FlexProductAnalyticsSnapshot BuildProductSnapshot(
        IReadOnlyList<FlexTelemetryEvent> rawEvents,
        IReadOnlyList<FlexSqlTelemetryEntry> sqlEntries,
        ITelemetryCatalogProvider? catalog,
        ITelemetryWorkflowProvider? workflowProvider,
        FlexTelemetryQueryFilter filter,
        string defaultTenant = "")
    {
        var nowUtc = DateTime.UtcNow;
        int daysBack = Math.Clamp(filter.DaysBack <= 0 ? 30 : filter.DaysBack, 1, 365);
        int? minutesBack = filter.MinutesBack is > 0 ? Math.Clamp(filter.MinutesBack.Value, 1, 525_600) : null;
        var span = minutesBack.HasValue ? TimeSpan.FromMinutes(minutesBack.Value) : TimeSpan.FromDays(daysBack);
        var cutoffUtc = nowUtc - span;
        var priorCutoffUtc = cutoffUtc - span;

        string timeWindowLabel = minutesBack.HasValue
            ? (minutesBack.Value < 60
                ? $"Last {minutesBack.Value}m"
                : (minutesBack.Value % 1440 == 0 ? $"Last {minutesBack.Value / 1440}d" : $"Last {minutesBack.Value / 60}h"))
            : $"Last {daysBack}d";

        var targetTenant = (filter.Tenant ?? string.Empty).Trim();
        bool allTenants = string.IsNullOrEmpty(targetTenant) || string.Equals(targetTenant, "*", StringComparison.Ordinal);
        var userFilter = (filter.UserId ?? string.Empty).Trim();
        bool hasUserFilter = !string.IsNullOrEmpty(userFilter) && !string.Equals(userFilter, "All", StringComparison.OrdinalIgnoreCase);
        var moduleFilter = (filter.ModuleFilter ?? string.Empty).Trim();
        bool hasModuleFilter = !string.IsNullOrEmpty(moduleFilter) && !string.Equals(moduleFilter, "All", StringComparison.OrdinalIgnoreCase);
        var search = (filter.SearchText ?? string.Empty).Trim();

        var currentEvents = new List<FlexTelemetryEvent>();
        var priorEvents = new List<FlexTelemetryEvent>();
        var thirtyDayEvents = new List<FlexTelemetryEvent>();
        var mauCutoffUtc = nowUtc.AddDays(-30);

        for (int i = 0; i < rawEvents.Count; i++)
        {
            var e = rawEvents[i];
            if (!FlexTelemetryAggregator.MatchesTenant(e.Tenant, targetTenant, allTenants, defaultTenant))
                continue;
            if (hasUserFilter && !string.Equals(e.UserId, userFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var normRoute = NormalizeRoute(e.Route);
            var (resolvedTitle, resolvedModule) = ResolveScreen(catalog, normRoute, e.Screen);
            if (hasModuleFilter && !string.Equals(resolvedModule, moduleFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(search) && !MatchesSearch(e, resolvedTitle, resolvedModule, search))
                continue;

            if (e.TimestampUtc >= mauCutoffUtc)
                thirtyDayEvents.Add(e);

            if (e.TimestampUtc >= cutoffUtc)
                currentEvents.Add(e);
            else if (e.TimestampUtc >= priorCutoffUtc)
                priorEvents.Add(e);
        }

        currentEvents.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        priorEvents.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));

        var filteredSql = sqlEntries
            .Where(q => !q.IsBackgroundPoll && q.TimestampUtc >= cutoffUtc)
            .Where(q => FlexTelemetryAggregator.MatchesTenant(q.Tenant, targetTenant, allTenants, defaultTenant))
            .OrderBy(q => q.TimestampUtc)
            .ToList();

        var stickiness = ComputeStickiness(currentEvents, priorEvents, thirtyDayEvents, nowUtc, daysBack);
        var (adoptionRows, medShare, medFreq, sunburstRoot, treemapPts) = ComputeFeatureAdoption(
            currentEvents, priorEvents, catalog, hasModuleFilter ? moduleFilter : null);

        var presets = BuildWorkflowPresets(workflowProvider, currentEvents, catalog);
        var defaultPreset = presets[0];
        var defaultFunnel = EvaluateFunnel(currentEvents, defaultPreset.Steps, bySession: true, defaultPreset.Name, catalog);

        var journeys = BuildUserJourneys(currentEvents, catalog, anchorScreen: null, reverseMode: false, maxStages: 4, topBranchesPerStage: 6);
        var weeklyCohorts = BuildCohortReport(currentEvents.Count >= 4 ? currentEvents : thirtyDayEvents, catalog, weekly: true, nowUtc);
        var dailyCohorts = BuildCohortReport(currentEvents, catalog, weekly: false, nowUtc);

        var (frictionIncidents, apdexRows, overallApdex) = DetectFrictionAndApdex(currentEvents, filteredSql, catalog);
        var sessions = BuildSessionSummaries(currentEvents, filteredSql, frictionIncidents, catalog);
        var pivotRows = BuildPivotRows(currentEvents, catalog);

        return new FlexProductAnalyticsSnapshot
        {
            GeneratedAtLocal = DateTime.Now,
            TimeWindowLabel = timeWindowLabel,
            Stickiness = stickiness,
            MedianUserSharePercent = medShare,
            MedianActionsPerUser = medFreq,
            AdoptionRows = adoptionRows,
            HierarchySunburstRoot = sunburstRoot,
            ModuleScreenTreemapPoints = treemapPts,
            AvailableWorkflowPresets = presets,
            DefaultFunnel = defaultFunnel,
            Journeys = journeys,
            WeeklyCohorts = weeklyCohorts,
            DailyCohorts = dailyCohorts,
            OverallApdexScore = overallApdex,
            RageClickCount = frictionIncidents.Count(f => f.Kind == UxFrictionKind.RageClick),
            AbandonedFormCount = frictionIncidents.Count(f => f.Kind == UxFrictionKind.AbandonedForm),
            FilterThrashCount = frictionIncidents.Count(f => f.Kind == UxFrictionKind.FilterThrash),
            SlowInteractionCount = frictionIncidents.Count(f => f.Kind is UxFrictionKind.SlowInteraction or UxFrictionKind.SqlFailure),
            FrictionIncidents = frictionIncidents,
            ControlApdexRows = apdexRows,
            Sessions = sessions,
            PivotRows = pivotRows,
            WindowEvents = currentEvents,
            WindowSqlEntries = filteredSql
        };
    }

    // ── 1. Stickiness & Period-over-Period Deltas ─────────────────────────────

    private static StickinessSummary ComputeStickiness(
        List<FlexTelemetryEvent> current,
        List<FlexTelemetryEvent> prior,
        List<FlexTelemetryEvent> last30d,
        DateTime nowUtc,
        int daysBack)
    {
        var wauCutoff = nowUtc.AddDays(-7);
        var wauUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mauUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in last30d)
        {
            var u = NormalizeUser(e.UserId);
            mauUsers.Add(u);
            if (e.TimestampUtc >= wauCutoff)
                wauUsers.Add(u);
        }

        int sparkDays = Math.Clamp(daysBack, 7, 30);
        var startDay = DateOnly.FromDateTime(nowUtc.ToLocalTime().AddDays(-(sparkDays - 1)));
        var dailyUsers = new Dictionary<DateOnly, HashSet<string>>();
        var dailyCounts = new Dictionary<DateOnly, int>();

        foreach (var e in current)
        {
            var d = DateOnly.FromDateTime(e.TimestampUtc.ToLocalTime());
            if (!dailyUsers.TryGetValue(d, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dailyUsers[d] = set;
            }
            set.Add(NormalizeUser(e.UserId));
            dailyCounts[d] = dailyCounts.GetValueOrDefault(d) + 1;
        }

        var dauSpark = new List<double>(sparkDays);
        var evtSpark = new List<double>(sparkDays);
        int peakDau = 0;
        int activeDaysCount = 0;
        int sumDau = 0;

        for (int i = 0; i < sparkDays; i++)
        {
            var day = startDay.AddDays(i);
            int uCnt = dailyUsers.TryGetValue(day, out var us) ? us.Count : 0;
            int eCnt = dailyCounts.GetValueOrDefault(day);
            dauSpark.Add(uCnt);
            evtSpark.Add(eCnt);
            if (uCnt > peakDau) peakDau = uCnt;
            if (uCnt > 0)
            {
                sumDau += uCnt;
                activeDaysCount++;
            }
        }

        double avgDau = activeDaysCount > 0 ? Math.Round((double)sumDau / activeDaysCount, 1) : 0;
        int wau = Math.Max(wauUsers.Count, peakDau);
        int mau = Math.Max(mauUsers.Count, wau);

        double dauOverWau = wau > 0 ? Math.Clamp(Math.Round(avgDau / wau * 100.0, 1), 0, 100) : 0;
        double wauOverMau = mau > 0 ? Math.Clamp(Math.Round((double)wau / mau * 100.0, 1), 0, 100) : 0;

        int curUsers = current.Select(e => NormalizeUser(e.UserId)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int priUsers = prior.Select(e => NormalizeUser(e.UserId)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int curOpens = current.Count(e => e.Kind == TelemetryEventKind.ScreenOpen);
        int priOpens = prior.Count(e => e.Kind == TelemetryEventKind.ScreenOpen);
        double curMins = current.Sum(e => e.DurationMs ?? 0) / 60000.0;
        double priMins = prior.Sum(e => e.DurationMs ?? 0) / 60000.0;

        return new StickinessSummary
        {
            PeakDau = peakDau,
            AverageDau = avgDau,
            Wau = wau,
            Mau = mau,
            DauOverWauPercent = dauOverWau,
            WauOverMauPercent = wauOverMau,
            EventsDeltaPercent = ComputeDeltaPercent(current.Count, prior.Count),
            UsersDeltaPercent = ComputeDeltaPercent(curUsers, priUsers),
            ScreenOpensDeltaPercent = ComputeDeltaPercent(curOpens, priOpens),
            ActiveMinutesDeltaPercent = ComputeDeltaPercent(curMins, priMins),
            DailyActiveUsersSparkline = dauSpark,
            DailyEventsSparkline = evtSpark
        };
    }

    // ── 2. Feature Adoption Matrix & Hierarchy Sunburst / Treemap ─────────────

    private sealed class AdoptionAcc(string route, string title, string module)
    {
        public string Route { get; } = route;
        public string Title { get; } = title;
        public string Module { get; } = module;
        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Opens;
        public int Actions;
        public long DwellMs;
        public int PriorActions;
        public Dictionary<string, int> Controls { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static (List<FeatureAdoptionRow> Rows, double MedShare, double MedFreq, SunburstNode? Sunburst, List<ChartDataPoint> Treemap)
        ComputeFeatureAdoption(
            List<FlexTelemetryEvent> current,
            List<FlexTelemetryEvent> prior,
            ITelemetryCatalogProvider? catalog,
            string? moduleFilter)
    {
        var map = new Dictionary<string, AdoptionAcc>(StringComparer.OrdinalIgnoreCase);
        if (catalog is not null)
        {
            foreach (var ks in catalog.GetKnownScreens())
            {
                if (moduleFilter is not null && !string.Equals(ks.Module, moduleFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                map[ks.RouteKey] = new AdoptionAcc(ks.RouteKey, ks.Title, ks.Module);
            }
        }

        var allUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in current)
        {
            var u = NormalizeUser(e.UserId);
            allUsers.Add(u);

            var normRoute = NormalizeRoute(e.Route);
            if (IsShellRoute(normRoute) && string.IsNullOrWhiteSpace(e.Screen))
                continue;

            var (title, mod) = ResolveScreen(catalog, normRoute, e.Screen);
            if (moduleFilter is not null && !string.Equals(mod, moduleFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = !IsShellRoute(normRoute) ? normRoute : $"screen:{title}";
            if (!map.TryGetValue(key, out var acc))
            {
                acc = new AdoptionAcc(normRoute, title, mod);
                map[key] = acc;
            }

            acc.Users.Add(u);
            acc.Actions++;
            if (e.Kind == TelemetryEventKind.ScreenOpen)
                acc.Opens++;
            if (e.DurationMs is > 0)
                acc.DwellMs += e.DurationMs.Value;

            if (!string.IsNullOrWhiteSpace(e.Control) || !string.IsNullOrWhiteSpace(e.Action))
            {
                var cLabel = !string.IsNullOrWhiteSpace(e.Control)
                    ? $"{e.Category}:{e.Control}"
                    : $"{e.Category}:{e.Action}";
                acc.Controls[cLabel] = acc.Controls.GetValueOrDefault(cLabel) + 1;
            }
        }

        foreach (var pe in prior)
        {
            var normRoute = NormalizeRoute(pe.Route);
            var (title, _) = ResolveScreen(catalog, normRoute, pe.Screen);
            var key = !IsShellRoute(normRoute) ? normRoute : $"screen:{title}";
            if (map.TryGetValue(key, out var acc))
                acc.PriorActions++;
        }

        int totalUsers = Math.Max(1, allUsers.Count);

        var activeShares = map.Values
            .Where(a => a.Actions > 0)
            .Select(a => a.Users.Count * 100.0 / totalUsers)
            .OrderBy(v => v)
            .ToList();
        var activeFreqs = map.Values
            .Where(a => a.Actions > 0 && a.Users.Count > 0)
            .Select(a => (double)a.Actions / a.Users.Count)
            .OrderBy(v => v)
            .ToList();

        double medShare = activeShares.Count > 0 ? Math.Round(Median(activeShares), 1) : 25.0;
        double medFreq = activeFreqs.Count > 0 ? Math.Round(Median(activeFreqs), 1) : 3.0;

        var rows = map.Values
            .Select(a =>
            {
                double share = a.Actions > 0 ? Math.Round(a.Users.Count * 100.0 / totalUsers, 1) : 0;
                double freq = a.Users.Count > 0 ? Math.Round((double)a.Actions / a.Users.Count, 1) : 0;
                var quad = a.Actions == 0
                    ? FeatureAdoptionQuadrant.Dormant
                    : (share >= medShare && freq >= medFreq)
                        ? FeatureAdoptionQuadrant.CoreDriver
                        : (share < medShare && freq >= medFreq)
                            ? FeatureAdoptionQuadrant.PowerNiche
                            : (share >= medShare && freq < medFreq)
                                ? FeatureAdoptionQuadrant.BroadUtility
                                : FeatureAdoptionQuadrant.Dormant;

                return new FeatureAdoptionRow
                {
                    ScreenTitle = a.Title,
                    Route = a.Route,
                    Module = a.Module,
                    UniqueUsers = a.Users.Count,
                    UserSharePercent = share,
                    OpenCount = a.Opens,
                    TotalActions = a.Actions,
                    ActionsPerUser = freq,
                    AvgDwellSeconds = a.Opens > 0 ? Math.Round(a.DwellMs / 1000.0 / a.Opens, 1) : 0,
                    Quadrant = quad,
                    PriorPeriodDeltaPercent = ComputeDeltaPercent(a.Actions, a.PriorActions)
                };
            })
            .OrderByDescending(r => r.TotalActions)
            .ThenBy(r => r.ScreenTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Build Sunburst (Module -> Screen -> Top Controls) & Treemap points
        SunburstNode? root = null;
        var treemap = new List<ChartDataPoint>();
        var activeAccs = map.Values.Where(a => a.Actions > 0).ToList();
        if (activeAccs.Count > 0)
        {
            root = new SunburstNode("All Modules", 0);
            int modIdx = 0;
            foreach (var modGrp in activeAccs.GroupBy(a => string.IsNullOrWhiteSpace(a.Module) ? "General" : a.Module).OrderByDescending(g => g.Sum(x => x.Actions)))
            {
                var modColor = DefaultHierarchyColors[modIdx++ % DefaultHierarchyColors.Length];
                var modNode = new SunburstNode(modGrp.Key, 0, modColor);
                foreach (var scr in modGrp.OrderByDescending(x => x.Actions).Take(10))
                {
                    treemap.Add(new ChartDataPoint
                    {
                        Category = modGrp.Key,
                        Label = scr.Title,
                        Value = scr.Actions,
                        Color = modColor
                    });

                    if (scr.Controls.Count == 0)
                    {
                        modNode.AddChild(new SunburstNode(scr.Title, scr.Actions, modColor));
                    }
                    else
                    {
                        var scrNode = new SunburstNode(scr.Title, 0, modColor);
                        foreach (var ctl in scr.Controls.OrderByDescending(kv => kv.Value).Take(8))
                        {
                            scrNode.AddChild(new SunburstNode(ctl.Key, ctl.Value, modColor));
                        }
                        modNode.AddChild(scrNode);
                    }
                }
                root.AddChild(modNode);
            }
        }

        return (rows, medShare, medFreq, root, treemap);
    }

    // ── 3. Conversion Funnels ─────────────────────────────────────────────────

    public static IReadOnlyList<TelemetryWorkflowPreset> BuildWorkflowPresets(
        ITelemetryWorkflowProvider? provider,
        IReadOnlyList<FlexTelemetryEvent> events,
        ITelemetryCatalogProvider? catalog)
    {
        var list = new List<TelemetryWorkflowPreset>();
        if (provider is not null)
        {
            foreach (var p in provider.GetWorkflowPresets())
            {
                if (p.Steps.Count >= 2)
                    list.Add(p);
            }
        }

        list.Add(new TelemetryWorkflowPreset(
            "auto-screen-to-commit",
            "Screen Open → Browse/Grid → Edit Input → Commit Action",
            "Generic behavioral conversion funnel from opening any screen through grid/input engagement to clicking an action button.",
            new[]
            {
                new TelemetryFunnelStepSpec("1. Open Screen", TelemetryFunnelMatchKind.EventKind, nameof(TelemetryEventKind.ScreenOpen)),
                new TelemetryFunnelStepSpec("2. Inspect / Grid Operation", TelemetryFunnelMatchKind.EventKind, nameof(TelemetryEventKind.GridOperation)),
                new TelemetryFunnelStepSpec("3. Edit Field / Input", TelemetryFunnelMatchKind.EventKind, nameof(TelemetryEventKind.ControlChange)),
                new TelemetryFunnelStepSpec("4. Trigger Button / Save", TelemetryFunnelMatchKind.EventKind, nameof(TelemetryEventKind.ButtonClick))
            }));

        var topScreens = events
            .Select(e => ResolveScreen(catalog, NormalizeRoute(e.Route), e.Screen).ScreenTitle)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(2)
            .ToList();

        if (topScreens.Count >= 1)
        {
            var top = topScreens[0];
            list.Add(new TelemetryWorkflowPreset(
                "auto-top-screen-depth",
                $"{top}: Visit → Interact → Commit",
                $"Engagement depth funnel on the most active screen ({top}).",
                new[]
                {
                    new TelemetryFunnelStepSpec($"Open / View {top}", TelemetryFunnelMatchKind.Screen, top),
                    new TelemetryFunnelStepSpec("Modify Input or Grid", TelemetryFunnelMatchKind.Action, "Changed|Sorted|Filtered|RowSelected|Edited"),
                    new TelemetryFunnelStepSpec("Execute Command Button", TelemetryFunnelMatchKind.EventKind, nameof(TelemetryEventKind.ButtonClick))
                }));
        }

        return list;
    }

    public static ComputedFunnelReport EvaluateFunnel(
        IReadOnlyList<FlexTelemetryEvent> events,
        IReadOnlyList<TelemetryFunnelStepSpec> steps,
        bool bySession = true,
        string funnelName = "Conversion Funnel",
        ITelemetryCatalogProvider? catalog = null)
    {
        if (steps.Count == 0)
            return new ComputedFunnelReport { Name = funnelName, ScopeLabel = bySession ? "Sessions" : "Users" };

        var grouped = events
            .OrderBy(e => e.TimestampUtc)
            .GroupBy(
                e => bySession
                    ? (!string.IsNullOrWhiteSpace(e.SessionId) ? e.SessionId : $"u:{NormalizeUser(e.UserId)}")
                    : NormalizeUser(e.UserId),
                StringComparer.OrdinalIgnoreCase);

        var stepConvertedCounts = new int[steps.Count];
        var stepDurations = new List<double>[steps.Count];
        for (int i = 0; i < steps.Count; i++)
            stepDurations[i] = new List<double>();

        var totalCompletionSeconds = new List<double>();
        var dropOffs = new List<FunnelDropOffRow>();

        foreach (var actorGrp in grouped)
        {
            int nextStep = 0;
            DateTime firstStepUtc = default;
            DateTime prevStepUtc = default;
            FlexTelemetryEvent? lastEvt = null;

            foreach (var evt in actorGrp)
            {
                lastEvt = evt;
                if (nextStep < steps.Count && MatchesFunnelStep(evt, steps[nextStep], catalog))
                {
                    stepConvertedCounts[nextStep]++;
                    if (nextStep == 0)
                    {
                        firstStepUtc = evt.TimestampUtc;
                    }
                    else
                    {
                        double sec = Math.Max(0, (evt.TimestampUtc - prevStepUtc).TotalSeconds);
                        stepDurations[nextStep].Add(sec);
                    }
                    prevStepUtc = evt.TimestampUtc;
                    nextStep++;
                    if (nextStep == steps.Count)
                    {
                        totalCompletionSeconds.Add(Math.Max(0, (evt.TimestampUtc - firstStepUtc).TotalSeconds));
                        break;
                    }
                }
            }

            if (nextStep > 0 && nextStep < steps.Count && lastEvt is not null)
            {
                var (scrTitle, _) = ResolveScreen(catalog, NormalizeRoute(lastEvt.Route), lastEvt.Screen);
                dropOffs.Add(new FunnelDropOffRow
                {
                    ActorKey = actorGrp.Key,
                    UserId = NormalizeUser(lastEvt.UserId),
                    SessionId = lastEvt.SessionId,
                    LastCompletedStep = nextStep,
                    DroppedBeforeStep = steps[nextStep].Label,
                    LastScreen = scrTitle,
                    LastAction = !string.IsNullOrWhiteSpace(lastEvt.Control)
                        ? $"{lastEvt.Control} ({lastEvt.Action})"
                        : lastEvt.Action,
                    LastSeenLocal = lastEvt.TimestampUtc.ToLocalTime()
                });
            }
        }

        int entered = stepConvertedCounts[0];
        int completed = stepConvertedCounts[^1];
        double overallPct = entered > 0 ? Math.Round(completed * 100.0 / entered, 1) : 0;

        var visualSteps = new List<FunnelStepItem>(steps.Count);
        var stepRows = new List<ComputedFunnelStepRow>(steps.Count);

        for (int i = 0; i < steps.Count; i++)
        {
            int count = stepConvertedCounts[i];
            int prev = i == 0 ? entered : stepConvertedCounts[i - 1];
            double ovPct = entered > 0 ? Math.Round(count * 100.0 / entered, 1) : 0;
            double stPct = i == 0 ? 100.0 : (prev > 0 ? Math.Round(count * 100.0 / prev, 1) : 0);
            int dropped = i == 0 ? 0 : Math.Max(0, prev - count);
            double dropPct = i == 0 ? 0 : (prev > 0 ? Math.Round(dropped * 100.0 / prev, 1) : 0);
            double medSec = stepDurations[i].Count > 0 ? Math.Round(Median(stepDurations[i]), 1) : 0;
            string ruleText = $"{steps[i].MatchKind}: {steps[i].Pattern}";

            visualSteps.Add(new FunnelStepItem(
                steps[i].Label,
                count,
                i > 0 && medSec > 0 ? medSec : null,
                subtitle: ruleText,
                key: $"step-{i + 1}"));

            stepRows.Add(new ComputedFunnelStepRow
            {
                StepNumber = i + 1,
                StepLabel = steps[i].Label,
                MatchRule = ruleText,
                ConvertedActors = count,
                OverallRatePercent = ovPct,
                StepRatePercent = stPct,
                DroppedActors = dropped,
                DropOffPercent = dropPct,
                MedianSecondsFromPrev = medSec
            });
        }

        return new ComputedFunnelReport
        {
            Name = funnelName,
            ScopeLabel = bySession ? "Sessions" : "Users",
            TotalEntered = entered,
            TotalCompleted = completed,
            OverallConversionPercent = overallPct,
            MedianTotalSeconds = totalCompletionSeconds.Count > 0 ? Math.Round(Median(totalCompletionSeconds), 1) : 0,
            VisualSteps = visualSteps,
            StepRows = stepRows,
            DropOffRows = dropOffs.OrderByDescending(d => d.LastSeenLocal).Take(200).ToList()
        };
    }

    private static bool MatchesFunnelStep(FlexTelemetryEvent evt, TelemetryFunnelStepSpec spec, ITelemetryCatalogProvider? catalog)
    {
        if (string.IsNullOrWhiteSpace(spec.Pattern))
            return false;

        var alternatives = spec.Pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (screenTitle, _) = ResolveScreen(catalog, NormalizeRoute(evt.Route), evt.Screen);

        foreach (var pat in alternatives)
        {
            bool matched = spec.MatchKind switch
            {
                TelemetryFunnelMatchKind.Screen =>
                    screenTitle.Contains(pat, StringComparison.OrdinalIgnoreCase)
                    || evt.Screen.Contains(pat, StringComparison.OrdinalIgnoreCase)
                    || evt.Route.Contains(pat, StringComparison.OrdinalIgnoreCase),
                TelemetryFunnelMatchKind.Action =>
                    evt.Action.Contains(pat, StringComparison.OrdinalIgnoreCase),
                TelemetryFunnelMatchKind.Tag =>
                    !string.IsNullOrEmpty(evt.Tag) && evt.Tag.Contains(pat, StringComparison.OrdinalIgnoreCase),
                TelemetryFunnelMatchKind.ControlLocation =>
                    evt.Location.Contains(pat, StringComparison.OrdinalIgnoreCase)
                    || evt.Control.Contains(pat, StringComparison.OrdinalIgnoreCase),
                TelemetryFunnelMatchKind.EventKind =>
                    string.Equals(evt.Kind.ToString(), pat, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (matched)
                return true;
        }
        return false;
    }

    // ── 4. Multi-Stage User Journeys & Ping-Pong Workflow Loops ───────────────

    public static UserJourneyReport BuildUserJourneys(
        IReadOnlyList<FlexTelemetryEvent> events,
        ITelemetryCatalogProvider? catalog = null,
        string? anchorScreen = null,
        bool reverseMode = false,
        int maxStages = 4,
        int topBranchesPerStage = 6)
    {
        maxStages = Math.Clamp(maxStages, 2, 6);
        var sessionStreams = new List<(string SessionKey, string User, List<(string Screen, DateTime Utc)> Steps)>();
        var screenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var grp in events
                     .OrderBy(e => e.TimestampUtc)
                     .GroupBy(e => !string.IsNullOrWhiteSpace(e.SessionId) ? e.SessionId : $"u:{NormalizeUser(e.UserId)}", StringComparer.Ordinal))
        {
            var seq = new List<(string Screen, DateTime Utc)>();
            foreach (var e in grp)
            {
                if (e.Kind == TelemetryEventKind.ScreenClose)
                    continue;
                var normRoute = NormalizeRoute(e.Route);
                if (IsShellRoute(normRoute) && string.IsNullOrWhiteSpace(e.Screen))
                    continue;

                var (scr, _) = ResolveScreen(catalog, normRoute, e.Screen);
                if (string.IsNullOrWhiteSpace(scr))
                    continue;

                screenSet.Add(scr);
                if (seq.Count == 0 || !string.Equals(seq[^1].Screen, scr, StringComparison.OrdinalIgnoreCase))
                {
                    seq.Add((scr, e.TimestampUtc));
                }
            }
            if (seq.Count > 0)
                sessionStreams.Add((grp.Key, NormalizeUser(grp.First().UserId), seq));
        }

        // Pairwise transitions & Ping-Pong loop detection (A -> B -> A -> B within 120s)
        var pairMap = new Dictionary<string, (string From, string To, int Count, HashSet<string> Sessions, HashSet<string> Users, List<double> Secs)>(StringComparer.OrdinalIgnoreCase);
        var loopMap = new Dictionary<string, (string A, string B, int Count, HashSet<string> Sessions, HashSet<string> Users, List<double> Secs, DateTime LastLocal)>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sessionStreams)
        {
            var seq = s.Steps;
            for (int i = 0; i < seq.Count - 1; i++)
            {
                var from = seq[i].Screen;
                var to = seq[i + 1].Screen;
                var key = $"{from}||{to}";
                if (!pairMap.TryGetValue(key, out var p))
                    p = (from, to, 0, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new List<double>());
                p.Count++;
                p.Sessions.Add(s.SessionKey);
                p.Users.Add(s.User);
                p.Secs.Add(Math.Max(0, (seq[i + 1].Utc - seq[i].Utc).TotalSeconds));
                pairMap[key] = p;
            }

            for (int i = 0; i <= seq.Count - 4; i++)
            {
                var s0 = seq[i].Screen;
                var s1 = seq[i + 1].Screen;
                var s2 = seq[i + 2].Screen;
                var s3 = seq[i + 3].Screen;
                double spanSec = (seq[i + 3].Utc - seq[i].Utc).TotalSeconds;
                if (!string.Equals(s0, s1, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s0, s2, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s1, s3, StringComparison.OrdinalIgnoreCase)
                    && spanSec <= 180)
                {
                    var ordered = string.Compare(s0, s1, StringComparison.OrdinalIgnoreCase) <= 0 ? (s0, s1) : (s1, s0);
                    var lKey = $"{ordered.Item1}<->{ordered.Item2}";
                    if (!loopMap.TryGetValue(lKey, out var lp))
                        lp = (ordered.Item1, ordered.Item2, 0, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new List<double>(), seq[i + 3].Utc.ToLocalTime());
                    lp.Count++;
                    lp.Sessions.Add(s.SessionKey);
                    lp.Users.Add(s.User);
                    lp.Secs.Add(Math.Max(1, spanSec));
                    var localEnd = seq[i + 3].Utc.ToLocalTime();
                    if (localEnd > lp.LastLocal) lp.LastLocal = localEnd;
                    loopMap[lKey] = lp;
                }
            }
        }

        // Extract anchored or entry multi-stage sequences
        var paths = new List<List<string>>();
        bool hasAnchor = !string.IsNullOrWhiteSpace(anchorScreen) && !string.Equals(anchorScreen, "All", StringComparison.OrdinalIgnoreCase);

        foreach (var s in sessionStreams)
        {
            var seq = s.Steps;
            if (!reverseMode)
            {
                int startIdx = 0;
                if (hasAnchor)
                {
                    startIdx = seq.FindIndex(x => string.Equals(x.Screen, anchorScreen, StringComparison.OrdinalIgnoreCase));
                    if (startIdx < 0) continue;
                }
                var slice = new List<string>(maxStages);
                for (int k = 0; k < maxStages && startIdx + k < seq.Count; k++)
                    slice.Add(seq[startIdx + k].Screen);
                if (slice.Count >= 2 || (slice.Count == 1 && seq.Count == 1))
                {
                    if (slice.Count < maxStages)
                        slice.Add("(Session Exit)");
                    paths.Add(slice);
                }
            }
            else
            {
                int endIdx = seq.Count - 1;
                if (hasAnchor)
                {
                    endIdx = seq.FindLastIndex(x => string.Equals(x.Screen, anchorScreen, StringComparison.OrdinalIgnoreCase));
                    if (endIdx <= 0) continue;
                }
                if (endIdx < 1) continue;
                int startIdx = Math.Max(0, endIdx - (maxStages - 1));
                var slice = new List<string>(maxStages);
                for (int k = startIdx; k <= endIdx; k++)
                    slice.Add(seq[k].Screen);
                paths.Add(slice);
            }
        }

        // Top-N pruning per stage + graph construction
        var stageCounts = new Dictionary<(int Stage, string Label), int>();
        foreach (var p in paths)
        {
            for (int st = 0; st < p.Count; st++)
            {
                int alignedStage = reverseMode ? (maxStages - p.Count + st) : st;
                var key = (alignedStage, p[st]);
                stageCounts[key] = stageCounts.GetValueOrDefault(key) + 1;
            }
        }

        var topPerStage = stageCounts
            .GroupBy(kv => kv.Key.Stage)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Value)
                      .Take(topBranchesPerStage)
                      .Select(x => x.Key.Label)
                      .ToHashSet(StringComparer.OrdinalIgnoreCase));

        var nodeTotals = new Dictionary<string, PathFlowNode>(StringComparer.Ordinal);
        var linkTotals = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var p in paths)
        {
            string? prevNodeId = null;
            for (int st = 0; st < p.Count; st++)
            {
                int alignedStage = reverseMode ? (maxStages - p.Count + st) : st;
                string rawLabel = p[st];
                bool isExit = string.Equals(rawLabel, "(Session Exit)", StringComparison.Ordinal);
                string bucketLabel = isExit
                    ? "(Session Exit)"
                    : (topPerStage.TryGetValue(alignedStage, out var allowed) && allowed.Contains(rawLabel) ? rawLabel : "Other Screens");

                string nodeId = $"S{alignedStage}:{bucketLabel}";
                if (!nodeTotals.TryGetValue(nodeId, out var node))
                {
                    node = new PathFlowNode
                    {
                        Id = nodeId,
                        Stage = alignedStage,
                        Label = bucketLabel,
                        Value = 0,
                        IsDropOff = isExit
                    };
                    nodeTotals[nodeId] = node;
                }
                node.Value += 1;

                if (prevNodeId is not null)
                {
                    var lKey = $"{prevNodeId}||{nodeId}";
                    linkTotals[lKey] = linkTotals.GetValueOrDefault(lKey) + 1;
                }

                prevNodeId = nodeId;
                if (isExit) break;
            }
        }

        var links = linkTotals
            .Select(kv =>
            {
                var parts = kv.Key.Split("||");
                return new PathFlowLink
                {
                    SourceId = parts[0],
                    TargetId = parts[1],
                    Value = kv.Value
                };
            })
            .OrderByDescending(l => l.Value)
            .ToList();

        var titles = new List<string>(maxStages);
        for (int i = 0; i < maxStages; i++)
        {
            titles.Add(reverseMode
                ? (i == maxStages - 1 ? "Target Screen" : $"Step −{maxStages - 1 - i}")
                : (i == 0 ? "Entry / Start" : $"Step {i + 1}"));
        }

        var transitions = pairMap.Values
            .OrderByDescending(p => p.Count)
            .Take(100)
            .Select(p => new UserTransitionRow
            {
                FromScreen = p.From,
                ToScreen = p.To,
                TransitionCount = p.Count,
                UniqueSessions = p.Sessions.Count,
                UniqueUsers = p.Users.Count,
                MedianTransitionSeconds = p.Secs.Count > 0 ? Math.Round(Median(p.Secs), 1) : 0
            })
            .ToList();

        var loops = loopMap.Values
            .OrderByDescending(l => l.Count)
            .Select(l => new PingPongLoopRow
            {
                ScreenA = l.A,
                ScreenB = l.B,
                LoopCount = l.Count,
                AffectedSessions = l.Sessions.Count,
                AffectedUsers = l.Users.Count,
                AvgLoopDurationSeconds = l.Secs.Count > 0 ? Math.Round(l.Secs.Average(), 1) : 0,
                LastOccurredLocal = l.LastLocal
            })
            .ToList();

        return new UserJourneyReport
        {
            AvailableScreens = screenSet.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            Nodes = nodeTotals.Values.OrderBy(n => n.Stage).ThenByDescending(n => n.Value).ToList(),
            Links = links,
            StageTitles = titles,
            TopTransitions = transitions,
            PingPongLoops = loops
        };
    }

    // ── 5. Retention Cohorts & User Lifecycle ─────────────────────────────────

    public static CohortRetentionReport BuildCohortReport(
        IReadOnlyList<FlexTelemetryEvent> events,
        ITelemetryCatalogProvider? catalog,
        bool weekly,
        DateTime nowUtc)
    {
        string granLabel = weekly ? "Weekly" : "Daily";
        int maxPeriods = weekly ? 6 : 8;
        var periodHeaders = new List<string>(maxPeriods);
        for (int i = 0; i < maxPeriods; i++)
            periodHeaders.Add(weekly ? (i == 0 ? "Week 0" : $"Week +{i}") : (i == 0 ? "Day 0" : $"Day +{i}"));

        if (events.Count == 0)
        {
            return new CohortRetentionReport
            {
                Granularity = granLabel,
                PeriodHeaders = periodHeaders
            };
        }

        DateOnly BucketStart(DateTime utc)
        {
            var local = DateOnly.FromDateTime(utc.ToLocalTime());
            if (!weekly) return local;
            int diff = ((int)local.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return local.AddDays(-diff);
        }

        int BucketOffset(DateOnly start, DateOnly current) =>
            weekly ? (current.DayNumber - start.DayNumber) / 7 : (current.DayNumber - start.DayNumber);

        var currentBucket = BucketStart(nowUtc);
        var userBuckets = new Dictionary<string, HashSet<DateOnly>>(StringComparer.OrdinalIgnoreCase);
        var userStats = new Dictionary<string, (HashSet<DateOnly> Days, HashSet<string> Screens, HashSet<string> Modules, Dictionary<string, int> ModCounts, int Total, DateTime LastUtc)>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in events)
        {
            var u = NormalizeUser(e.UserId);
            var b = BucketStart(e.TimestampUtc);
            if (!userBuckets.TryGetValue(u, out var bSet))
            {
                bSet = new HashSet<DateOnly>();
                userBuckets[u] = bSet;
            }
            bSet.Add(b);

            var normRoute = NormalizeRoute(e.Route);
            var (scr, mod) = ResolveScreen(catalog, normRoute, e.Screen);
            if (!userStats.TryGetValue(u, out var st))
            {
                st = (new HashSet<DateOnly>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 0, e.TimestampUtc);
            }
            st.Days.Add(DateOnly.FromDateTime(e.TimestampUtc.ToLocalTime()));
            if (!string.IsNullOrWhiteSpace(scr)) st.Screens.Add(scr);
            if (!string.IsNullOrWhiteSpace(mod))
            {
                st.Modules.Add(mod);
                st.ModCounts[mod] = st.ModCounts.GetValueOrDefault(mod) + 1;
            }
            st.Total++;
            if (e.TimestampUtc > st.LastUtc) st.LastUtc = e.TimestampUtc;
            userStats[u] = st;
        }

        var firstBucketByUser = userBuckets.ToDictionary(kv => kv.Key, kv => kv.Value.Min(), StringComparer.OrdinalIgnoreCase);
        var cohortGroups = firstBucketByUser
            .GroupBy(kv => kv.Value)
            .OrderByDescending(g => g.Key)
            .Take(8)
            .OrderBy(g => g.Key)
            .ToList();

        var cohortRows = new List<CohortMatrixRow>(cohortGroups.Count);
        foreach (var cg in cohortGroups)
        {
            var cohortStart = cg.Key;
            var members = cg.Select(x => x.Key).ToList();
            int availableHorizon = Math.Clamp(BucketOffset(cohortStart, currentBucket) + 1, 1, maxPeriods);
            var counts = new int[availableHorizon];
            for (int p = 0; p < availableHorizon; p++)
            {
                var targetBucket = weekly ? cohortStart.AddDays(p * 7) : cohortStart.AddDays(p);
                int active = 0;
                foreach (var u in members)
                {
                    if (userBuckets[u].Contains(targetBucket))
                        active++;
                }
                counts[p] = active;
            }

            cohortRows.Add(new CohortMatrixRow
            {
                CohortLabel = weekly ? $"Wk of {cohortStart:MMM dd}" : cohortStart.ToString("MMM dd (ddd)", CultureInfo.InvariantCulture),
                CohortSize = members.Count,
                ActiveCounts = counts
            });
        }

        // User Lifecycle buckets across observed periods
        var allObservedBuckets = userBuckets.Values.SelectMany(x => x).Distinct().OrderBy(d => d).TakeLast(8).ToList();
        var lifecycle = new List<UserLifecycleBucketRow>(allObservedBuckets.Count);
        var seenEver = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? prevActive = null;

        foreach (var b in allObservedBuckets)
        {
            var activeNow = userBuckets
                .Where(kv => kv.Value.Contains(b))
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int newU = 0, retainedU = 0, resurrectedU = 0, dormantU = 0;
            foreach (var u in activeNow)
            {
                if (!seenEver.Contains(u))
                    newU++;
                else if (prevActive is not null && prevActive.Contains(u))
                    retainedU++;
                else
                    resurrectedU++;
            }

            if (prevActive is not null)
            {
                foreach (var pu in prevActive)
                {
                    if (!activeNow.Contains(pu))
                        dormantU++;
                }
            }

            foreach (var u in activeNow)
                seenEver.Add(u);
            prevActive = activeNow;

            lifecycle.Add(new UserLifecycleBucketRow
            {
                PeriodLabel = weekly ? b.ToString("MM/dd", CultureInfo.InvariantCulture) : b.ToString("MM/dd", CultureInfo.InvariantCulture),
                NewUsers = newU,
                RetainedUsers = retainedU,
                ResurrectedUsers = resurrectedU,
                DormantUsers = dormantU
            });
        }

        // RFM User Engagement Segments
        var segments = userStats
            .Select(kv =>
            {
                var st = kv.Value;
                double daysSince = Math.Round(Math.Max(0, (nowUtc - st.LastUtc).TotalDays), 1);
                string seg = (st.Days.Count >= 3 && st.Total >= 25 && daysSince <= 7)
                    ? "Champion"
                    : (daysSince > 14)
                        ? "At-Risk / Dormant"
                        : (st.Total >= 8 ? "Regular" : "Occasional");
                string topMod = st.ModCounts.OrderByDescending(m => m.Value).Select(m => m.Key).FirstOrDefault() ?? "General";

                return new UserEngagementSegmentRow
                {
                    UserId = kv.Key,
                    Segment = seg,
                    ActiveDays = st.Days.Count,
                    DistinctScreens = st.Screens.Count,
                    DistinctModules = st.Modules.Count,
                    TotalActions = st.Total,
                    DaysSinceLastSeen = daysSince,
                    TopModule = topMod
                };
            })
            .OrderByDescending(r => r.TotalActions)
            .ToList();

        return new CohortRetentionReport
        {
            Granularity = granLabel,
            PeriodHeaders = periodHeaders,
            CohortRows = cohortRows,
            LifecycleBuckets = lifecycle,
            UserSegments = segments
        };
    }

    // ── 6. UX Friction Intelligence & Click-to-SQL Apdex ──────────────────────

    private static (List<UxFrictionIncident> Incidents, List<ControlApdexRow> ApdexRows, double OverallApdex)
        DetectFrictionAndApdex(
            List<FlexTelemetryEvent> events,
            List<FlexSqlTelemetryEntry> sqlEntries,
            ITelemetryCatalogProvider? catalog)
    {
        var incidents = new List<UxFrictionIncident>();
        var sqlBySession = sqlEntries
            .Where(s => !string.IsNullOrWhiteSpace(s.SessionId))
            .GroupBy(s => s.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.TimestampUtc).ToList(), StringComparer.Ordinal);

        // Standalone SQL Errors
        foreach (var sq in sqlEntries.Where(q => q.HasError))
        {
            incidents.Add(new UxFrictionIncident
            {
                TimestampUtc = sq.TimestampUtc,
                TimestampLocal = sq.TimestampUtc.ToLocalTime(),
                Kind = UxFrictionKind.SqlFailure,
                Severity = "Error",
                UserId = NormalizeUser(sq.UserId),
                SessionId = sq.SessionId,
                Screen = string.IsNullOrWhiteSpace(sq.Screen) ? "Database" : sq.Screen,
                Location = sq.Tables,
                Summary = $"SQL {sq.ErrorClass}: {sq.ErrorMessage}",
                LatencyMs = sq.ElapsedMs,
                RelatedSql = sq.SqlPreview
            });
        }

        // Per-session behavioral heuristics (Rage Clicks, Form Abandonment, Filter Thrash)
        foreach (var sessGrp in events.GroupBy(
                     e => !string.IsNullOrWhiteSpace(e.SessionId) ? e.SessionId : $"u:{NormalizeUser(e.UserId)}",
                     StringComparer.Ordinal))
        {
            var sessEvents = sessGrp.OrderBy(e => e.TimestampUtc).ToList();
            var sessSql = sqlBySession.GetValueOrDefault(sessGrp.Key);

            // A. Rage Clicks (>= 3 clicks on identical Location/Control within 2,000ms)
            int i = 0;
            while (i < sessEvents.Count)
            {
                var e0 = sessEvents[i];
                if (e0.Kind is TelemetryEventKind.ButtonClick or TelemetryEventKind.MenuAction && !string.IsNullOrWhiteSpace(e0.Control))
                {
                    int j = i + 1;
                    while (j < sessEvents.Count
                           && (sessEvents[j].TimestampUtc - e0.TimestampUtc).TotalMilliseconds <= 2000
                           && sessEvents[j].Kind == e0.Kind
                           && string.Equals(sessEvents[j].Control, e0.Control, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(sessEvents[j].Screen, e0.Screen, StringComparison.OrdinalIgnoreCase))
                    {
                        j++;
                    }
                    int burst = j - i;
                    if (burst >= 3)
                    {
                        var (scr, _) = ResolveScreen(catalog, NormalizeRoute(e0.Route), e0.Screen);
                        var loc = !string.IsNullOrWhiteSpace(e0.Location) ? e0.Location : $"{scr} > {e0.Category}:{e0.Control}";
                        incidents.Add(new UxFrictionIncident
                        {
                            TimestampUtc = e0.TimestampUtc,
                            TimestampLocal = e0.TimestampUtc.ToLocalTime(),
                            Kind = UxFrictionKind.RageClick,
                            Severity = "High",
                            UserId = NormalizeUser(e0.UserId),
                            SessionId = e0.SessionId,
                            Screen = scr,
                            Location = loc,
                            Tag = e0.Tag,
                            EventCount = burst,
                            Summary = $"Clicked '{e0.Control}' {burst}× in {(sessEvents[j - 1].TimestampUtc - e0.TimestampUtc).TotalMilliseconds:0}ms"
                        });
                        i = j;
                        continue;
                    }
                }
                i++;
            }

            // B. Grid Filter / Search Thrashing (>= 4 filter/search ops within 30s without row selection/edit)
            for (int k = 0; k < sessEvents.Count; k++)
            {
                var ek = sessEvents[k];
                if (ek.Kind == TelemetryEventKind.GridOperation && IsFilterOrSearchAction(ek.Action))
                {
                    int m = k + 1;
                    int filterCount = 1;
                    bool productive = false;
                    while (m < sessEvents.Count && (sessEvents[m].TimestampUtc - ek.TimestampUtc).TotalSeconds <= 30)
                    {
                        var em = sessEvents[m];
                        if (em.Kind == TelemetryEventKind.GridOperation && string.Equals(em.Control, ek.Control, StringComparison.OrdinalIgnoreCase))
                        {
                            if (IsFilterOrSearchAction(em.Action))
                                filterCount++;
                            else
                                productive = true;
                        }
                        m++;
                    }
                    if (filterCount >= 4 && !productive)
                    {
                        var (scr, _) = ResolveScreen(catalog, NormalizeRoute(ek.Route), ek.Screen);
                        incidents.Add(new UxFrictionIncident
                        {
                            TimestampUtc = ek.TimestampUtc,
                            TimestampLocal = ek.TimestampUtc.ToLocalTime(),
                            Kind = UxFrictionKind.FilterThrash,
                            Severity = "Medium",
                            UserId = NormalizeUser(ek.UserId),
                            SessionId = ek.SessionId,
                            Screen = scr,
                            Location = !string.IsNullOrWhiteSpace(ek.Location) ? ek.Location : $"{scr} > GridControl:{ek.Control}",
                            Tag = ek.Tag,
                            EventCount = filterCount,
                            Summary = $"{filterCount} rapid filter/search adjustments on '{ek.Control}' without selecting a record"
                        });
                        k = m - 1;
                    }
                }
            }

            // C. Abandoned Form / Dialog Edits (>= 2 ControlChange inputs on a screen closed/left with 0 commit click & 0 SQL writes)
            string? activeScreen = null;
            DateTime screenEnterUtc = default;
            int inputEdits = 0;
            bool committed = false;
            FlexTelemetryEvent? lastInputEvt = null;

            void FlushScreenVisit(DateTime exitUtc)
            {
                if (activeScreen is not null && inputEdits >= 2 && !committed)
                {
                    bool hadSqlWrite = sessSql is not null && sessSql.Any(q =>
                        q.IsWrite && q.TimestampUtc >= screenEnterUtc.AddMilliseconds(-100) && q.TimestampUtc <= exitUtc.AddSeconds(2));
                    if (!hadSqlWrite && lastInputEvt is not null)
                    {
                        incidents.Add(new UxFrictionIncident
                        {
                            TimestampUtc = lastInputEvt.TimestampUtc,
                            TimestampLocal = lastInputEvt.TimestampUtc.ToLocalTime(),
                            Kind = UxFrictionKind.AbandonedForm,
                            Severity = "Medium",
                            UserId = NormalizeUser(lastInputEvt.UserId),
                            SessionId = lastInputEvt.SessionId,
                            Screen = activeScreen,
                            Location = lastInputEvt.Location,
                            Tag = lastInputEvt.Tag,
                            EventCount = inputEdits,
                            Summary = $"Modified {inputEdits} input fields on '{activeScreen}' then exited without saving"
                        });
                    }
                }
                inputEdits = 0;
                committed = false;
                lastInputEvt = null;
            }

            foreach (var ev in sessEvents)
            {
                var normRoute = NormalizeRoute(ev.Route);
                var (scr, _) = ResolveScreen(catalog, normRoute, ev.Screen);
                if (ev.Kind == TelemetryEventKind.ScreenClose)
                {
                    FlushScreenVisit(ev.TimestampUtc);
                    activeScreen = null;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(scr) && !string.Equals(scr, activeScreen, StringComparison.OrdinalIgnoreCase))
                {
                    FlushScreenVisit(ev.TimestampUtc);
                    activeScreen = scr;
                    screenEnterUtc = ev.TimestampUtc;
                }

                if (ev.Kind == TelemetryEventKind.ControlChange)
                {
                    inputEdits++;
                    lastInputEvt = ev;
                }
                else if (ev.Kind is TelemetryEventKind.ButtonClick or TelemetryEventKind.MenuAction && IsCommitAction(ev))
                {
                    committed = true;
                }
                else if (ev.Kind == TelemetryEventKind.GridOperation
                         && ev.Action.Contains("Save", StringComparison.OrdinalIgnoreCase))
                {
                    committed = true;
                }
            }

            if (sessEvents.Count > 0)
                FlushScreenVisit(sessEvents[^1].TimestampUtc);
        }

        // D. Unhandled Errors & Click-to-SQL Apdex Correlation (Satisfied <= 400ms, Tolerating <= 1600ms)
        const long apdexTargetMs = 400;
        const long apdexToleratingMs = 1600;
        var apdexMap = new Dictionary<string, (string Screen, string Loc, string Ctl, string Act, List<long> Samples, int SqlCnt, int SqlWrites, int SqlErrs)>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in events)
        {
            var (scr, _) = ResolveScreen(catalog, NormalizeRoute(ev.Route), ev.Screen);

            if (ev.Kind == TelemetryEventKind.UnhandledError || string.Equals(ev.Level, "ERROR", StringComparison.OrdinalIgnoreCase))
            {
                incidents.Add(new UxFrictionIncident
                {
                    TimestampUtc = ev.TimestampUtc,
                    TimestampLocal = ev.TimestampUtc.ToLocalTime(),
                    Kind = UxFrictionKind.UnhandledError,
                    Severity = "Error",
                    UserId = NormalizeUser(ev.UserId),
                    SessionId = ev.SessionId,
                    Screen = scr,
                    Location = ev.Location,
                    Tag = ev.Tag,
                    Summary = !string.IsNullOrWhiteSpace(ev.Detail) ? ev.Detail : $"{ev.Control}: {ev.Action}"
                });
                continue;
            }

            if (ev.Kind is not (TelemetryEventKind.ButtonClick or TelemetryEventKind.MenuAction or TelemetryEventKind.GridOperation or TelemetryEventKind.TaskLaunch or TelemetryEventKind.ReportRun))
                continue;

            long latencyMs = ev.DurationMs ?? 0;
            int corrSqlCount = 0;
            int corrWrites = 0;
            int corrErrors = 0;
            string? slowestSql = null;

            if (!string.IsNullOrWhiteSpace(ev.SessionId) && sqlBySession.TryGetValue(ev.SessionId, out var sSql))
            {
                long sqlSum = 0;
                long maxSingleSql = 0;
                foreach (var q in sSql)
                {
                    double deltaMs = (q.TimestampUtc - ev.TimestampUtc).TotalMilliseconds;
                    if (deltaMs >= -50 && deltaMs <= 2200)
                    {
                        corrSqlCount++;
                        sqlSum += q.ElapsedMs;
                        if (q.IsWrite) corrWrites++;
                        if (q.HasError) corrErrors++;
                        if (q.ElapsedMs > maxSingleSql)
                        {
                            maxSingleSql = q.ElapsedMs;
                            slowestSql = q.SqlPreview;
                        }
                    }
                }
                if (sqlSum > latencyMs)
                    latencyMs = sqlSum;
            }

            if (latencyMs >= 500)
            {
                incidents.Add(new UxFrictionIncident
                {
                    TimestampUtc = ev.TimestampUtc,
                    TimestampLocal = ev.TimestampUtc.ToLocalTime(),
                    Kind = UxFrictionKind.SlowInteraction,
                    Severity = latencyMs >= apdexToleratingMs ? "High" : "Warning",
                    UserId = NormalizeUser(ev.UserId),
                    SessionId = ev.SessionId,
                    Screen = scr,
                    Location = !string.IsNullOrWhiteSpace(ev.Location) ? ev.Location : $"{scr} > {ev.Category}:{ev.Control}",
                    Tag = ev.Tag,
                    LatencyMs = latencyMs,
                    RelatedSql = slowestSql,
                    Summary = $"'{ev.Control}' ({ev.Action}) took {latencyMs:N0}ms" + (corrSqlCount > 0 ? $" across {corrSqlCount} SQL call(s)" : string.Empty)
                });
            }

            var ctlName = string.IsNullOrWhiteSpace(ev.Control) ? ev.Category : ev.Control;
            var locKey = !string.IsNullOrWhiteSpace(ev.Location) ? ev.Location : $"{scr} > {ev.Category}:{ctlName}";
            var aKey = $"{scr}||{locKey}||{ev.Action}";
            if (!apdexMap.TryGetValue(aKey, out var entry))
            {
                entry = (scr, locKey, ctlName, ev.Action, new List<long>(), 0, 0, 0);
            }
            entry.Samples.Add(corrErrors > 0 ? apdexToleratingMs + 1000 : latencyMs);
            entry.SqlCnt += corrSqlCount;
            entry.SqlWrites += corrWrites;
            entry.SqlErrs += corrErrors;
            apdexMap[aKey] = entry;
        }

        int totalSat = 0, totalTol = 0, totalSamples = 0;
        var apdexRows = new List<ControlApdexRow>(apdexMap.Count);

        foreach (var kv in apdexMap.Values)
        {
            kv.Samples.Sort();
            int n = kv.Samples.Count;
            int sat = 0, tol = 0, fru = 0;
            foreach (var ms in kv.Samples)
            {
                if (ms <= apdexTargetMs) sat++;
                else if (ms <= apdexToleratingMs) tol++;
                else fru++;
            }
            totalSat += sat;
            totalTol += tol;
            totalSamples += n;

            double score = n > 0 ? Math.Round((sat + 0.5 * tol) / n, 2) : 1.0;
            long p95 = kv.Samples[Math.Clamp((int)Math.Ceiling(n * 0.95) - 1, 0, n - 1)];

            apdexRows.Add(new ControlApdexRow
            {
                Screen = kv.Screen,
                Location = kv.Loc,
                Control = kv.Ctl,
                Action = kv.Act,
                SampleCount = n,
                AvgMs = Math.Round(kv.Samples.Average(), 1),
                P95Ms = p95,
                SatisfiedCount = sat,
                ToleratingCount = tol,
                FrustratedCount = fru,
                ApdexScore = score,
                CorrelatedSqlCount = kv.SqlCnt,
                CorrelatedSqlWrites = kv.SqlWrites
            });
        }

        double overallApdex = totalSamples > 0 ? Math.Round((totalSat + 0.5 * totalTol) / totalSamples, 2) : 1.0;

        return (
            incidents.OrderByDescending(x => x.TimestampUtc).Take(300).ToList(),
            apdexRows.OrderBy(r => r.ApdexScore).ThenByDescending(r => r.SampleCount).Take(250).ToList(),
            overallApdex);
    }

    // ── 7. Session Summaries & Flight-Recorder Replay ─────────────────────────

    private static List<SessionSummaryRow> BuildSessionSummaries(
        List<FlexTelemetryEvent> events,
        List<FlexSqlTelemetryEntry> sqlEntries,
        List<UxFrictionIncident> friction,
        ITelemetryCatalogProvider? catalog)
    {
        var sqlCountBySession = sqlEntries
            .Where(q => !string.IsNullOrWhiteSpace(q.SessionId))
            .GroupBy(q => q.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var frictionCountBySession = friction
            .Where(f => !string.IsNullOrWhiteSpace(f.SessionId))
            .GroupBy(f => f.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return events
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId))
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.TimestampUtc).ToList();
                var first = ordered[0];
                var last = ordered[^1];
                var screens = ordered
                    .Select(e => ResolveScreen(catalog, NormalizeRoute(e.Route), e.Screen).ScreenTitle)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                return new SessionSummaryRow
                {
                    SessionId = g.Key,
                    UserId = NormalizeUser(first.UserId),
                    Tenant = first.Tenant,
                    StartLocal = first.TimestampUtc.ToLocalTime(),
                    EndLocal = last.TimestampUtc.ToLocalTime(),
                    DurationMinutes = Math.Round(Math.Max(0.1, (last.TimestampUtc - first.TimestampUtc).TotalMinutes), 1),
                    EventCount = ordered.Count,
                    ScreenCount = screens.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    SqlCount = sqlCountBySession.GetValueOrDefault(g.Key),
                    FrictionCount = frictionCountBySession.GetValueOrDefault(g.Key),
                    EntryScreen = screens.FirstOrDefault() ?? first.Screen,
                    ExitScreen = screens.LastOrDefault() ?? last.Screen
                };
            })
            .OrderByDescending(s => s.FrictionCount > 0 ? 1 : 0)
            .ThenByDescending(s => s.EndLocal)
            .Take(200)
            .ToList();
    }

    public static SessionReplayTrace? BuildSessionReplay(
        string sessionId,
        IReadOnlyList<FlexTelemetryEvent> events,
        IReadOnlyList<FlexSqlTelemetryEntry> sqlEntries,
        IReadOnlyList<UxFrictionIncident> frictionIncidents,
        ITelemetryCatalogProvider? catalog = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var sessEvents = events
            .Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        var sessSql = sqlEntries
            .Where(q => !q.IsBackgroundPoll && string.Equals(q.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(q => q.TimestampUtc)
            .ToList();

        var sessFriction = frictionIncidents
            .Where(f => string.Equals(f.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(f => f.TimestampUtc)
            .ToList();

        if (sessEvents.Count == 0 && sessSql.Count == 0)
            return null;

        DateTime startUtc = sessEvents.Count > 0 && sessSql.Count > 0
            ? (sessEvents[0].TimestampUtc <= sessSql[0].TimestampUtc ? sessEvents[0].TimestampUtc : sessSql[0].TimestampUtc)
            : (sessEvents.Count > 0 ? sessEvents[0].TimestampUtc : sessSql[0].TimestampUtc);
        DateTime endUtc = sessEvents.Count > 0 ? sessEvents[^1].TimestampUtc : sessSql[^1].TimestampUtc;
        if (sessSql.Count > 0 && sessSql[^1].TimestampUtc > endUtc)
            endUtc = sessSql[^1].TimestampUtc;

        var items = new List<SessionTimelineItem>(sessEvents.Count + sessSql.Count + sessFriction.Count);

        foreach (var e in sessEvents)
        {
            var (scr, _) = ResolveScreen(catalog, NormalizeRoute(e.Route), e.Screen);
            string title = e.Kind switch
            {
                TelemetryEventKind.ScreenOpen => $"Opened Screen: {scr}",
                TelemetryEventKind.ScreenClose => $"Closed Screen: {scr}",
                TelemetryEventKind.GridOperation => $"Grid {e.Action}: {e.Control}",
                TelemetryEventKind.ControlChange => $"Edited {e.Control} ({e.Action})",
                TelemetryEventKind.ButtonClick or TelemetryEventKind.MenuAction => $"Clicked {e.Control} ({e.Action})",
                TelemetryEventKind.UnhandledError => $"Error on {scr}: {e.Detail}",
                _ => $"{e.Category} {e.Control}: {e.Action}"
            };

            items.Add(new SessionTimelineItem
            {
                TimestampUtc = e.TimestampUtc,
                TimestampLocal = e.TimestampUtc.ToLocalTime(),
                OffsetSeconds = Math.Round(Math.Max(0, (e.TimestampUtc - startUtc).TotalSeconds), 2),
                SpanSeconds = e.DurationMs is > 0 ? Math.Round(e.DurationMs.Value / 1000.0, 2) : 0,
                EntryKind = e.Kind.ToString(),
                Screen = scr,
                Title = title,
                Subtitle = !string.IsNullOrWhiteSpace(e.Detail) ? e.Detail : e.Location,
                Location = e.Location,
                Tag = e.Tag,
                Value = e.Value,
                ElapsedMs = e.DurationMs,
                IsError = e.Kind == TelemetryEventKind.UnhandledError || string.Equals(e.Level, "ERROR", StringComparison.OrdinalIgnoreCase)
            });
        }

        foreach (var sq in sessSql)
        {
            items.Add(new SessionTimelineItem
            {
                TimestampUtc = sq.TimestampUtc,
                TimestampLocal = sq.TimestampUtc.ToLocalTime(),
                OffsetSeconds = Math.Round(Math.Max(0, (sq.TimestampUtc - startUtc).TotalSeconds), 2),
                SpanSeconds = Math.Round(sq.ElapsedMs / 1000.0, 3),
                EntryKind = "SqlQuery",
                Screen = sq.Screen,
                Title = $"SQL {sq.StatementKind} ({sq.Tables})",
                Subtitle = sq.SqlPreview,
                Location = sq.Source,
                Value = sq.RowCount.HasValue ? $"{sq.RowCount.Value:N0} rows" : null,
                ElapsedMs = sq.ElapsedMs,
                IsError = sq.HasError,
                IsFriction = sq.ElapsedMs >= 500 || sq.HasError,
                IsSqlWrite = sq.IsWrite
            });
        }

        foreach (var fr in sessFriction.Where(f => f.Kind is UxFrictionKind.RageClick or UxFrictionKind.AbandonedForm or UxFrictionKind.FilterThrash))
        {
            items.Add(new SessionTimelineItem
            {
                TimestampUtc = fr.TimestampUtc,
                TimestampLocal = fr.TimestampUtc.ToLocalTime(),
                OffsetSeconds = Math.Round(Math.Max(0, (fr.TimestampUtc - startUtc).TotalSeconds), 2),
                EntryKind = fr.KindLabel,
                Screen = fr.Screen,
                Title = $"⚠ {fr.KindLabel}",
                Subtitle = fr.Summary,
                Location = fr.Location,
                Tag = fr.Tag,
                ElapsedMs = fr.LatencyMs,
                IsFriction = true,
                IsError = string.Equals(fr.Severity, "Error", StringComparison.OrdinalIgnoreCase)
            });
        }

        items.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));

        var screensVisited = sessEvents
            .Select(e => ResolveScreen(catalog, NormalizeRoute(e.Route), e.Screen).ScreenTitle)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var summary = new SessionSummaryRow
        {
            SessionId = sessionId,
            UserId = sessEvents.Count > 0 ? NormalizeUser(sessEvents[0].UserId) : NormalizeUser(sessSql[0].UserId),
            Tenant = sessEvents.Count > 0 ? sessEvents[0].Tenant : sessSql[0].Tenant,
            StartLocal = startUtc.ToLocalTime(),
            EndLocal = endUtc.ToLocalTime(),
            DurationMinutes = Math.Round(Math.Max(0.1, (endUtc - startUtc).TotalMinutes), 1),
            EventCount = sessEvents.Count,
            ScreenCount = screensVisited.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            SqlCount = sessSql.Count,
            FrictionCount = sessFriction.Count,
            EntryScreen = screensVisited.FirstOrDefault() ?? string.Empty,
            ExitScreen = screensVisited.LastOrDefault() ?? string.Empty
        };

        return new SessionReplayTrace
        {
            Summary = summary,
            Items = items,
            FrictionIncidents = sessFriction
        };
    }

    // ── 8. Flattened Pivot Rows ───────────────────────────────────────────────

    private static List<TelemetryPivotRow> BuildPivotRows(
        List<FlexTelemetryEvent> events,
        ITelemetryCatalogProvider? catalog)
    {
        int start = Math.Max(0, events.Count - 5_000);
        var rows = new List<TelemetryPivotRow>(events.Count - start);

        for (int i = start; i < events.Count; i++)
        {
            var e = events[i];
            var local = e.TimestampUtc.ToLocalTime();
            var (scr, mod) = ResolveScreen(catalog, NormalizeRoute(e.Route), e.Screen);
            int hBlock = (local.Hour / 3) * 3;
            rows.Add(new TelemetryPivotRow
            {
                Tenant = string.IsNullOrWhiteSpace(e.Tenant) ? "Default" : e.Tenant,
                Module = string.IsNullOrWhiteSpace(mod) ? "General" : mod,
                Screen = string.IsNullOrWhiteSpace(scr) ? "Main" : scr,
                UserId = NormalizeUser(e.UserId),
                EventKind = e.Kind.ToString(),
                ControlType = string.IsNullOrWhiteSpace(e.Category) ? "Control" : e.Category,
                Control = string.IsNullOrWhiteSpace(e.Control) ? e.Action : e.Control,
                Action = string.IsNullOrWhiteSpace(e.Action) ? "Event" : e.Action,
                DayOfWeek = local.ToString("ddd", CultureInfo.InvariantCulture),
                DateLabel = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                HourBlock = $"{hBlock:00}:00–{hBlock + 2:00}:59",
                Events = 1,
                DurationSeconds = e.DurationMs.HasValue ? Math.Round(e.DurationMs.Value / 1000.0, 2) : 0
            });
        }
        return rows;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NormalizeUser(string? userId) =>
        string.IsNullOrWhiteSpace(userId) || string.Equals(userId, "anonymous", StringComparison.OrdinalIgnoreCase)
            ? "(anonymous)"
            : userId.Trim();

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "/";
        var r = route.Trim();
        int q = r.IndexOf('?');
        if (q >= 0) r = r[..q];
        if (!r.StartsWith('/')) r = "/" + r;
        return r.Length > 1 ? r.TrimEnd('/') : r;
    }

    private static bool IsShellRoute(string route) =>
        route is "/" or "/login" or "/logout" or "/error";

    private static (string ScreenTitle, string Module) ResolveScreen(
        ITelemetryCatalogProvider? catalog,
        string normRoute,
        string fallbackScreen)
    {
        if (catalog is not null)
            return catalog.ResolveScreenInfo(normRoute, fallbackScreen);
        var title = !string.IsNullOrWhiteSpace(fallbackScreen) ? fallbackScreen : normRoute;
        return (title, "General");
    }

    private static bool MatchesSearch(FlexTelemetryEvent e, string resolvedTitle, string resolvedModule, string search) =>
        resolvedTitle.Contains(search, StringComparison.OrdinalIgnoreCase)
        || resolvedModule.Contains(search, StringComparison.OrdinalIgnoreCase)
        || e.Screen.Contains(search, StringComparison.OrdinalIgnoreCase)
        || e.Location.Contains(search, StringComparison.OrdinalIgnoreCase)
        || e.Control.Contains(search, StringComparison.OrdinalIgnoreCase)
        || e.Action.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrEmpty(e.Tag) && e.Tag.Contains(search, StringComparison.OrdinalIgnoreCase))
        || e.UserId.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool IsFilterOrSearchAction(string? action) =>
        !string.IsNullOrWhiteSpace(action)
        && (action.Contains("Filter", StringComparison.OrdinalIgnoreCase)
            || action.Contains("Search", StringComparison.OrdinalIgnoreCase));

    private static bool IsCommitAction(FlexTelemetryEvent ev)
    {
        foreach (var kw in CommitKeywords)
        {
            if (ev.Control.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || ev.Action.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(ev.Tag) && ev.Tag.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static double? ComputeDeltaPercent(double current, double prior)
    {
        if (prior <= 0.0001)
            return current > 0 ? 100.0 : null;
        return Math.Round((current - prior) / prior * 100.0, 1);
    }

    private static double Median(List<double> sortedOrUnsorted)
    {
        if (sortedOrUnsorted.Count == 0) return 0;
        var copy = sortedOrUnsorted.OrderBy(x => x).ToList();
        int mid = copy.Count / 2;
        return copy.Count % 2 == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) / 2.0;
    }
}
