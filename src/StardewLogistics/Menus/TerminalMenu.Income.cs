using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Devices;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>What the Income tab is showing.</summary>
    internal enum IncomeView
    {
        Forecast,
        History,
        Expenses,
        Ledger
    }

    /// <summary>The terminal's Income tab: what the farm is earning, has earned, and needs to earn.</summary>
    /// <remarks>
    /// Four views sharing the grid's space. The forecast graphs what the network's producers will make over the
    /// coming days; history graphs what the ledger recorded. Either can be broken down by source, each source in
    /// its own colour and switchable on and off. The expense planner holds what the player is saving for and what
    /// their inputs cost, which turns income into profit and says how long the savings will take. The ledger lists
    /// each day's earnings, shipping itemised.
    /// </remarks>
    internal partial class TerminalMenu
    {
        /*********
        ** Fields
        *********/
        private static readonly int[] IncomeRanges = { 7, 28, 112 };

        /// <summary>How long the graph takes to draw itself in.</summary>
        private const double GraphAnimationSeconds = 0.7;

        /// <summary>How far ahead the expense planner looks for when savings would be met.</summary>
        private const int ExpenseHorizon = 336;

        private const int LedgerLineHeight = 40;

        private IncomeView IncomeMode = IncomeView.Forecast;

        /// <summary>When the Income view last changed, for its transition.</summary>
        private DateTime IncomeViewChangedAt = DateTime.MinValue;

        /// <summary>Which way the new view slides in from: 1 from the right, -1 from the left.</summary>
        private int IncomeViewDirection;

        /// <summary>Where the active view's highlight started gliding from.</summary>
        private Rectangle IncomeHighlightFrom;
        private bool IncomeCumulative;
        private int IncomeRangeIndex = 1;
        private bool IncomeBySource;

        /// <summary>Whether the graph's gold axis is logarithmic: good for a few big days among many small ones.</summary>
        private bool IncomeLogScale;

        /// <summary>Sources switched off in the breakdown, by name.</summary>
        private readonly HashSet<string> HiddenSources = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When the graph last started drawing itself in.</summary>
        private DateTime GraphAnimationStart = DateTime.UtcNow;

        /// <summary>The player's expense plan, read when the tab is first shown.</summary>
        private ExpensePlan ExpensePlanCache;

        /// <summary>Ledger days opened to show their items, by day number.</summary>
        private readonly HashSet<int> ExpandedLedgerDays = new();

        /// <summary>Clickable parts of the current view, rebuilt as it's drawn.</summary>
        private readonly List<(Rectangle Bounds, string Action, object Value)> IncomeHotspots = new();

        private ExpensePlan Expenses => this.ExpensePlanCache ??= ExpensePlan.Load();


        /*********
        ** Nested types
        *********/
        /// <summary>One coloured series on the graph.</summary>
        private class GraphSeries
        {
            public string Name { get; init; }
            public Color Colour { get; init; }
            public Item Icon { get; init; }
            public double[] Daily { get; init; }
            public double PerDay { get; init; }
            public double CostPerDay { get; init; }
            public bool Hidden { get; init; }
        }


        /*********
        ** Private methods: data
        *********/
        /// <summary>The network's producers, from the Shipping tab's cached summary.</summary>
        private List<IncomeSource> GetIncomeSources() => this.GetShippingSummary().Income;

        /// <summary>The series for the forecast: one per kind of producer, busiest first.</summary>
        private List<GraphSeries> GetForecastSeries(int days)
        {
            List<GraphSeries> series = new();
            int index = 0;
            foreach (IGrouping<string, IncomeSource> group in this.GetIncomeSources().GroupBy(source => source.Name).OrderByDescending(group => group.Sum(source => source.PerDay)))
            {
                series.Add(new GraphSeries
                {
                    Name = group.Key,
                    Colour = MoneyColours.ForSource(index++),
                    Icon = group.First().Icon,
                    Daily = IncomeForecast.Project(group, days),
                    PerDay = group.Sum(source => source.PerDay),
                    CostPerDay = group.Sum(source => source.CostPerDay(this.Expenses)),
                    Hidden = this.HiddenSources.Contains(group.Key)
                });
            }

            return series;
        }

        /// <summary>The series for history: shipping and everything else, over the last days.</summary>
        private List<GraphSeries> GetHistorySeries(int days)
        {
            double[] shipping = new double[days];
            double[] other = new double[days];
            int today = Game1.Date.TotalDays;

            foreach (LedgerDay day in this.Jobs?.Ledger?.Days ?? Array.Empty<LedgerDay>())
            {
                int index = days - (today - day.TotalDays);
                if (index < 0 || index >= days)
                    continue;
                shipping[index] += day.Shipping;
                other[index] += day.Other;
            }

            string shippingName = this.Translations.Get("income.series-shipping");
            string otherName = this.Translations.Get("income.series-other");
            return new List<GraphSeries>
            {
                new() { Name = shippingName, Colour = MoneyColours.ForSource(2), Daily = shipping, PerDay = shipping.Average(), Hidden = this.HiddenSources.Contains(shippingName) },
                new() { Name = otherName, Colour = MoneyColours.ForSource(1), Daily = other, PerDay = other.Average(), Hidden = this.HiddenSources.Contains(otherName) }
            };
        }

        /// <summary>Profit a day across the network: income, less what the inputs cost.</summary>
        private double GetDailyProfit()
        {
            return this.GetIncomeSources().Sum(source => source.PerDay - source.CostPerDay(this.Expenses));
        }

        /// <summary>Days until the planned expenses are met, or null if not within the horizon.</summary>
        private int? DaysToMeetExpenses(out long needed)
        {
            ExpensePlan plan = this.Expenses;
            needed = plan.Total - (plan.CountGoldOnHand ? Game1.player.Money : 0);
            if (needed <= 0)
                return 0;

            double[] daily = IncomeForecast.Project(this.GetIncomeSources(), ExpenseHorizon, source => source.Value - source.Cost(plan));
            double total = 0;
            for (int day = 0; day < daily.Length; day++)
            {
                total += daily[day];
                if (total >= needed)
                    return day + 1;
            }

            return null;
        }

        /// <summary>Items a cost might be set for: inputs of what's running, seeds of what's growing, and what's stored.</summary>
        private List<Item> GetCostCandidates()
        {
            List<string> ids = new();
            foreach (IncomeSource source in this.GetIncomeSources())
                ids.AddRange(source.Inputs.Select(input => input.ItemId));
            ids.AddRange(this.AllStock.Where(entry => entry.Sample?.Category == StardewValley.Object.SeedsCategory).Select(entry => entry.Sample.QualifiedItemId));
            ids.AddRange(this.AllStock.Select(entry => entry.Sample?.QualifiedItemId));

            return ids
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => ItemRegistry.QualifyItemId(StockId.BaseId(id)))
                .Where(id => id != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => ItemRegistry.Create(id, allowNull: true))
                .Where(item => item != null)
                .Take(300)
                .ToList();
        }

        /// <summary>Restarts the graph's draw-in animation.</summary>
        private void RestartGraphAnimation() => this.GraphAnimationStart = DateTime.UtcNow;

        /// <summary>How far the graph has drawn itself in, eased: 0 to 1.</summary>
        private float GraphProgress()
        {
            return UiAnimation.EaseOut(UiAnimation.Progress(this.GraphAnimationStart, GraphAnimationSeconds * 1000));
        }


        /*********
        ** Private methods: layout
        *********/
        /// <summary>The view buttons, on the header's control row.</summary>
        private IEnumerable<(Rectangle Bounds, IncomeView View)> GetIncomeViewButtons()
        {
            int x = this.xPositionOnScreen + 32;
            int y = this.yPositionOnScreen + 16 + (2 * TabRowHeight);
            foreach (IncomeView view in Enum.GetValues<IncomeView>())
            {
                int width = MeasureButton(this.Translations.Get("income.view-" + view.ToString().ToLowerInvariant())) - 22;
                yield return (new Rectangle(x, y, width, 44), view);
                x += width + 10;
            }
        }

        /// <summary>The plot area and the legend beside it.</summary>
        private (Rectangle Plot, Rectangle Legend) GetGraphLayout()
        {
            Rectangle grid = this.GetGridBounds();
            Rectangle legend = new(grid.Right - 250, grid.Y, 250, grid.Height);
            Rectangle plot = new(grid.X + 84, grid.Y + 12, grid.Width - 84 - legend.Width - 24, grid.Height - 12 - 36);
            return (plot, legend);
        }


        /*********
        ** Private methods: input
        *********/
        /// <summary>Handles a click on the Income tab.</summary>
        private void ReceiveClickOnIncome(int x, int y)
        {
            foreach ((Rectangle bounds, IncomeView view) in this.GetIncomeViewButtons())
            {
                if (!bounds.Contains(x, y))
                    continue;

                if (view != this.IncomeMode)
                {
                    this.IncomeHighlightFrom = this.GetIncomeViewHighlight();
                    this.IncomeViewDirection = Math.Sign((int)view - (int)this.IncomeMode);
                    this.IncomeViewChangedAt = DateTime.UtcNow;
                }

                this.IncomeMode = view;
                this.ScrollOffset = 0;
                this.RestartGraphAnimation();
                Game1.playSound("smallSelect");
                return;
            }

            foreach ((Rectangle bounds, string action, object value) in this.IncomeHotspots.ToList())
            {
                if (!bounds.Contains(x, y))
                    continue;

                this.DoIncomeAction(action, value);
                return;
            }
        }

        /// <summary>Carries out a click on one of the Income tab's controls.</summary>
        private void DoIncomeAction(string action, object value)
        {
            switch (action)
            {
                case "mode":
                    this.IncomeCumulative = !this.IncomeCumulative;
                    this.RestartGraphAnimation();
                    break;
                case "range":
                    this.IncomeRangeIndex = (int)value;
                    this.RestartGraphAnimation();
                    break;
                case "by-source":
                    this.IncomeBySource = !this.IncomeBySource;
                    this.RestartGraphAnimation();
                    break;
                case "scale":
                    this.IncomeLogScale = !this.IncomeLogScale;
                    this.RestartGraphAnimation();
                    break;
                case "toggle-source":
                    string name = (string)value;
                    if (!this.HiddenSources.Remove(name))
                        this.HiddenSources.Add(name);
                    break;
                case "add-expense":
                case "add-cost":
                    this.OpenExpenseInput(forItem: action == "add-cost");
                    return;
                case "remove-expense":
                    this.Expenses.Expenses.Remove((PlannedExpense)value);
                    this.Expenses.Save();
                    Game1.playSound("trashcan");
                    return;
                case "remove-cost":
                    this.Expenses.ItemCosts.Remove((string)value);
                    this.Expenses.Save();
                    Game1.playSound("trashcan");
                    return;
                case "gold-on-hand":
                    this.Expenses.CountGoldOnHand = !this.Expenses.CountGoldOnHand;
                    this.Expenses.Save();
                    break;
                case "ledger-day":
                    int day = (int)value;
                    if (!this.ExpandedLedgerDays.Remove(day))
                        this.ExpandedLedgerDays.Add(day);
                    break;
            }

            Game1.playSound("drumkit6");
        }

        /// <summary>Opens the window to add a planned expense or an item cost.</summary>
        private void OpenExpenseInput(bool forItem)
        {
            TerminalMenu parent = this;
            ExpensePlan plan = this.Expenses;
            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new ExpenseInputMenu(
                this.Translations,
                forItem,
                forItem ? this.GetCostCandidates() : null,
                onSave: (key, amount) =>
                {
                    if (forItem)
                        plan.ItemCosts[key] = amount;
                    else
                        plan.Expenses.Add(new PlannedExpense { Name = key, Amount = amount });
                    plan.Save();
                },
                onClose: () => Game1.activeClickableMenu = parent
            );
        }

        /// <summary>Sets the hover text for the Income tab.</summary>
        private void PerformHoverOnIncome(int x, int y)
        {
            foreach ((Rectangle bounds, string action, object value) in this.IncomeHotspots)
            {
                if (!bounds.Contains(x, y))
                    continue;

                this.HoverText = action switch
                {
                    "toggle-source" => this.DescribeSource((string)value),
                    "by-source" => this.Translations.Get("income.by-source-hint"),
                    "scale" => this.Translations.Get("income.scale-hint"),
                    "gold-on-hand" => this.Translations.Get("expense.gold-on-hand-hint"),
                    "ledger-day" => this.Translations.Get("ledger.expand-hint"),
                    _ => ""
                };
                return;
            }

            if (this.IncomeMode is IncomeView.Forecast or IncomeView.History)
            {
                (Rectangle plot, _) = this.GetGraphLayout();
                if (plot.Contains(x, y))
                    this.HoverTooltip = this.DescribeGraphDay(x, plot);
            }
        }

        /// <summary>A source's income, costs and margin, for its legend entry.</summary>
        private string DescribeSource(string name)
        {
            GraphSeries series = (this.IncomeMode == IncomeView.History ? this.GetHistorySeries(IncomeRanges[this.IncomeRangeIndex]) : this.GetForecastSeries(1))
                .FirstOrDefault(entry => entry.Name == name);
            if (series == null)
                return "";

            string toggle = this.Translations.Get(series.Hidden ? "income.show-hint" : "income.hide-hint");
            if (this.IncomeMode == IncomeView.History || series.CostPerDay <= 0)
                return $"{name}\n{this.Translations.Get("income.source-rate", new { gold = Selling.Gold(series.PerDay) })}\n{toggle}";

            double profit = series.PerDay - series.CostPerDay;
            double margin = series.PerDay > 0 ? profit / series.PerDay * 100 : 0;
            return $"{name}\n{this.Translations.Get("income.source-margin", new { income = Selling.Gold(series.PerDay), cost = Selling.Gold(series.CostPerDay), profit = Selling.Gold(profit), margin = $"{margin:0}" })}\n{toggle}";
        }

        /// <summary>A day's figures, for hovering the graph.</summary>
        /// <remarks>
        /// Coloured as the graph is: each source keyed and named in its legend colour, and each amount by the
        /// same tiers as the headline figures, so the tooltip reads at a glance.
        /// </remarks>
        private RichTooltip DescribeGraphDay(int mouseX, Rectangle plot)
        {
            int days = IncomeRanges[this.IncomeRangeIndex];
            int day = Math.Clamp((int)((mouseX - plot.X) / (double)plot.Width * days), 0, days - 1);
            List<GraphSeries> series = (this.IncomeMode == IncomeView.History ? this.GetHistorySeries(days) : this.GetForecastSeries(days)).Where(entry => !entry.Hidden).ToList();

            double total = series.Sum(entry => entry.Daily[day]);
            double running = series.Sum(entry => entry.Daily.Take(day + 1).Sum());

            RichTooltip tooltip = new RichTooltip()
                .Line().Add(this.GraphDayLabel(day, days) + ": ").Add(Selling.Gold(total), MoneyColours.ForDaily(total));

            if (this.IncomeBySource)
            {
                foreach (GraphSeries entry in series.Where(entry => entry.Daily[day] > 0).OrderByDescending(entry => entry.Daily[day]))
                {
                    tooltip.Line(key: entry.Colour, icon: entry.Icon, indent: 8)
                        .Add(entry.Name + ": ", UiTheme.Legible(entry.Colour))
                        .Add(Selling.Gold(entry.Daily[day]), MoneyColours.ForDaily(entry.Daily[day]));
                }
            }

            string runningLabel = this.Translations.Get("income.running", new { gold = "" }).ToString().TrimEnd();
            tooltip.Line().Add(runningLabel + " ").Add(Selling.Gold(running), MoneyColours.ForWorth(running));
            return tooltip;
        }


        /*********
        ** Private methods: drawing
        *********/
        /// <summary>Draws the Income tab.</summary>
        private void DrawIncomeTab(SpriteBatch b)
        {
            this.IncomeHotspots.Clear();
            Rectangle grid = this.GetGridBounds();

            // The view buttons, on the header's control row, with the active one's highlight gliding to it the way
            // the tabs' does.
            foreach ((Rectangle bounds, IncomeView _) in this.GetIncomeViewButtons())
                DrawPlainButton(b, bounds, null, active: false);

            Rectangle highlight = this.GetIncomeViewHighlight();
            if (!highlight.IsEmpty)
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), highlight.X, highlight.Y, highlight.Width, highlight.Height, Color.Wheat, 3f, drawShadow: false);

            foreach ((Rectangle bounds, IncomeView view) in this.GetIncomeViewButtons())
                DrawPlainButton(b, bounds, this.Translations.Get("income.view-" + view.ToString().ToLowerInvariant()), active: false, drawBox: false);

            // The headline figures, on the header's search row, coloured by how good they are.
            if (this.Network != null)
            {
                ShippingSummary summary = this.GetShippingSummary();
                double profit = this.GetDailyProfit();
                Vector2 position = new(grid.X, this.yPositionOnScreen + 16 + (2 * TabRowHeight) + 62);
                position = DrawPart(b, this.Translations.Get("income.networth-label"), position, Game1.textColor);
                position = DrawPart(b, Selling.Gold(summary.NetWorth), position, MoneyColours.ForWorth(summary.NetWorth));
                position = DrawPart(b, "   " + this.Translations.Get("income.income-label"), position, Game1.textColor);
                position = DrawPart(b, this.Translations.Get("income.a-day", new { gold = Selling.Gold(summary.DailyIncome) }), position, MoneyColours.ForDaily(summary.DailyIncome));
                if (this.Expenses.ItemCosts.Count > 0)
                {
                    position = DrawPart(b, "   " + this.Translations.Get("income.profit-label"), position, Game1.textColor);
                    DrawPart(b, this.Translations.Get("income.a-day", new { gold = Selling.Gold(profit) }), position, profit < 0 ? UiTheme.Bad : MoneyColours.ForDaily(profit));
                }
            }

            if (this.Network == null)
            {
                this.DrawCentredMessage(b, grid, this.NotConnectedText);
                return;
            }

            // The view itself slides in and fades up, as a tab does; the headline figures above it stay put.
            float transition = UiAnimation.EaseOut(UiAnimation.Progress(this.IncomeViewChangedAt, TabTransitionMs));
            Rectangle content = this.GetContentBounds();
            Rectangle viewArea = new(content.X, grid.Y - 8, content.Width, content.Bottom - (grid.Y - 8));
            bool sliding = transition < 1f && UiBatch.Push(b, viewArea, new Vector2((1f - transition) * TabSlideDistance * this.IncomeViewDirection, 0));
            try
            {
                switch (this.IncomeMode)
                {
                    case IncomeView.Forecast:
                    case IncomeView.History:
                        this.DrawIncomeGraphView(b);
                        break;
                    case IncomeView.Expenses:
                        this.DrawExpensesView(b);
                        break;
                    case IncomeView.Ledger:
                        this.DrawLedgerView(b);
                        break;
                }
            }
            finally
            {
                if (sliding)
                    UiBatch.Pop(b);
            }

            if (transition < 1f)
                this.DrawPanelOver(b, viewArea, 1f - transition);
        }

        /// <summary>Where the active view's highlight is drawn: gliding from the last view to this one.</summary>
        private Rectangle GetIncomeViewHighlight()
        {
            Rectangle target = this.GetIncomeViewButtons().FirstOrDefault(button => button.View == this.IncomeMode).Bounds;
            float t = UiAnimation.EaseOut(UiAnimation.Progress(this.IncomeViewChangedAt, TabTransitionMs));
            if (t >= 1f || this.IncomeHighlightFrom.IsEmpty)
                return target;

            return new Rectangle(
                (int)MathHelper.Lerp(this.IncomeHighlightFrom.X, target.X, t),
                (int)MathHelper.Lerp(this.IncomeHighlightFrom.Y, target.Y, t),
                (int)MathHelper.Lerp(this.IncomeHighlightFrom.Width, target.Width, t),
                (int)MathHelper.Lerp(this.IncomeHighlightFrom.Height, target.Height, t)
            );
        }

        /// <summary>Draws text and returns where the next part goes.</summary>
        private static Vector2 DrawPart(SpriteBatch b, string text, Vector2 position, Color colour)
        {
            Utility.drawTextWithShadow(b, text, Game1.smallFont, position, colour);
            return position + new Vector2(Game1.smallFont.MeasureString(text).X, 0);
        }

        /// <summary>Draws a button and makes it clickable.</summary>
        private void DrawIncomeButton(SpriteBatch b, Rectangle bounds, string label, bool active, string action, object value = null)
        {
            DrawPlainButton(b, bounds, label, active);
            this.IncomeHotspots.Add((bounds, action, value));
        }

        /// <summary>Draws a button with its label centred; lit when active.</summary>
        private static void DrawPlainButton(SpriteBatch b, Rectangle bounds, string label, bool active, bool drawBox = true)
        {
            if (drawBox)
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, active ? Color.Wheat : Color.White, 3f, drawShadow: false);
            if (string.IsNullOrEmpty(label))
                return;

            Vector2 size = Game1.smallFont.MeasureString(label);
            Marquee.Draw(b, label, Game1.smallFont, new Vector2(bounds.X + Math.Max(12, (bounds.Width - size.X) / 2), bounds.Center.Y - (size.Y / 2)), bounds.Width - 24, Game1.textColor);
        }

        /// <summary>Draws the forecast or history graph, its legend, and its options.</summary>
        private void DrawIncomeGraphView(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();
            (Rectangle plot, Rectangle legend) = this.GetGraphLayout();
            int days = IncomeRanges[this.IncomeRangeIndex];
            bool history = this.IncomeMode == IncomeView.History;
            List<GraphSeries> series = history ? this.GetHistorySeries(days) : this.GetForecastSeries(days);

            this.DrawGraph(b, plot, series, days);

            // Linear or log, at the foot of the legend: the options row below is full.
            Rectangle scale = new(legend.X, legend.Bottom - 40, legend.Width, 40);
            this.DrawIncomeButton(b, scale, this.Translations.Get(this.IncomeLogScale ? "income.scale-log" : "income.scale-linear"), this.IncomeLogScale, "scale");
            this.DrawLegend(b, new Rectangle(legend.X, legend.Y, legend.Width, legend.Height - 48), series);

            // Options below the graph.
            int y = grid.Bottom + 4;
            int x = grid.X;
            string mode = this.Translations.Get(this.IncomeCumulative ? "income.mode-total" : "income.mode-daily");
            int modeWidth = MeasureButton(mode) - 22;
            this.DrawIncomeButton(b, new Rectangle(x, y, modeWidth, 40), mode, false, "mode");
            x += modeWidth + 16;

            for (int i = 0; i < IncomeRanges.Length; i++)
            {
                string label = this.Translations.Get("income.range", new { days = IncomeRanges[i] });
                int width = MeasureButton(label) - 22;
                this.DrawIncomeButton(b, new Rectangle(x, y, width, 40), label, i == this.IncomeRangeIndex, "range", i);
                x += width + 8;
            }
            x += 8;

            string bySource = this.Translations.Get("income.by-source");
            int bySourceWidth = MeasureButton(bySource) + 10;
            Rectangle toggle = new(x, y, bySourceWidth, 40);
            this.DrawIncomeButton(b, toggle, "", this.IncomeBySource, "by-source");
            b.Draw(Game1.mouseCursors, new Rectangle(toggle.X + 8, toggle.Center.Y - 14, 28, 28), this.IncomeBySource ? OptionsCheckbox.sourceRectChecked : OptionsCheckbox.sourceRectUnchecked, Color.White);
            Utility.drawTextWithShadow(b, bySource, Game1.smallFont, new Vector2(toggle.X + 44, toggle.Y + 6), Game1.textColor);

            string hint = this.Translations.Get(history ? "income.history-hint" : "income.forecast-hint");
            Marquee.DrawWrapped(b, hint, Game1.smallFont, new Vector2(grid.X, grid.Bottom + 52), grid.Width, Game1.textColor * 0.6f);
        }

        /// <summary>Draws the graph itself, drawing in from nothing when first shown.</summary>
        private void DrawGraph(SpriteBatch b, Rectangle plot, List<GraphSeries> all, int days)
        {
            b.Draw(Game1.staminaRect, plot, new Color(60, 44, 32) * 0.1f);
            this.DrawSeasons(b, plot, days);
            List<GraphSeries> series = all.Where(entry => !entry.Hidden).ToList();
            float progress = this.GraphProgress();

            // What's plotted: each day's gold, or the running total.
            List<double[]> values = series.Select(entry => this.IncomeCumulative ? RunningTotal(entry.Daily) : entry.Daily).ToList();
            double[] totals = Enumerable.Range(0, days).Select(day => values.Sum(value => value[day])).ToArray();
            double highest = this.IncomeBySource && this.IncomeCumulative
                ? values.SelectMany(value => value).DefaultIfEmpty(0).Max()
                : totals.DefaultIfEmpty(0).Max();
            bool log = this.IncomeLogScale;

            // A log axis runs to the next power of ten; a linear one to the next round number.
            double top = highest <= 0 ? 0 : log ? Math.Pow(10, Math.Max(1, Math.Ceiling(Math.Log10(highest)))) : NiceCeiling(highest);

            // Where a value sits up the axis, from 0 to 1. On a log axis 0g still sits at the bottom.
            double Scale(double value) => value <= 0 || top <= 0
                ? 0
                : log ? Math.Log10(1 + value) / Math.Log10(1 + top) : value / top;

            // Gridlines, gold up the side: quarters on a linear axis, each power of ten on a log one.
            List<double> marks = log && top > 0
                ? new[] { 0.0 }.Concat(Enumerable.Range(0, (int)Math.Round(Math.Log10(top)) + 1).Select(power => Math.Pow(10, power))).ToList()
                : Enumerable.Range(0, 5).Select(i => top * i / 4).ToList();
            int lastLabelY = int.MaxValue;
            foreach (double mark in marks)
            {
                int y = plot.Bottom - (int)(plot.Height * Scale(mark));
                b.Draw(Game1.staminaRect, new Rectangle(plot.X, y, plot.Width, 1), Game1.textColor * (mark <= 0 ? 0.6f : 0.15f));

                string label = NumberFormat.Abbreviate((long)mark) + "g";
                Vector2 size = Game1.smallFont.MeasureString(label);
                if (mark > 0 && lastLabelY - y < size.Y)
                    continue; // too close to the label below it to read
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(plot.X - size.X - 10, y - (size.Y / 2)), Game1.textColor * 0.7f);
                lastLabelY = y;
            }
            b.Draw(Game1.staminaRect, new Rectangle(plot.X, plot.Y, 1, plot.Height), Game1.textColor * 0.6f);

            // Days along the bottom, by day of the month, as far apart as they need to be not to touch.
            float labelSlot = plot.Width / (float)days;
            int widest = (int)Game1.smallFont.MeasureString("28").X + 14;
            int step = new[] { 1, 2, 4, 7, 14, 28 }.FirstOrDefault(candidate => candidate * labelSlot >= widest);
            if (step == 0)
                step = 28;
            for (int day = 0; day < days; day++)
            {
                (int season, int dayOfMonth, int _) = this.GraphDate(day, days);
                if (season < 0 || ((dayOfMonth - 1) % step != 0 && step != 1))
                    continue;

                string label = dayOfMonth.ToString();
                float x = plot.X + ((day + 0.5f) * labelSlot);
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(x - (size.X / 2), plot.Bottom + 4), Game1.textColor * 0.7f);
            }

            if (top <= 0)
            {
                string none = this.Translations.Get(this.IncomeMode == IncomeView.History ? "income.history-none" : "income.none");
                Vector2 size = Game1.smallFont.MeasureString(none);
                Utility.drawTextWithShadow(b, none, Game1.smallFont, new Vector2(plot.Center.X - (size.X / 2), plot.Center.Y - (size.Y / 2)), Game1.textColor * 0.6f);
                return;
            }

            float slot = plot.Width / (float)days;
            int barWidth = Math.Max(1, (int)(slot * 0.72f));
            float Height(double value) => (float)(Scale(value) * plot.Height * progress);

            // Lines: the running total, one per source when broken down. They draw across as the graph comes in.
            if (this.IncomeCumulative)
            {
                // How far along the line has drawn, in days: smooth, so a short range doesn't step a day at a time.
                float reach = progress * (days - 1);
                if (this.IncomeBySource)
                {
                    for (int s = 0; s < series.Count; s++)
                        this.DrawSeriesLine(b, plot, values[s], Scale, slot, reach, _ => series[s].Colour);
                }
                else
                    this.DrawSeriesLine(b, plot, totals, Scale, slot, reach, value => MoneyColours.ForWorth(value));
                return;
            }

            // Bars: stacked by source, or one per day in the colour of how good a day it was.
            for (int day = 0; day < days; day++)
            {
                int barX = plot.X + (int)(day * slot) + (int)((slot - barWidth) / 2);
                if (this.IncomeBySource)
                {
                    // Each segment spans from the stack below it to the stack including it, so on a log axis the
                    // whole bar still reaches the day's total.
                    double below = 0;
                    for (int s = 0; s < series.Count; s++)
                    {
                        double value = values[s][day];
                        if (value <= 0)
                            continue;

                        float from = Height(below);
                        float to = Height(below + value);
                        below += value;
                        if (to - from <= 0)
                            continue;
                        b.Draw(Game1.staminaRect, new Rectangle(barX, (int)(plot.Bottom - to), barWidth, Math.Max(1, (int)Math.Ceiling(to - from))), series[s].Colour);
                    }
                }
                else
                {
                    float height = Height(totals[day]);
                    if (height > 0)
                        b.Draw(Game1.staminaRect, new Rectangle(barX, (int)(plot.Bottom - height), barWidth, (int)Math.Ceiling(height)), MoneyColours.ForDaily(totals[day]));
                }
            }
        }

        /// <summary>Shades the plot by season, each band named at its top.</summary>
        private void DrawSeasons(SpriteBatch b, Rectangle plot, int days)
        {
            float slot = plot.Width / (float)days;
            int start = 0;
            while (start < days)
            {
                int season = this.GraphDate(start, days).Season;
                int end = start;
                while (end < days && this.GraphDate(end, days).Season == season)
                    end++;

                // Before the farm began there's no season to shade.
                if (season < 0)
                {
                    start = end;
                    continue;
                }

                int x = plot.X + (int)(start * slot);
                int width = (int)(end * slot) - (int)(start * slot);
                b.Draw(Game1.staminaRect, new Rectangle(x, plot.Y, width, plot.Height), SeasonColours[season] * 0.22f);

                string name = Utility.getSeasonNameFromNumber(season);
                Vector2 size = Game1.smallFont.MeasureString(name) * 0.75f;
                if (size.X + 12 <= width)
                    b.DrawString(Game1.smallFont, name, new Vector2(x + 6, plot.Y + 4), SeasonColours[season] * 0.9f, 0f, Vector2.Zero, 0.75f, SpriteEffects.None, 1f);

                start = end;
            }
        }

        /// <summary>The colour of each season's band: spring green, summer yellow, fall orange, winter blue.</summary>
        private static readonly Color[] SeasonColours =
        {
            new(70, 150, 60),
            new(200, 150, 20),
            new(190, 90, 30),
            new(60, 120, 190)
        };

        /// <summary>The date of a day on the graph: forecast days run forward from today, history days back from yesterday.</summary>
        /// <returns>The season, day and year; a season of -1 for a day before the farm began.</returns>
        private (int Season, int DayOfMonth, int Year) GraphDate(int day, int days)
        {
            int total = this.IncomeMode == IncomeView.History
                ? Game1.Date.TotalDays - (days - day)
                : Game1.Date.TotalDays + day;
            if (total < 0)
                return (-1, 0, 0);
            return ((total / 28) % 4, (total % 28) + 1, (total / 112) + 1);
        }

        /// <summary>Draws one line across the plot, up to a day, coloured segment by segment.</summary>
        private void DrawSeriesLine(SpriteBatch b, Rectangle plot, double[] values, Func<double, double> scale, float slot, float reach, Func<double, Color> colour)
        {
            if (values.Length == 0)
                return;

            Vector2 PointAt(int day) => new(plot.X + ((day + 0.5f) * slot), plot.Bottom - (float)(scale(values[day]) * plot.Height));

            // Whole segments up to the last day reached, then part of the next, ending wherever the line has got to.
            int whole = Math.Min((int)reach, values.Length - 1);
            for (int day = 1; day <= whole; day++)
                DrawLine(b, PointAt(day - 1), PointAt(day), colour(values[day]), 3);

            float part = reach - whole;
            if (part > 0 && whole + 1 < values.Length)
            {
                Vector2 from = PointAt(whole);
                Vector2 to = Vector2.Lerp(from, PointAt(whole + 1), part);
                double value = values[whole] + ((values[whole + 1] - values[whole]) * part);
                DrawLine(b, from, to, colour(value), 3);
            }
        }

        /// <summary>Draws the legend: sources with their colours and switches, or the colour key.</summary>
        private void DrawLegend(SpriteBatch b, Rectangle legend, List<GraphSeries> series)
        {
            int y = legend.Y;

            if (!this.IncomeBySource)
            {
                Utility.drawTextWithShadow(b, this.Translations.Get("income.legend-tiers"), Game1.smallFont, new Vector2(legend.X, y), Game1.textColor);
                y += 36;
                double[] steps = this.IncomeCumulative ? MoneyColours.WorthSteps : MoneyColours.DailySteps;
                for (int tier = 0; tier < MoneyColours.Tiers.Length; tier++)
                {
                    b.Draw(Game1.staminaRect, new Rectangle(legend.X, y + 6, 20, 20), MoneyColours.Tiers[tier]);
                    string label = tier == 0
                        ? this.Translations.Get("income.tier-under", new { gold = Selling.Gold(steps[0]) })
                        : this.Translations.Get("income.tier-over", new { gold = Selling.Gold(steps[tier - 1]) });
                    Marquee.Draw(b, label, Game1.smallFont, new Vector2(legend.X + 30, y + 2), legend.Width - 30, Game1.textColor * 0.85f);
                    y += 34;
                }
                return;
            }

            Utility.drawTextWithShadow(b, this.Translations.Get("income.legend-sources"), Game1.smallFont, new Vector2(legend.X, y), Game1.textColor);
            y += 36;

            if (series.Count == 0)
            {
                Marquee.Draw(b, this.Translations.Get("income.none-short"), Game1.smallFont, new Vector2(legend.X, y), legend.Width, Game1.textColor * 0.6f);
                return;
            }

            foreach (GraphSeries entry in series)
            {
                if (y > legend.Bottom - 34)
                {
                    Marquee.Draw(b, this.Translations.Get("income.legend-more", new { count = series.Count - series.IndexOf(entry) }), Game1.smallFont, new Vector2(legend.X, y), legend.Width, Game1.textColor * 0.5f);
                    break;
                }

                Rectangle row = new(legend.X, y, legend.Width, 32);
                this.IncomeHotspots.Add((row, "toggle-source", entry.Name));
                if (row.Contains(Game1.getMouseX(), Game1.getMouseY()))
                    b.Draw(Game1.staminaRect, row, Color.Wheat * 0.4f);

                // A filled swatch when shown, an outline when hidden.
                Rectangle swatch = new(row.X + 2, row.Y + 6, 20, 20);
                if (entry.Hidden)
                {
                    b.Draw(Game1.staminaRect, new Rectangle(swatch.X, swatch.Y, swatch.Width, 2), entry.Colour);
                    b.Draw(Game1.staminaRect, new Rectangle(swatch.X, swatch.Bottom - 2, swatch.Width, 2), entry.Colour);
                    b.Draw(Game1.staminaRect, new Rectangle(swatch.X, swatch.Y, 2, swatch.Height), entry.Colour);
                    b.Draw(Game1.staminaRect, new Rectangle(swatch.Right - 2, swatch.Y, 2, swatch.Height), entry.Colour);
                }
                else
                    b.Draw(Game1.staminaRect, swatch, entry.Colour);

                Color text = entry.Hidden ? Game1.textColor * 0.4f : Game1.textColor;
                if (entry.Icon != null)
                    ItemIcon.Draw(b, entry.Icon, new Rectangle(row.X + 28, row.Y, 28, 28), entry.Hidden ? 0.4f : 1f);
                Marquee.Draw(b, entry.Name, Game1.smallFont, new Vector2(row.X + (entry.Icon != null ? 60 : 30), row.Y + 2), row.Width - (entry.Icon != null ? 60 : 30), text);
                y += 34;
            }
        }

        /// <summary>Draws the expense planner: what's being saved for, what inputs cost, and when the savings are met.</summary>
        private void DrawExpensesView(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();
            ExpensePlan plan = this.Expenses;
            int half = (grid.Width - 24) / 2;
            Rectangle left = new(grid.X, grid.Y, half, grid.Height);
            Rectangle right = new(grid.X + half + 24, grid.Y, half, grid.Height);

            // What's being saved for.
            this.DrawListHeader(b, left, this.Translations.Get("expense.planned"), "add-expense");
            int y = left.Y + 52;
            foreach (PlannedExpense expense in plan.Expenses.ToList())
            {
                if (y > left.Bottom - 36)
                    break;

                Rectangle remove = new(left.Right - 36, y, 32, 32);
                this.DrawRemoveButton(b, remove, "remove-expense", expense);
                string amount = Selling.Gold(expense.Amount);
                Vector2 amountSize = Game1.smallFont.MeasureString(amount);
                Utility.drawTextWithShadow(b, amount, Game1.smallFont, new Vector2(remove.X - 10 - amountSize.X, y + 2), Game1.textColor);
                Marquee.Draw(b, expense.Name, Game1.smallFont, new Vector2(left.X + 4, y + 2), (int)(remove.X - 20 - amountSize.X - left.X), Game1.textColor);
                y += 38;
            }
            if (plan.Expenses.Count == 0)
                Marquee.Draw(b, this.Translations.Get("expense.none"), Game1.smallFont, new Vector2(left.X + 4, y), left.Width - 8, Game1.textColor * 0.5f);

            // What inputs cost.
            this.DrawListHeader(b, right, this.Translations.Get("expense.costs"), "add-cost");
            y = right.Y + 52;
            foreach ((string itemId, long cost) in plan.ItemCosts.OrderBy(pair => StockId.GetDisplayName(pair.Key)).ToList())
            {
                if (y > right.Bottom - 36)
                    break;

                Rectangle remove = new(right.Right - 36, y, 32, 32);
                this.DrawRemoveButton(b, remove, "remove-cost", itemId);
                string amount = this.Translations.Get("expense.each", new { gold = Selling.Gold(cost) });
                Vector2 amountSize = Game1.smallFont.MeasureString(amount);
                Utility.drawTextWithShadow(b, amount, Game1.smallFont, new Vector2(remove.X - 10 - amountSize.X, y + 2), Game1.textColor);
                ItemIcon.Draw(b, this.GetJobIcon(itemId, 0), new Rectangle(right.X + 2, y, 30, 30), 1f, showQuality: false);
                Marquee.Draw(b, StockId.GetDisplayName(itemId), Game1.smallFont, new Vector2(right.X + 40, y + 2), (int)(remove.X - 50 - amountSize.X - right.X), Game1.textColor);
                y += 38;
            }
            if (plan.ItemCosts.Count == 0)
                Marquee.Draw(b, this.Translations.Get("expense.no-costs"), Game1.smallFont, new Vector2(right.X + 4, y), right.Width - 8, Game1.textColor * 0.5f);

            // Below: the total, whether gold on hand counts, and when it'll all be paid for.
            int line = grid.Bottom + 4;
            Rectangle check = new(grid.X, line, MeasureButton(this.Translations.Get("expense.gold-on-hand", new { gold = Selling.Gold(Game1.player.Money) })) + 20, 40);
            this.DrawIncomeButton(b, check, "", plan.CountGoldOnHand, "gold-on-hand");
            b.Draw(Game1.mouseCursors, new Rectangle(check.X + 8, check.Center.Y - 14, 28, 28), plan.CountGoldOnHand ? OptionsCheckbox.sourceRectChecked : OptionsCheckbox.sourceRectUnchecked, Color.White);
            Utility.drawTextWithShadow(b, this.Translations.Get("expense.gold-on-hand", new { gold = Selling.Gold(Game1.player.Money) }), Game1.smallFont, new Vector2(check.X + 44, check.Y + 6), Game1.textColor);

            string outlook;
            Color colour;
            int? days = this.DaysToMeetExpenses(out long needed);
            if (plan.Expenses.Count == 0)
            {
                outlook = this.Translations.Get("expense.outlook-none");
                colour = Game1.textColor * 0.6f;
            }
            else if (days == 0)
            {
                outlook = this.Translations.Get("expense.outlook-covered", new { total = Selling.Gold(plan.Total) });
                colour = UiTheme.Good;
            }
            else if (days != null)
            {
                outlook = this.Translations.Get("expense.outlook-days", new { total = Selling.Gold(plan.Total), needed = Selling.Gold(needed), days });
                colour = MoneyColours.ForDaily(this.GetDailyProfit());
            }
            else
            {
                outlook = this.Translations.Get("expense.outlook-never", new { total = Selling.Gold(plan.Total), needed = Selling.Gold(needed), days = ExpenseHorizon });
                colour = UiTheme.Bad;
            }
            Marquee.DrawWrapped(b, outlook, Game1.smallFont, new Vector2(grid.X, grid.Bottom + 52), grid.Width, colour);
        }

        /// <summary>A list's heading, with its Add button.</summary>
        private void DrawListHeader(SpriteBatch b, Rectangle area, string title, string addAction)
        {
            b.Draw(Game1.staminaRect, area, new Color(60, 44, 32) * 0.08f);
            Utility.drawTextWithShadow(b, title, Game1.smallFont, new Vector2(area.X + 4, area.Y + 8), Game1.textColor);

            string add = this.Translations.Get("expense.add");
            int width = MeasureButton(add) - 22;
            this.DrawIncomeButton(b, new Rectangle(area.Right - width, area.Y + 2, width, 40), add, false, addAction);
        }

        /// <summary>A small remove button.</summary>
        private void DrawRemoveButton(SpriteBatch b, Rectangle bounds, string action, object value)
        {
            this.IncomeHotspots.Add((bounds, action, value));
            b.Draw(Game1.mouseCursors, bounds, new Rectangle(337, 494, 12, 12), Color.White * (bounds.Contains(Game1.getMouseX(), Game1.getMouseY()) ? 1f : 0.7f));
        }

        /// <summary>Draws the ledger: a line per day, opened to show what was shipped.</summary>
        private void DrawLedgerView(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();
            List<(LedgerDay Day, LedgerItem Item)> lines = this.GetLedgerLines();

            if (lines.Count == 0)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("ledger.none"));
                return;
            }

            int visible = grid.Height / LedgerLineHeight;
            for (int i = 0; i < visible; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= lines.Count)
                    break;

                (LedgerDay day, LedgerItem item) = lines[index];
                int y = grid.Y + (i * LedgerLineHeight);

                if (item == null)
                {
                    // A day: its date, then shipping, other income, and the total.
                    Rectangle row = new(grid.X, y, grid.Width - 16, LedgerLineHeight - 4);
                    this.IncomeHotspots.Add((row, "ledger-day", day.TotalDays));
                    b.Draw(Game1.staminaRect, row, (row.Contains(Game1.getMouseX(), Game1.getMouseY()) ? Color.Wheat : new Color(60, 44, 32)) * 0.12f);

                    string arrow = this.ExpandedLedgerDays.Contains(day.TotalDays) ? "-" : "+";
                    Utility.drawTextWithShadow(b, arrow, Game1.smallFont, new Vector2(row.X + 8, y + 4), Game1.textColor);
                    Utility.drawTextWithShadow(b, ShippingLedger.FormatDate(day), Game1.smallFont, new Vector2(row.X + 32, y + 4), Game1.textColor);

                    Vector2 position = new(row.X + 300, y + 4);
                    position = DrawPart(b, this.Translations.Get("ledger.shipping"), position, Game1.textColor * 0.7f);
                    position = DrawPart(b, Selling.Gold(day.Shipping) + "   ", position, MoneyColours.ForDaily(day.Shipping));
                    position = DrawPart(b, this.Translations.Get("ledger.other"), position, Game1.textColor * 0.7f);
                    DrawPart(b, Selling.Gold(day.Other), position, MoneyColours.ForDaily(day.Other));

                    string total = Selling.Gold(day.Total);
                    Vector2 totalSize = Game1.smallFont.MeasureString(total);
                    Utility.drawTextWithShadow(b, total, Game1.smallFont, new Vector2(row.Right - totalSize.X - 8, y + 4), MoneyColours.ForDaily(day.Total));
                }
                else
                {
                    // An item shipped that day.
                    Item icon = this.GetJobIcon(item.ItemId, item.Quality);
                    ItemIcon.Draw(b, icon, new Rectangle(grid.X + 40, y + 2, 32, 32));
                    Marquee.Draw(b, $"{NumberFormat.Full(item.Count)}x {item.Name}", Game1.smallFont, new Vector2(grid.X + 82, y + 4), 440, Game1.textColor * 0.85f);
                    string gold = Selling.Gold(item.Gold);
                    Vector2 size = Game1.smallFont.MeasureString(gold);
                    Utility.drawTextWithShadow(b, gold, Game1.smallFont, new Vector2(grid.Right - 24 - size.X, y + 4), Game1.textColor * 0.85f);
                }
            }

            this.DrawScrollbar(b, grid, visible, lines.Count);

            // Below: recent totals.
            IReadOnlyList<LedgerDay> days = this.Jobs?.Ledger?.Days ?? Array.Empty<LedgerDay>();
            int today = Game1.Date.TotalDays;
            long week = days.Where(day => today - day.TotalDays <= 7).Sum(day => day.Total);
            long month = days.Where(day => today - day.TotalDays <= 28).Sum(day => day.Total);
            LedgerDay best = days.OrderByDescending(day => day.Total).FirstOrDefault();

            Vector2 summary = new(grid.X, grid.Bottom + 10);
            summary = DrawPart(b, this.Translations.Get("ledger.week"), summary, Game1.textColor);
            summary = DrawPart(b, Selling.Gold(week) + "   ", summary, MoneyColours.ForDaily(week / 7.0));
            summary = DrawPart(b, this.Translations.Get("ledger.month"), summary, Game1.textColor);
            DrawPart(b, Selling.Gold(month), summary, MoneyColours.ForDaily(month / 28.0));

            // The best day on a line of its own.
            if (best != null)
            {
                Vector2 bestLine = DrawPart(b, this.Translations.Get("ledger.best-label"), new Vector2(grid.X, grid.Bottom + 40), Game1.textColor);
                DrawPart(b, $"{ShippingLedger.FormatDate(best)}, {Selling.Gold(best.Total)}", bestLine, MoneyColours.ForDaily(best.Total));
            }
            Marquee.DrawWrapped(b, this.Translations.Get("ledger.hint"), Game1.smallFont, new Vector2(grid.X, grid.Bottom + 72), grid.Width, Game1.textColor * 0.55f, maxLines: 1);
        }

        /// <summary>The ledger as lines: each day, newest first, with its items under it when opened.</summary>
        private List<(LedgerDay Day, LedgerItem Item)> GetLedgerLines()
        {
            List<(LedgerDay, LedgerItem)> lines = new();
            foreach (LedgerDay day in (this.Jobs?.Ledger?.Days ?? Array.Empty<LedgerDay>()).OrderByDescending(day => day.TotalDays))
            {
                lines.Add((day, null));
                if (this.ExpandedLedgerDays.Contains(day.TotalDays))
                    lines.AddRange(day.Items.Select(item => (day, item)));
            }
            return lines;
        }

        /// <summary>A day's label for hovering the graph: its date, and how far it is from today.</summary>
        private string GraphDayLabel(int day, int days)
        {
            (int season, int dayOfMonth, int _) = this.GraphDate(day, days);
            string date = season < 0 ? this.Translations.Get("income.before-start") : $"{Utility.getSeasonNameFromNumber(season)} {dayOfMonth}";

            string relative;
            if (this.IncomeMode == IncomeView.History)
            {
                int ago = days - day;
                relative = ago <= 1 ? this.Translations.Get("income.yesterday") : this.Translations.Get("income.days-ago", new { days = ago });
            }
            else
                relative = day == 0 ? this.Translations.Get("income.today") : this.Translations.Get("income.day", new { day });

            return $"{date} ({relative})";
        }

        /// <summary>The largest scroll offset for the Income tab: only the ledger scrolls.</summary>
        private int GetMaxIncomeScroll()
        {
            if (this.IncomeMode != IncomeView.Ledger)
                return 0;
            return Math.Max(0, this.GetLedgerLines().Count - (this.GetGridBounds().Height / LedgerLineHeight));
        }


        /*********
        ** Private methods: drawing helpers
        *********/
        /// <summary>Draws a straight line as a rotated strip.</summary>
        private static void DrawLine(SpriteBatch b, Vector2 from, Vector2 to, Color colour, int thickness)
        {
            Vector2 delta = to - from;
            float angle = (float)Math.Atan2(delta.Y, delta.X);
            // Positioned and sized in fractions of a pixel, so a line drawing itself in grows smoothly.
            b.Draw(Game1.staminaRect, from, null, colour, angle, new Vector2(0, 0.5f), new Vector2(delta.Length(), thickness), SpriteEffects.None, 0f);
        }

        private static double[] RunningTotal(double[] daily)
        {
            double[] total = new double[daily.Length];
            double sum = 0;
            for (int i = 0; i < daily.Length; i++)
                total[i] = sum += daily[i];
            return total;
        }

        /// <summary>A round number at or above a value, for the top of the axis: 1, 2 or 5 times a power of ten.</summary>
        private static double NiceCeiling(double value)
        {
            if (value <= 0)
                return 0;

            double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (double step in new[] { 1, 2, 5, 10 })
            {
                if (step * power >= value)
                    return step * power;
            }
            return 10 * power;
        }
    }
}
